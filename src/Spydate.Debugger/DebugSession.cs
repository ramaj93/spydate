using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;


namespace Spydate.Debugger;

/// <summary>What the debuggee is doing.</summary>
public enum DebugState
{
    NotStarted,
    Running,
    Stopped,
    Exited,
}

/// <summary>
/// Where a freshly launched process should first stop of its own accord.
///
/// The loader break arrives whatever anybody asked for — it is the moment the image is finally
/// mapped and breakpoints go in — so this decides what happens once it has: report it, let go of it,
/// or run on to a one-shot at the program's entry or the opened module's own entry point.
/// </summary>
public enum EntryStop
{
    /// <summary>Stop at the loader break, before the program's own code — dnSpy's "Create Process".</summary>
    LoaderBreak,

    /// <summary>Let go of the loader break the instant it arrives, and run on — "Don't break".</summary>
    DontBreak,

    /// <summary>Run on to the launched process's own entry point, wherever it starts.</summary>
    ProcessEntry,

    /// <summary>
    /// Run on to the entry point of the module being read — the opened DLL's own entry under a host,
    /// the process's entry when it is the main image. Native code has no static constructor, so
    /// "Module cctor or Entry Point" is the entry point here.
    /// </summary>
    ModuleEntry,
}

/// <summary>How a managed step should treat the calls it meets on the way to the next IL offset.</summary>
public enum IlStepKind
{
    /// <summary>Descend into a managed call, stopping at the callee's first line; step over native ones.</summary>
    Into,

    /// <summary>Stay in this method: step over every call, stopping at the next IL offset here.</summary>
    Over,

    /// <summary>Run until this method returns to its caller.</summary>
    Out,
}

/// <summary>Something the debuggee did, in the order it did it.</summary>
public sealed record DebugEvent(string Kind, string Text)
{
    /// <summary>Where it stopped, when it stopped.</summary>
    public ulong? Address { get; init; }
}

/// <summary>
/// A breakpoint, and the byte it is standing in for.
///
/// <paramref name="Address"/> is the <em>static</em> address — the one in the listing. It is
/// translated to where the module actually landed each time it is planted, rather than once when it
/// is set: a breakpoint is usually set before anything is running, when there is no load address to
/// translate against, and on a module that may be loaded and unloaded more than once in a run.
/// </summary>
public sealed record Breakpoint(ulong Address, byte Original)
{
    public bool Planted { get; init; }

    /// <summary>
    /// The module this is in, or null for the one the listing is about.
    ///
    /// Null keeps <see cref="Address"/> meaning a static address, which is what every breakpoint was
    /// before there could be more than one module in play. A name makes it an RVA in that module
    /// instead — because a static address cannot say which module is meant once several are of
    /// interest at once.
    /// </summary>
    public string? Module { get; init; }
}

/// <summary>A module the debuggee has loaded, and where it landed.</summary>
public sealed record LoadedModule(string Path, ulong Base)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// A thread in the debuggee.
///
/// <paramref name="StartAddress"/> is what it was made to go and do, which before it has run
/// anywhere is the only thing telling one thread from another — a list of bare ids says nothing
/// about which is the worker and which is the one waiting on a socket.
/// </summary>
public sealed record DebugThread(uint Id, ulong StartAddress);

/// <summary>
/// Bytes to hold over the running module, kept by RVA so they survive it loading at a fresh address.
///
/// <paramref name="Original"/> is what the file says is there, which is what a removal puts back —
/// not read from the process, because by the time one is removed the byte in the process is the
/// patch, and restoring that would restore nothing. Same length as <paramref name="Bytes"/>, which
/// is the rule the whole of patching rests on: nothing moves, so no address anyone wrote down shifts.
/// </summary>
public sealed record LivePatch(uint Rva, IReadOnlyList<byte> Bytes, IReadOnlyList<byte> Original)
{
    /// <summary>
    /// The module the RVA is in, or null for the one the listing is about.
    ///
    /// An RVA was always module-relative; what it lacked was a way to say <em>which</em> module, so
    /// every patch went into the one being read. Naming one is what lets a hypothesis be tried in a
    /// protection DLL a program loads rather than only in the program itself.
    /// </summary>
    public string? Module { get; init; }
}

/// <summary>
/// A process running under a debug loop.
///
/// Everything that touches the debuggee happens on one thread. That is not tidiness: Windows ties a
/// debugging session to the thread that started it, and <c>WaitForDebugEvent</c> called from anywhere
/// else simply does not see the events. So the loop owns the process and callers post work to it.
///
/// Running the program is the whole point and also the thing to be careful about: everything else in
/// Spydate reads a file, and this executes it. It is never started implicitly — opening a binary,
/// analysing it and patching it all leave it inert, and only an explicit start runs anything.
/// </summary>
public sealed class DebugSession : IDisposable
{
    /// <summary>
    /// What a breakpoint is kept under.
    ///
    /// <paramref name="Module"/> null means the module the listing is about — the one
    /// <see cref="Start"/> named — and then <paramref name="Address"/> is a static address in it. A
    /// named module makes <paramref name="Address"/> an RVA in that module instead.
    ///
    /// The asymmetry is forced and worth stating. A static address cannot name a module: 0x180000000
    /// is the default base for an x64 DLL and most of them keep it, so several modules in one process
    /// routinely claim the same static address, and an address alone would silently mean whichever one
    /// happened to be asked first. An RVA plus a name always says which. The null case keeps its
    /// static address because that is what the listing shows and what every existing caller passes,
    /// and because with no image base given there is no RVA to speak of.
    /// </summary>
    private readonly record struct BreakpointAt(string? Module, ulong Address);

    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly SemaphoreSlim _resume = new(0, 1);
    private readonly Dictionary<BreakpointAt, Breakpoint> _breakpoints = new();
    private readonly Dictionary<ulong, LoadedModule> _modules = new();
    private readonly Dictionary<uint, DebugThread> _threads = new();

    /// <summary>
    /// What a patch is kept under: an RVA, and which module's RVA it is.
    ///
    /// Null module means the one the listing is about, which is what every existing caller means and
    /// what it meant when an RVA was the whole key. Two modules can hold a patch at the same RVA
    /// without either being the other, so the RVA alone stopped being an identity as soon as more
    /// than one module could be patched.
    /// </summary>
    private readonly record struct PatchAt(string? Module, uint Rva);

    /// <summary>
    /// Bytes to keep written over the module, by RVA. Written the moment the module is there and
    /// again every time it loads, so a debug run behaves like the patched copy without one being
    /// saved — and a hypothesis can be tried in the running process and taken straight back out.
    /// </summary>
    private readonly Dictionary<PatchAt, LivePatch> _patches = new();
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// The file name of the module whose addresses the listing is about, or null for the process's
    /// own executable. It is what makes debugging a DLL possible: the thing being run and the thing
    /// being read are then two different files, and only the second one's load address translates
    /// the addresses on screen.
    /// </summary>
    private string? _target;

    private Thread? _loop;
    private IntPtr _process;
    private uint _processId;
    private uint _threadId;

    /// <summary>True when the debuggee is 32-bit code under WOW64. Asked of the process, not the file.</summary>
    private bool _wow64;

    /// <summary>Set while stepping over a breakpoint, so its byte goes back afterwards.</summary>
    private (ulong Address, uint Thread)? _reArm;

    /// <summary>A breakpoint that exists to get somewhere once: step-over, run-to-cursor, or the
    /// one-shot that carries a launch on to its entry point.</summary>
    private ulong? _temporary;
    private byte _temporaryOriginal;

    /// <summary>Whether the one-shot planted its own int3, so its byte is ours to put back when it is
    /// hit or replaced. False when it rode an int3 already there — a breakpoint the analyst set, or the
    /// program's own — whose byte belongs to that owner and must not be overwritten with the one-shot's
    /// (which it never recorded). Restoring it regardless wrote a stale zero over a real instruction.</summary>
    private bool _temporaryPlanted;

    // The first-call catch (MIXED-MODE.md Phase 7): an int3 on the JIT's shared prestub worker catches a
    // cold managed method the instant it is about to be compiled, before its body runs. None of these
    // stops are reported — they are the machinery, not the breakpoint the analyst set.

    /// <summary>The armed PreStubWorker address, or 0. Every method's first JIT passes through it, and the
    /// MethodDesc being compiled is its argument, so the loop can tell whose JIT this is.</summary>
    private ulong _prestubVa;

    /// <summary>PreStubWorker's RVA in the runtime image once resolved from its PDB; 0 if unavailable, so
    /// the catch falls back to planting on a later call. Set on a background task, read on the loop.</summary>
    private volatile uint _prestubRva;

    /// <summary>Which register holds the MethodDesc being compiled at the prestub — rdx on CoreCLR, rcx on
    /// Framework. Set with <see cref="_prestubRva"/>.</summary>
    private string _prestubRegister = "rdx";

    /// <summary>True while the PDB is being resolved, so it is not started twice.</summary>
    private volatile bool _prestubResolving;

    /// <summary>The held breakpoint whose method is being compiled right now, and the prestub's return
    /// address where that method will have native code. Set between a matching prestub hit and the return
    /// one-shot that follows it.</summary>
    private HeldManaged? _dancing;
    private ulong _danceReturn;

    /// <summary>The stack pointer at the prestub hit for the method being caught. A method's JIT can
    /// trigger nested JITs whose prestub calls return through the same shared address; the return that
    /// matters is the one where the stack has unwound back to here, not a deeper nested one.</summary>
    private ulong _danceRsp;

    /// <summary>Where the run should first stop of its own accord. Settled at <see cref="Start"/>.</summary>
    private EntryStop _entryStop = EntryStop.LoaderBreak;

    /// <summary>The opened module's entry-point RVA, for <see cref="EntryStop.ModuleEntry"/>. Zero when unknown.</summary>
    private uint _entryRva;

    /// <summary>The launched process's own entry, as the OS reported it at CreateProcess. Zero until then.</summary>
    private ulong _processEntry;

    /// <summary>A step was asked for, as opposed to the trap flag being set to get off a breakpoint.</summary>
    /// <summary>The thread a step was asked of, until it has taken it. Null when nothing is stepping.</summary>
    private uint? _stepThread;

    /// <summary>Where the step began, and whether its thread was waiting in the kernel then.</summary>
    private ulong _stepFrom;

    private bool _stepWasWaiting;

    /// <summary>Set once the 32-bit loader has broken in, which it does as well as the 64-bit one.</summary>
    private bool _sawWow64Break;

    /// <summary>A pause has been asked for and its break-in has not arrived yet.</summary>
    private volatile bool _pausing;

    /// <summary>The process's first thread, which is what a pause shows when nobody has picked one.</summary>
    private uint _mainThread;

    /// <summary>
    /// Shown in place of the thread that reported, for the current stop only. A pause is reported by
    /// a thread Windows started to break in with, which is not worth looking at.
    /// </summary>
    private uint _stand;

    private bool _sawInitialBreak;

    /// <summary>Raised on the debug loop's thread. Consumers marshal for themselves.</summary>
    public event EventHandler<DebugEvent>? Reported;

    public DebugState State { get; private set; } = DebugState.NotStarted;

    /// <summary>Where the loaded image actually is, which is not where the file says on a rebased load.</summary>
    public ulong LoadedBase { get; private set; }

    /// <summary>What the file says its base is, taken from the caller so this need not parse the PE.</summary>
    public ulong ImageBase { get; private set; }

    /// <summary>
    /// Every thread alive right now. The one that stopped is <see cref="CurrentThreadId"/>: Windows
    /// reports a debug event against whichever thread caused it, and that is the thread whose
    /// registers are read and the thread a step actually steps.
    /// </summary>
    public IReadOnlyList<DebugThread> Threads
    {
        get
        {
            lock (_threads)
            {
                return _threads.Values.OrderBy(t => t.Id).ToList();
            }
        }
    }

    /// <summary>Which thread the last event came from, and so which one everything else is about.</summary>
    public uint CurrentThreadId => _threadId;

    /// <summary>
    /// The operating system id of the debuggee, or zero before there is one.
    ///
    /// Set while the process is being created and before <see cref="Start"/> returns, so a caller that
    /// has started a session can rely on it. Anything wanting to read the debuggee by another route
    /// than this loop needs it — the CLR data access layer attaches by process id — and without it
    /// such a caller has to guess which process was just launched.
    /// </summary>
    public uint ProcessId => _processId;

    /// <summary>
    /// The managed overlay: ClrMD attached passively to this process, or null before it has started.
    ///
    /// This loop owns the port and does the breaking; the overlay reads the managed world — threads,
    /// stacks, objects, the IL-to-native map — without a port of its own. It is only meaningful while
    /// the process is stopped, which is when this loop has it, and it is told when the process has run
    /// so its cached view can flush. Disposed when the session stops.
    /// </summary>
    public ManagedOverlay? Managed => _overlay;

    private ManagedOverlay? _overlay;

    /// <summary>
    /// The thread being looked at and stepped. Defaults to whichever one stopped.
    ///
    /// Windows decides which thread reports an event; it does not decide which one an analyst is
    /// interested in. A breakpoint hit on a worker leaves the thread you were following exactly
    /// where it was, and until this existed there was no way to go back to it.
    /// </summary>
    public uint SelectedThreadId
    {
        get
        {
            // A thread that has since exited is not a thread to step. Falling back to whichever one
            // stopped is the only sane answer, and silently stepping a dead id is not one.
            lock (_threads)
            {
                return _selected != 0 && _threads.ContainsKey(_selected) ? _selected
                    : _stand != 0 && _threads.ContainsKey(_stand) ? _stand
                    : _threadId;
            }
        }

        set => _selected = value;
    }

    private uint _selected;

    /// <summary>Threads this suspended for a step, so exactly those are resumed afterwards.</summary>
    private readonly List<uint> _held = new();

    /// <summary>Every module loaded right now, in load order.</summary>
    public IReadOnlyList<LoadedModule> Modules
    {
        get
        {
            lock (_modules)
            {
                return _modules.Values.ToList();
            }
        }
    }

    /// <summary>
    /// Whether the module the listing is about has been loaded yet. False for a DLL until its host
    /// gets round to loading it, which is when its breakpoints can first go in.
    /// </summary>
    public bool TargetLoaded => LoadedBase != 0;

    /// <summary>Where execution is, when it is stopped.</summary>
    public ulong CurrentAddress { get; private set; }

    /// <summary>How big the image is, so the translation knows where it stops applying.</summary>
    public uint ImageSize { get; private set; }

    /// <summary>
    /// A static address as it will be at run time. The two differ by the load bias whenever the image
    /// is relocated, which for anything built this decade is every run — a breakpoint set on the
    /// address in the listing lands nowhere without this.
    /// </summary>
    public ulong ToRuntime(ulong staticVa)
        => Translatable && staticVa >= ImageBase && staticVa < ImageBase + ImageSize
            ? staticVa - ImageBase + LoadedBase
            : staticVa;

    /// <summary>
    /// The inverse, for reporting a stop at an address the listing will recognise.
    ///
    /// Only inside the image. Execution stops in ntdll and in every other module far more often than
    /// it stops in the binary being read — at the loader break, in a system call, in an exception
    /// handler — and applying the image's bias to one of those addresses does not fail, it produces
    /// a number that looks like an address in this program and belongs to nothing at all.
    /// </summary>
    public ulong ToStatic(ulong runtimeVa)
        => Translatable && runtimeVa >= LoadedBase && runtimeVa < LoadedBase + ImageSize
            ? runtimeVa - LoadedBase + ImageBase
            : runtimeVa;

    /// <summary>Whether a stop is somewhere the listing can show.</summary>
    public bool InsideImage(ulong runtimeVa)
        => Translatable && runtimeVa >= LoadedBase && runtimeVa < LoadedBase + ImageSize;

    private bool Translatable => LoadedBase != 0 && ImageBase != 0 && ImageSize != 0;

    /// <summary>Where a named module landed this run, or zero if it is not loaded.</summary>
    private ulong BaseOf(string module)
    {
        lock (_modules)
        {
            foreach (var loaded in _modules.Values)
            {
                if (string.Equals(loaded.Name, module, StringComparison.OrdinalIgnoreCase))
                {
                    return loaded.Base;
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// Where a breakpoint is right now, or zero when there is nowhere for it to be yet.
    ///
    /// Zero is the whole point of the return: a breakpoint whose module is not loaded has no address,
    /// and the alternative is what used to happen — the translation handed back the static address
    /// unchanged, and planting wrote an int3 into whatever occupied that number, which on a relocated
    /// module is nothing at all.
    /// </summary>
    private ulong RuntimeOf(BreakpointAt at)
    {
        if (at.Module is null)
        {
            return TargetLoaded ? ToRuntime(at.Address) : 0;
        }

        ulong loaded = BaseOf(at.Module);
        return loaded == 0 ? 0 : loaded + at.Address;
    }

    /// <summary>
    /// Where a patch's bytes go right now, or zero when its module is not loaded.
    ///
    /// The same zero-means-nowhere as the breakpoint version, and for the same reason: writing to an
    /// RVA added to a base that is not there yet puts bytes into whatever occupies that number.
    /// </summary>
    private ulong RuntimeOf(PatchAt at)
    {
        if (at.Module is null)
        {
            return TargetLoaded ? LoadedBase + at.Rva : 0;
        }

        ulong loaded = BaseOf(at.Module);
        return loaded == 0 ? 0 : loaded + at.Rva;
    }

    /// <summary>A patch written the way somebody asked for it.</summary>
    private static string Where(PatchAt at)
        => at.Module is null ? $"RVA 0x{at.Rva:X}" : $"{at.Module}+0x{at.Rva:X}";

    /// <summary>The keys, copied, so they can be resolved without holding the table.</summary>
    private List<BreakpointAt> Keys()
    {
        lock (_breakpoints)
        {
            return _breakpoints.Keys.ToList();
        }
    }

    /// <summary>
    /// Which breakpoint is at a runtime address, or null for none of them.
    ///
    /// A reverse lookup rather than a keyed one, because a runtime address no longer identifies a
    /// breakpoint on its own — two modules can be at bases that make different breakpoints resolve
    /// from the same static number. The keys are copied out first: resolving one reads
    /// <see cref="_modules"/>, and taking that lock inside <see cref="_breakpoints"/> would invert the
    /// order every other path here uses.
    /// </summary>
    private BreakpointAt? Find(ulong runtime)
    {
        foreach (var at in Keys())
        {
            if (RuntimeOf(at) == runtime)
            {
                return at;
            }
        }

        return null;
    }

    /// <summary>A breakpoint written the way somebody asked for it.</summary>
    private static string Where(BreakpointAt at)
        => at.Module is null ? $"0x{at.Address:X}" : $"{at.Module}+0x{at.Address:X}";

    /// <summary>An address written as whichever breakpoint is there, or as a plain listing address.</summary>
    private string Describe(ulong runtime)
        => Find(runtime) is { } at ? Where(at) : $"0x{ToStatic(runtime):X}";

    public IReadOnlyList<Breakpoint> Breakpoints
    {
        get
        {
            lock (_breakpoints)
            {
                return _breakpoints.Values.ToList();
            }
        }
    }

    /// <summary>
    /// Whether the debuggee gets a console window of its own.
    ///
    /// On by default, because a console program's output is half of what the person watching it came
    /// for. Off for tests, which start dozens of these: each window takes the foreground as it
    /// appears, so a suite run while somebody is typing takes the keyboard away from them repeatedly.
    /// The program still has a console either way — only the window is withheld — so nothing about
    /// how it runs changes.
    /// </summary>
    public bool ShowConsole { get; init; } = true;

    /// <summary>
    /// Starts <paramref name="path"/> under the debugger. Nothing runs until this is called, and it
    /// is only ever called because somebody asked for it.
    /// </summary>
    /// <param name="path">What to run. For a DLL this is the host that loads it, not the DLL.</param>
    /// <param name="imageBase">The preferred base of the module being read, from its own headers.</param>
    /// <param name="imageSize">Its size, so the translation knows where it stops applying.</param>
    /// <param name="module">
    /// The file name of the module the listing is about. Null means the process's own executable,
    /// which is the case whenever the thing being run is also the thing being read.
    /// </param>
    public void Start(
        string path,
        ulong imageBase,
        uint imageSize,
        string? arguments = null,
        string? workingDirectory = null,
        string? module = null,
        EntryStop entryStop = EntryStop.LoaderBreak,
        uint entryRva = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (State != DebugState.NotStarted)
        {
            throw new InvalidOperationException("this session has already been started");
        }

        ImageBase = imageBase;
        ImageSize = imageSize;
        _target = module is { Length: > 0 } ? System.IO.Path.GetFileName(module) : null;
        _entryStop = entryStop;
        _entryRva = entryRva;
        var ready = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        _loop = new Thread(() => Loop(path, arguments, workingDirectory, ready))
        {
            IsBackground = true,
            Name = "spydate-debug",
        };

        _loop.Start();

        if (ready.Task.GetAwaiter().GetResult() is { } failure)
        {
            throw failure;
        }
    }

    /// <summary>Lets it run on. Does nothing unless it is stopped.</summary>
    /// <summary>Lets it run on, and lets go of anything a step was holding.</summary>
    public void Continue()
    {
        _overlay?.MarkMoved();
        Post(() =>
        {
            _stepThread = null;
            _managedStep = null;
            ReleaseOthers();
        });
    }

    /// <summary>
    /// Stops a running process wherever it happens to be. Returns false unless it was running.
    ///
    /// The one way back from running that does not end the run. Before this, a step that never
    /// landed - a thread waiting on something that did not come - left Stop as the only way out, and
    /// Stop kills the process along with every breakpoint reached and module loaded on the way.
    /// </summary>
    public bool Pause()
    {
        if (State != DebugState.Running || _process == IntPtr.Zero)
        {
            return false;
        }

        _pausing = true;
        _overlay?.MarkMoved();
        if (!Native.DebugBreakProcess(_process))
        {
            _pausing = false;
            return false;
        }

        return true;
    }

    /// <summary>One instruction, then stop again. Into a call, not over it.</summary>
    public void StepInstruction()
    {
        _overlay?.MarkMoved();
        Post(() => Trap());
    }

    /// <summary>
    /// One instruction, but over a call rather than into it.
    ///
    /// A call is stepped by breaking on the instruction after it and letting the whole call run,
    /// which is the only way that works: single-stepping through a call means single-stepping every
    /// instruction it and everything it calls executes, which for anything touching the CRT is
    /// millions of round trips through the debug loop.
    ///
    /// Everything else is an ordinary step. A conditional jump stepped "over" still has to go where
    /// it goes — there is no instruction after it to break on in any useful sense.
    /// </summary>
    public void StepOver()
    {
        _overlay?.MarkMoved();
        Post(() =>
        {
        if (Decode() is not { } instruction || !IsCall(instruction))
        {
            Trap();
            return;
        }

        // Armed only if it is really in place; otherwise a one-shot nobody will ever hit stays set.
        if (Plant(instruction.NextIP, temporary: true))
        {
            _temporary = instruction.NextIP;
        }
        else
        {
            Trap();
        }
        });
    }

    /// <summary>
    /// Runs until execution reaches a static address, then stops. The breakpoint is not remembered:
    /// it is a way of getting somewhere, not a place to keep stopping at.
    /// </summary>
    public void RunTo(ulong staticVa)
    {
        _overlay?.MarkMoved();
        Post(() =>
        {
            ulong runtime = ToRuntime(staticVa);
            if (Plant(runtime, temporary: true))
            {
                _temporary = runtime;
            }
        });
    }

    /// <summary>
    /// Runs until the function the stopped thread is in returns, and stops in its caller — a native
    /// "step out". The caller's return address comes from unwinding one frame with the platform's own
    /// unwinder (<see cref="NativeStackWalk"/>), which reads the function's <c>.pdata</c> where reading
    /// the stack directly could not. A one-shot breakpoint is planted there and the process let run to
    /// it, exactly as <see cref="RunTo"/> does — the difference is only how the address is found.
    /// </summary>
    /// <summary>
    /// True when the step was started. The unwind happens here rather than inside the posted work, and
    /// that ordering is the point: posting resumes the debuggee unconditionally, so a step out that
    /// could not find its caller would run the program on to wherever it ended — for the outermost
    /// frame, to exit. Working the address out first means a step out that cannot be done changes
    /// nothing at all and says why, leaving the program where it was stopped.
    /// </summary>
    public bool TryStepOut(out string? problem)
    {
        problem = null;
        if (State != DebugState.Stopped)
        {
            problem = "nothing is stopped";
            return false;
        }

        uint thread = SelectedThreadId;
        ulong rip = RipOf(thread);
        var module = ModuleAt(rip);

        // Safe to read from this thread: the debuggee is stopped with its threads suspended, and
        // nothing here touches the debug loop — only the process's memory and the thread's registers.
        ulong ret = NativeStackWalk.CallerReturn(_process, thread, _wow64, module?.Path, module?.Base ?? 0);
        if (ret == 0)
        {
            problem = NativeStackWalk.LastDiag ?? "could not work out where this function returns to";
            return false;
        }

        _overlay?.MarkMoved();
        Post(() =>
        {
            if (Plant(ret, temporary: true))
            {
                _temporary = ret;
            }
            else
            {
                Report("problem", $"could not set the one-shot breakpoint at 0x{ret:X}");
            }
        });

        return true;
    }

    /// <summary>The loaded module a runtime address is in — the one with the greatest base at or below
    /// it — or null when nothing is loaded there.</summary>
    public LoadedModule? ModuleAt(ulong runtimeVa)
    {
        lock (_modules)
        {
            LoadedModule? best = null;
            foreach (var module in _modules.Values)
            {
                if (module.Base <= runtimeVa && (best is null || module.Base > best.Base))
                {
                    best = module;
                }
            }

            return best;
        }
    }

    /// <summary>A managed step in flight: the thread, what to do with calls, and where it started.</summary>
    private sealed record ManagedStep(
        uint Thread, IlStepKind Kind, ulong RangeStart, ulong RangeEnd, ulong MethodStart, ulong MethodEnd, int StartIl, string Method);

    private ManagedStep? _managedStep;

    /// <summary>
    /// Steps by one IL offset, over the native loop and the DAC together.
    ///
    /// A managed step is a run of native single-steps that ends when the IL offset changes — the DAC
    /// says which native range each IL offset occupies, and while the instruction pointer stays in the
    /// starting range it is still on the same offset. Calls are the only complication: stepped over by
    /// a one-shot after them (<see cref="IlStepKind.Over"/>), descended into when they are managed and
    /// the step is <see cref="IlStepKind.Into"/>, and skipped entirely by <see cref="IlStepKind.Out"/>,
    /// which just runs to the caller's return address. Nothing here talks to ICorDebug; it is the
    /// overlay's addresses and the loop's int3s.
    /// </summary>
    public void StepManaged(IlStepKind kind) => Post(() => BeginManagedStep(kind));

    private void BeginManagedStep(IlStepKind kind)
    {
        uint thread = SelectedThreadId;
        ulong rip = RipOf(thread);
        if (_overlay?.StepInfoAt(rip) is not { } info)
        {
            Report("problem", "a managed step needs a managed frame, and there is none at this stop");
            return;
        }

        _managedStep = new ManagedStep(thread, kind, info.RangeStart, info.RangeEnd, info.MethodStart, info.MethodEnd, info.IlOffset, info.Method);

        if (kind == IlStepKind.Out)
        {
            // Out is not a walk: it runs to where the method returns, which is the instruction pointer
            // of the frame above this one, and the DAC hands that over. The one-shot there is what the
            // step waits on; the whole process runs until it is hit.
            ulong ret = CallerReturn(thread);
            if (ret == 0 || !Plant(ret, temporary: true))
            {
                _managedStep = null;
                Report("problem", "there is no caller to step out to");
                return;
            }

            _temporary = ret;
            return;
        }

        // Into and Over walk the method one instruction at a time, so the others are held for the run,
        // the way a plain native step holds them — otherwise another thread could stop first and the
        // step would report from somewhere else entirely.
        HoldOthers(thread);
        AdvanceManagedStep(rip);
    }

    /// <summary>
    /// Where a managed step goes next after landing at an address: finished, or one more micro-step.
    /// Returns whether the step is done — true means the caller reports the stop, false means another
    /// single-step or a step-over-the-call has been issued and nothing should be said yet.
    /// </summary>
    private bool AdvanceManagedStep(ulong rip)
    {
        if (_managedStep is not { } step)
        {
            return true;
        }

        bool done = step.Kind switch
        {
            IlStepKind.Over => rip < step.RangeStart || rip >= step.RangeEnd,
            IlStepKind.Out => rip < step.MethodStart || rip >= step.MethodEnd,
            IlStepKind.Into => IntoLanded(step, rip),
            _ => true,
        };

        if (done)
        {
            _managedStep = null;
            return true;
        }

        // Not done. A call is stepped over by a one-shot after it, unless the step is Into and the call
        // goes to managed code, in which case single-stepping descends into it.
        if (Decode() is { } instruction && IsCall(instruction))
        {
            bool descend = step.Kind == IlStepKind.Into && CallTargetManaged(instruction);
            if (!descend && Plant(instruction.NextIP, temporary: true))
            {
                _temporary = instruction.NextIP;
                return false;
            }
        }

        ArmSingleStep(step.Thread);
        return false;
    }

    /// <summary>Whether an Into step has reached a new managed location — a new IL offset, or a callee.</summary>
    private bool IntoLanded(ManagedStep step, ulong rip)
    {
        if (_overlay?.StepInfoAt(rip) is not { } info)
        {
            // Native code — a helper the step wandered into. Keep going until it is back in managed code.
            return false;
        }

        return info.Method != step.Method || info.IlOffset != step.StartIl;
    }

    /// <summary>Whether a direct call goes to code the DAC knows as managed, so an Into step descends.</summary>
    private bool CallTargetManaged(Iced.Intel.Instruction instruction)
    {
        if (instruction.FlowControl != Iced.Intel.FlowControl.Call)
        {
            // Indirect: the target is not known without reading the register, so it is stepped over.
            return false;
        }

        ulong target = instruction.NearBranchTarget;
        return target != 0 && _overlay?.LocationOf(target) is not null;
    }

    /// <summary>The return address of the managed frame above a thread's current one, or zero.</summary>
    private ulong CallerReturn(uint thread)
    {
        var walked = _overlay?.Threads().FirstOrDefault(t => t.OsId == thread);
        return walked is { Frames.Count: >= 2 } ? walked.Frames[1].InstructionPointer : 0;
    }

    /// <summary>A thread's instruction pointer, or zero when it cannot be read.</summary>
    private ulong RipOf(uint thread)
    {
        using var context = ThreadContext.For(_wow64);
        var handle = Native.OpenThread(Native.THREAD_ALL_ACCESS, false, thread);
        if (handle == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            return context.Read(handle) ? context.InstructionPointer : 0;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>Sets the trap flag on a thread so its next instruction faults, and attributes the step to it.</summary>
    private void ArmSingleStep(uint thread)
    {
        _stepThread = thread;
        _stepWasWaiting = false;

        using var context = ThreadContext.For(_wow64);
        var handle = Native.OpenThread(Native.THREAD_ALL_ACCESS, false, thread);
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (context.Read(handle))
            {
                _stepFrom = context.InstructionPointer;
                context.SetTrapFlag(true);
                context.Write(handle);
            }
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>The instruction at RIP, or null when it cannot be read or decoded.</summary>
    private Iced.Intel.Instruction? Decode()
    {
        using var context = ThreadContext.For(_wow64);
        var thread = Native.OpenThread(Native.THREAD_ALL_ACCESS, false, _threadId);
        if (thread == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!context.Read(thread))
            {
                return null;
            }

            byte[] code = ReadMemory(context.InstructionPointer, 16);
            if (code.Length == 0)
            {
                return null;
            }

            var decoder = Iced.Intel.Decoder.Create(_wow64 ? 32 : 64, new Iced.Intel.ByteArrayCodeReader(code), context.InstructionPointer);
            var instruction = decoder.Decode();
            return instruction.IsInvalid ? null : instruction;
        }
        finally
        {
            Native.CloseHandle(thread);
        }
    }

    private static bool IsCall(Iced.Intel.Instruction instruction)
        => instruction.FlowControl is Iced.Intel.FlowControl.Call or Iced.Intel.FlowControl.IndirectCall;

    /// <summary>
    /// Asks the processor to fault after the next instruction, and remembers that somebody wanted
    /// that fault. The trap flag alone cannot say so: getting off a breakpoint sets it too, and the
    /// two are told apart nowhere else.
    /// </summary>
    private void Trap()
    {
        uint stepping = SelectedThreadId;
        _stepThread = stepping;
        _stepWasWaiting = false;

        using var context = ThreadContext.For(_wow64);
        var thread = Native.OpenThread(Native.THREAD_ALL_ACCESS, false, stepping);
        if (thread == IntPtr.Zero)
        {
            return;
        }

        if (context.Read(thread))
        {
            _stepFrom = context.InstructionPointer;
            context.SetTrapFlag(true);
            context.Write(thread);
        }

        Native.CloseHandle(thread);

        // Every other thread is held for the duration of the step.
        //
        // Continuing lets the whole process run, and one instruction is long enough for another
        // thread to reach a breakpoint, take a fault, or simply move — so a step would report from
        // somewhere else entirely and the thread being followed would not have moved at all. Holding
        // the others makes "step" mean what it says: this thread, one instruction.
        //
        // Except when this thread is waiting in the kernel. It runs its next instruction only once
        // the wait ends, and what ends it is nearly always another thread - one of the ones about to
        // be held. Holding them made the step impossible: it never landed, and the debugger sat on a
        // process that could not move. So the others run, and the step lands when the wait is over.
        if (IsWaitingInKernel(stepping))
        {
            _stepWasWaiting = true;
            Report("note", $"thread {stepping} is waiting in the kernel; the others are left running so the wait "
                           + "can end, and the step lands when it returns");
            return;
        }

        HoldOthers(stepping);
    }

    /// <summary>
    /// Suspends every thread but one, remembering which so they can be let go again.
    ///
    /// A suspend count, not a flag: a thread the debuggee itself suspended must not be resumed by
    /// us, so only the ones actually stopped here are recorded and only those are started again.
    /// </summary>
    /// <summary>
    /// True when a thread is parked in a system call: its RIP sits just past a <c>syscall</c>.
    ///
    /// Most threads are, at any stop - workers waiting for work, a window waiting for a message. Read
    /// from the thread itself rather than through <see cref="RegistersOf"/>, because a step asks
    /// from inside <see cref="Post"/>, where the session already says running though nothing has
    /// moved yet.
    /// </summary>
    public bool IsWaitingInKernel(uint threadId)
    {
        var thread = Native.OpenThread(Native.THREAD_ALL_ACCESS, false, threadId);
        if (thread == IntPtr.Zero)
        {
            return false;
        }

        ulong rip = 0;
        using (var context = ThreadContext.For(_wow64))
        {
            if (context.Read(thread))
            {
                rip = context.InstructionPointer;
            }
        }

        Native.CloseHandle(thread);

        // Only for 64-bit threads. A WOW64 thread enters the kernel through an indirect call, whose
        // opcode bytes are far too ordinary to match on: a false positive here is the worse failure,
        // because it stops a step holding the other threads and the step then reports from whichever
        // one moved. So a 32-bit thread is never called waiting, stepping one that is parked can hang
        // as it did before this existed, and Pause is the way out.
        return !_wow64 && rip >= 2 && ReadMemory(rip - 2, 2) is [0x0F, 0x05];
    }

    private void HoldOthers(uint except)
    {
        // Anything still held from before goes free first. Clearing the list without resuming left a
        // thread suspended twice and resumed once - frozen for the rest of the run.
        ReleaseOthers();

        foreach (var thread in Threads)
        {
            if (thread.Id == except)
            {
                continue;
            }

            var handle = Native.OpenThread(Native.THREAD_ALL_ACCESS, false, thread.Id);
            if (handle == IntPtr.Zero)
            {
                continue;
            }

            if (Native.SuspendThread(handle) != uint.MaxValue)
            {
                _held.Add(thread.Id);
            }

            Native.CloseHandle(handle);
        }
    }

    /// <summary>Lets go of everything <see cref="HoldOthers"/> took, and nothing else.</summary>
    private void ReleaseOthers()
    {
        foreach (uint id in _held)
        {
            var handle = Native.OpenThread(Native.THREAD_ALL_ACCESS, false, id);
            if (handle == IntPtr.Zero)
            {
                continue;
            }

            Native.ResumeThread(handle);
            Native.CloseHandle(handle);
        }

        _held.Clear();
    }

    /// <summary>
    /// Adds a breakpoint at a <em>static</em> address — the one in the listing. It is translated for
    /// the run and planted when the process is there to plant it in.
    /// </summary>
    /// <summary>The live patches currently held, applied or not.</summary>
    public IReadOnlyList<LivePatch> LivePatches
    {
        get
        {
            lock (_patches)
            {
                return _patches.Values.ToList();
            }
        }
    }

    /// <summary>
    /// Holds bytes over the module and writes them if it is loaded, or remembers them for when it is.
    /// Returns null on success, or why it could not be written now.
    ///
    /// The write itself is only safe stopped, and only where nothing is standing on the bytes: a
    /// thread paused inside them would resume half-way through a new instruction, and a breakpoint's
    /// int3 would be overwritten with no record of it left. Both are refused rather than risked. When
    /// the module is not loaded yet the patch is only held, and goes in the moment it arrives.
    /// </summary>
    public string? SetPatch(LivePatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.Bytes.Count != patch.Original.Count || patch.Bytes.Count == 0)
        {
            return "a patch must replace as many bytes as it covers";
        }

        // Which module's RVA this is comes off the patch itself, so a caller naming one needs no other
        // entry point than the property.
        var at = new PatchAt(patch.Module, patch.Rva);
        lock (_patches)
        {
            _patches[at] = patch;
        }

        // Held rather than refused while its module is absent: a patch waits for the load exactly as a
        // breakpoint does, and goes in the moment that module arrives.
        if (_process == IntPtr.Zero || RuntimeOf(at) == 0)
        {
            return null;
        }

        return WriteRange(at, patch.Bytes);
    }

    /// <summary>
    /// Takes a held patch out, putting the file's own bytes back if the module is loaded. Returns
    /// null on success, or why it could not be done now — in which case it stays held and applied.
    /// </summary>
    public string? ClearPatch(uint rva) => Clear(new PatchAt(null, rva));

    /// <summary>Takes out a patch that was set in a named module.</summary>
    public string? ClearPatch(string module, uint rva)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);

        return Clear(new PatchAt(System.IO.Path.GetFileName(module), rva));
    }

    private string? Clear(PatchAt at)
    {
        LivePatch? patch;
        lock (_patches)
        {
            patch = _patches.TryGetValue(at, out var found) ? found : null;
        }

        if (patch is null)
        {
            return null;
        }

        if (_process != IntPtr.Zero && RuntimeOf(at) != 0
            && WriteRange(at, patch.Original) is { } refused)
        {
            return refused;
        }

        lock (_patches)
        {
            _patches.Remove(at);
        }

        return null;
    }

    /// <summary>
    /// Writes bytes at an RVA in the loaded module, refusing when it is not a moment to. The caller
    /// has already decided what goes there; this decides only whether it is safe to put it there now.
    /// </summary>
    private string? WriteRange(PatchAt at, IReadOnlyList<byte> bytes)
    {
        if (State == DebugState.Running)
        {
            return "it is running — pause it before changing its bytes";
        }

        ulong start = RuntimeOf(at);
        if (start == 0)
        {
            return $"{at.Module ?? "the module the listing is about"} is not loaded, so there is "
                   + "nowhere to put those bytes yet";
        }

        ulong end = start + (ulong)bytes.Count;

        foreach (var thread in Threads)
        {
            ulong rip = InstructionPointerOf(thread.Id) ?? 0;
            if (rip >= start && rip < end)
            {
                return $"thread {thread.Id} is stopped inside those bytes; it would resume in the middle "
                       + "of a changed instruction";
            }
        }

        foreach (var key in Keys())
        {
            Breakpoint? breakpoint;
            lock (_breakpoints)
            {
                _breakpoints.TryGetValue(key, out breakpoint);
            }

            ulong planted = RuntimeOf(key);
            if (breakpoint is { Planted: true } && planted >= start && planted < end)
            {
                return $"a breakpoint at {Where(key)} is inside those bytes; clear it first";
            }
        }

        // The last thing that can refuse it, and it used to be the one thing that could not: the write
        // went out with its result discarded, so a patch onto bytes the process would not take was
        // reported as applied. SetPatch and ClearPatch both come through here, which means a failed
        // removal is now reported too — a patch that could not be taken back out is worth knowing
        // about, because the caller is about to believe the program is back to normal.
        if (!WriteBytes(start, bytes))
        {
            return $"the {bytes.Count} byte(s) at {Where(at)} could not be written";
        }

        return null;
    }

    /// <summary>
    /// Writes every held patch into the module. Called before breakpoints are planted, so a
    /// breakpoint that lands on a patched byte saves the patched byte and restores the patch, not the
    /// file, when it is hit or lifted.
    /// </summary>
    private void WritePatches()
    {
        List<PatchAt> held;
        lock (_patches)
        {
            held = _patches.Keys.ToList();
        }

        foreach (var at in held)
        {
            LivePatch? patch;
            lock (_patches)
            {
                _patches.TryGetValue(at, out patch);
            }

            // Skipped rather than written wrong when its module is not here. It goes in when that
            // module loads, which is what WritePatchesNaming is for.
            ulong start = RuntimeOf(at);
            if (patch is null || start == 0)
            {
                continue;
            }

            // Reported per patch rather than in the aggregate. This runs at the loader break and every
            // time the module loads, with nobody waiting on a return value, so a patch that does not
            // go in has the log as its only way of saying so — and "the program behaved as though it
            // were unpatched" is otherwise a mystery with no evidence attached to it.
            if (!WriteBytes(start, patch.Bytes))
            {
                Report("problem", $"the patch at {Where(at)} could not be written");
            }
        }
    }

    public bool AddBreakpoint(ulong staticVa) => Add(new BreakpointAt(null, staticVa));

    /// <summary>
    /// Adds a breakpoint at an RVA in a named module, which is how one goes into a module other than
    /// the one the listing is about.
    ///
    /// A name and an RVA rather than an address, for the reason <see cref="BreakpointAt"/> gives: an
    /// address cannot name a module when modules share a preferred base, and they usually do. This is
    /// what lets breakpoints sit in three different DLLs of one process at once.
    /// </summary>
    public bool AddBreakpoint(string module, uint rva)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);

        return Add(new BreakpointAt(System.IO.Path.GetFileName(module), rva));
    }

    /// <summary>
    /// Sets a breakpoint at a managed method and IL offset — a managed breakpoint over the native loop.
    ///
    /// The overlay resolves it to the native address the JIT put that code at, and this plants an
    /// ordinary native int3 there. That is the whole trick: a managed breakpoint <em>is</em> a native
    /// breakpoint at an address the DAC found, so it re-arms and fires like any other, and needs
    /// nothing from ICorDebug. The address is a JIT address, outside every module image, so it goes in
    /// as a plain runtime address rather than a module and RVA.
    ///
    /// Returns null when it went in, or why it could not — a method not yet compiled (it JITs on its
    /// first call, and there is no address before then), a name that does not resolve, an offset with
    /// no code. The refusal is deliberate: a managed breakpoint that failed quietly would read as a
    /// method that was never reached.
    /// </summary>
    public string? AddManagedBreakpoint(string type, string method, int ilOffset)
        => AddManagedBreakpoint(new HeldManaged(type, method, 0, ilOffset, HoldUntilAvailable: false));

    /// <summary>
    /// The same, but naming the method by its metadata token — which is what the window has when a line
    /// is clicked. Held until it can be planted rather than refused up front: a token from the opened
    /// assembly's own metadata is a real method that <em>will</em> load, so a breakpoint on it set before
    /// the CLR is up, or before its type is loaded, or before it is JITted, is kept and planted the
    /// moment each of those is true. That is what lets a managed breakpoint be marked in the gutter
    /// before the run, over the native loop, and go in once its code exists.
    /// </summary>
    public string? AddManagedBreakpoint(string type, int methodToken, int ilOffset)
        => AddManagedBreakpoint(new HeldManaged(type, null, methodToken, ilOffset, HoldUntilAvailable: true));

    private string? AddManagedBreakpoint(HeldManaged held)
    {
        if (_overlay is not { } overlay)
        {
            return "nothing is running";
        }

        var resolved = held.Resolve(overlay);
        if (resolved.Ok)
        {
            return AddBreakpoint(resolved.Address)
                ? null
                : $"a breakpoint is already set at 0x{resolved.Address:X}";
        }

        // Held rather than refused. A not-yet-compiled method is planted the moment it has native code,
        // either when a JIT notification announces it (the deterministic first-call catch) or when
        // PlantPending next re-resolves it (the fallback, which catches a later call). A token-based
        // breakpoint is also held while the CLR is not up yet or its type has not loaded, since a token
        // from the opened assembly is a real method that will arrive — a name-based one is not, because
        // a name that does not resolve is as likely a typo as a thing not loaded yet.
        bool holdable = resolved.NotCompiled || (held.HoldUntilAvailable && !resolved.Ok);
        if (holdable)
        {
            // Carry the method token if resolution found it — a name-named breakpoint arrives without one,
            // and the prestub catch needs it to tell whose JIT a hit is for.
            if (held.Token == 0 && resolved.MethodToken != 0)
            {
                held = held with { Token = resolved.MethodToken };
            }

            lock (_pendingManaged)
            {
                if (!_pendingManaged.Contains(held))
                {
                    _pendingManaged.Add(held);
                }
            }

            // Try to catch the first call at the prestub. When the PDB resolves this is exact; when it
            // does not, the held breakpoint is planted on a later call instead — either way it is held.
            EnablePrestubCatch();

            return resolved.NotCompiled
                ? $"{held.Label} is not compiled yet; held and planted on its first call once it JITs"
                : $"{held.Label} is held; it is planted once the CLR is up and its code exists";
        }

        return resolved.Problem ?? "the managed breakpoint could not be resolved";
    }

    /// <summary>
    /// A managed breakpoint waiting to be planted: it is named by type and either a method name or a
    /// metadata token, plus the IL offset. <see cref="HoldUntilAvailable"/> is set for a token-named
    /// one, which is kept through "no CLR yet" and "type not loaded yet" as well as "not compiled yet",
    /// because a token from the opened assembly is a real method that will turn up.
    /// </summary>
    private sealed record HeldManaged(string Type, string? Method, int Token, int IlOffset, bool HoldUntilAvailable)
    {
        public OverlayResolution Resolve(ManagedOverlay overlay)
            => Method is not null ? overlay.Resolve(Type, Method, IlOffset) : overlay.Resolve(Type, Token, IlOffset);

        public string Label => Method ?? $"{Type} 0x{Token:X8}";
    }

    private readonly List<HeldManaged> _pendingManaged = new();

    private const uint ClrDataNotifyException = 0x04242420;

    /// <summary>The first parameter of every CLR DAC notification — a magic marker, so a stray exception
    /// with the same code is not mistaken for one.</summary>
    private const ulong ClrNotifyMagic = 0x31415927;

    /// <summary>
    /// Plants every held managed breakpoint whose method has since been compiled, and returns how many
    /// went in. The fallback to a JIT notification: a caller can poll this, and each pending breakpoint
    /// is planted the first time its method has native code — which catches a later call even when JIT
    /// notifications are not enabled.
    /// </summary>
    public int PlantPending()
    {
        if (_overlay is not { } overlay)
        {
            return 0;
        }

        List<HeldManaged> waiting;
        lock (_pendingManaged)
        {
            waiting = _pendingManaged.ToList();
        }

        int planted = 0;
        foreach (var held in waiting)
        {
            var resolved = held.Resolve(overlay);
            if (resolved.Ok && AddBreakpoint(resolved.Address))
            {
                planted++;
                lock (_pendingManaged)
                {
                    _pendingManaged.Remove(held);
                }

                Report("module", $"{held.Label} compiled; its held breakpoint is planted at 0x{ToStatic(resolved.Address):X}");
            }
        }

        return planted;
    }

    /// <summary>
    /// A CLR DAC notification arrived — a method was jitted, or a module loaded. When it is a JIT
    /// notification, this is the deterministic moment a cold method first has native code, so every
    /// held breakpoint whose method is now compiled is planted before the call that caused the JIT
    /// continues. Runs on the loop thread while the process is stopped on the notification.
    /// </summary>
    private void OnClrNotification(Native.DEBUG_EVENT e)
    {
        // Only a real notification, told by its magic marker; and only a JIT one, which carries three
        // parameters (magic, MethodDesc, native code) as against the two of a module notification.
        if (e.NumberParameters < 3 || e.ExceptionInformation(0) != ClrNotifyMagic)
        {
            return;
        }

        // The MethodDesc and its fresh native code are in the notification, but the simplest and most
        // robust use of it is as the trigger to re-resolve every held breakpoint: the method is
        // compiled as of this instant, so PlantPending finds and plants it.
        PlantPending();
    }

    /// <summary>
    /// Clears a managed breakpoint set by <see cref="AddManagedBreakpoint"/>. Resolution is stable
    /// while the method stays compiled — the same method and offset give the same address — so
    /// re-resolving finds exactly what was planted.
    /// </summary>
    public string? RemoveManagedBreakpoint(string type, string method, int ilOffset)
    {
        if (_overlay is not { } overlay)
        {
            return "nothing is running";
        }

        var resolved = overlay.Resolve(type, method, ilOffset);
        if (!resolved.Ok)
        {
            return resolved.Problem ?? "the managed breakpoint could not be resolved";
        }

        return RemoveBreakpoint(resolved.Address) ? null : "there was no such breakpoint";
    }

    /// <summary>
    /// Clears a managed breakpoint set by the token-named <see cref="AddManagedBreakpoint(string,int,int)"/>.
    /// Drops it from the held list whether or not it had been planted yet, so a mark cleared before its
    /// code existed does not quietly plant itself later.
    /// </summary>
    public string? RemoveManagedBreakpoint(string type, int methodToken, int ilOffset)
    {
        var held = new HeldManaged(type, null, methodToken, ilOffset, HoldUntilAvailable: true);
        bool wasHeld;
        lock (_pendingManaged)
        {
            wasHeld = _pendingManaged.Remove(held);
        }

        if (_overlay is not { } overlay)
        {
            return wasHeld ? null : "nothing is running";
        }

        var resolved = overlay.Resolve(type, methodToken, ilOffset);
        if (resolved.Ok && RemoveBreakpoint(resolved.Address))
        {
            return null;
        }

        // Held but never planted, or planted and now gone: either way the mark is cleared. Only a
        // token that cannot resolve and was not held is a genuine "there was nothing there".
        return wasHeld || resolved.NotCompiled ? null : resolved.Problem ?? "there was no such breakpoint";
    }

    private bool Add(BreakpointAt at)
    {
        lock (_breakpoints)
        {
            if (_breakpoints.ContainsKey(at))
            {
                return false;
            }

            _breakpoints[at] = new Breakpoint(at.Address, 0) { Module = at.Module };
        }

        // Only if its module is actually here. On a DLL that its host has not loaded yet there is no
        // address to put it at; it goes in when the module arrives. RuntimeOf answers zero for that,
        // which is what PlantOne refuses on — so this no longer has to ask about the target module
        // specifically, and a breakpoint in any loaded module goes in at once.
        //
        // Written straight in rather than posted. Post means "do this, then continue", so setting a
        // breakpoint while stopped let the program run on; and while it ran, one set did nothing
        // until something else happened to plant it. Writing a byte needs the process handle, not
        // the debug loop's thread.
        if (_process != IntPtr.Zero)
        {
            PlantOne(at);
        }

        return true;
    }

    public bool RemoveBreakpoint(ulong staticVa) => Remove(new BreakpointAt(null, staticVa));

    /// <summary>Removes a breakpoint named the way <see cref="AddBreakpoint(string, uint)"/> set it.</summary>
    public bool RemoveBreakpoint(string module, uint rva)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);

        return Remove(new BreakpointAt(System.IO.Path.GetFileName(module), rva));
    }

    private bool Remove(BreakpointAt at)
    {
        Breakpoint? existing;
        lock (_breakpoints)
        {
            if (!_breakpoints.TryGetValue(at, out existing))
            {
                return false;
            }

            _breakpoints.Remove(at);
        }

        // The byte goes back whenever there is one to put back, running or not, and without resuming
        // anything. Posted, removing a breakpoint while stopped let the program run; removed while it
        // ran, it only left the table - the int3 stayed in the code with nothing left that knew what
        // it had replaced.
        ulong runtime = RuntimeOf(at);
        if (existing.Planted && _process != IntPtr.Zero && runtime != 0
            && !WriteByte(runtime, existing.Original))
        {
            // Said, because this is exactly the failure the paragraph above is about. The table no
            // longer holds the breakpoint and the int3 is still in the code, so the program goes on
            // stopping at something that nothing can now explain, find or remove. The removal itself
            // still stands — it was asked for — but it does not get to be silent about this.
            Report("problem",
                $"the breakpoint at {Where(at)} was removed, but its original byte could not be put "
                + "back — the int3 is still in the code");
        }

        return true;
    }

    /// <summary>Reads the debuggee's memory. Empty when it cannot be read, which is not exceptional.</summary>
    public byte[] ReadMemory(ulong runtimeAddress, int length)
    {
        if (_process == IntPtr.Zero || length <= 0)
        {
            return [];
        }

        byte[] buffer = new byte[length];
        unsafe
        {
            fixed (byte* p = buffer)
            {
                if (!Native.ReadProcessMemory(_process, runtimeAddress, p, (nuint)length, out nuint read))
                {
                    return [];
                }

                return read == (nuint)length ? buffer : buffer[..(int)read];
            }
        }
    }

    /// <summary>The registers as they stand, for the thread that stopped. Null unless it is stopped.</summary>
    public IReadOnlyList<(string Name, ulong Value)>? Registers() => RegistersOf(_threadId);

    /// <summary>
    /// The top of the stack, as address and value pairs. At a breakpoint this is usually the first
    /// thing worth looking at: the return address, then whatever the frame is holding.
    /// </summary>
    public IReadOnlyList<(ulong Address, ulong Value)> Stack(int count = 16) => StackOf(_threadId, count);

    /// <summary>
    /// The top of one thread's stack. Every thread has its own, and the one beside a thread's
    /// registers has to be that thread's: showing the reporting thread's stack under another
    /// thread's registers put two threads in one picture with nothing to say so.
    /// </summary>
    public IReadOnlyList<(ulong Address, ulong Value)> StackOf(uint threadId, int count = 16)
    {
        if (State != DebugState.Stopped)
        {
            return [];
        }

        if (StackPointerOf(threadId) is not { } sp || sp == 0)
        {
            return [];
        }

        // A word is the machine's, not this program's: a 32-bit stack read eight bytes at a time
        // shows every other slot as rubbish and half as many frames as are there.
        int word = Is32Bit ? 4 : 8;
        byte[] bytes = ReadMemory(sp, count * word);
        var rows = new List<(ulong, ulong)>();
        for (int i = 0; i + word <= bytes.Length; i += word)
        {
            rows.Add((sp + (ulong)i, word == 4 ? BitConverter.ToUInt32(bytes, i) : BitConverter.ToUInt64(bytes, i)));
        }

        return rows;
    }

    /// <summary>Ends the debuggee. It is a debugged process, so it does not outlive the session.</summary>
    public void Stop()
    {
        _stopping.Cancel();

        _managedStep = null;
        _prestubVa = 0;
        _dancing = null;
        _danceReturn = 0;
        _danceRsp = 0;
        _temporary = null;
        _temporaryOriginal = 0;
        _temporaryPlanted = false;
        lock (_pendingManaged)
        {
            _pendingManaged.Clear();
        }

        // The DAC's read handle on the process goes before the process does. Disposed here rather than
        // only in Dispose so a session stopped and left around does not hold the debuggee's handle open.
        _overlay?.Dispose();
        _overlay = null;

        // Ended first, and only then let go. Resuming the held threads of a live process let them
        // run, and one that was due to trap reported a step after Stop had been pressed.
        if (_process != IntPtr.Zero)
        {
            Native.TerminateProcess(_process, 0);
        }

        ReleaseOthers();

        // Let a stopped loop notice it is finished.
        if (_resume.CurrentCount == 0)
        {
            try
            {
                _resume.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    private void Post(Action work)
    {
        if (State != DebugState.Stopped)
        {
            return;
        }

        _commands.Enqueue(work);
        State = DebugState.Running;
        try
        {
            _resume.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private void Report(string kind, string text, ulong? address = null)
    {
        // Stopped is true before anybody is told, not after.
        //
        // Whoever hears this acts on it immediately — the assistant hears "stopped" and asks to
        // continue within microseconds — and Post refuses work unless the state already says
        // stopped. Telling first and setting after left a window in which that command was dropped
        // on the floor: the loop then waited for a command that would never arrive, the process
        // never resumed, and the panel showed a session where only Stop could be pressed. A person
        // clicking a button rarely lost that race. Something reacting in code lost it nearly always.
        if (kind == "stopped")
        {
            State = DebugState.Stopped;
        }

        Reported?.Invoke(this, new DebugEvent(kind, text) { Address = address });
    }

    /// <summary>
    /// A line the debuggee printed. <paramref name="IsError"/> tells the two streams apart, which
    /// is most of the value: a program that fails usually says so on stderr and says nothing at all
    /// on stdout, and a panel that merged them would lose exactly the distinction worth having.
    /// </summary>
    public sealed record ProgramOutput(string Text, bool IsError);

    /// <summary>What the debuggee wrote to its standard output or standard error.</summary>
    public event EventHandler<ProgramOutput>? Wrote;

    /// <summary>
    /// Whether to capture the debuggee's output instead of letting it go to a console.
    ///
    /// The two are exclusive by construction: once its standard handles are the pipes on this side,
    /// a console window of its own would sit there empty. So capturing implies no console, and a
    /// program that wants one can still make its own with AllocConsole.
    /// </summary>
    public bool CaptureOutput { get; init; }

    /// <summary>The read ends, kept so they can be closed when the session ends.</summary>
    private IntPtr _stdoutRead, _stderrRead;

    /// <summary>
    /// Reads one pipe to end-of-file, reporting whole lines.
    ///
    /// Its own thread, and a blocking read: the debug loop must not wait on a program that may
    /// print nothing for an hour, and the pipe ends when the last writing handle closes, which is
    /// when the debuggee exits.
    /// </summary>
    private void Drain(IntPtr pipe, bool isError)
    {
        var thread = new Thread(() =>
        {
            var pending = new StringBuilder();
            var buffer = new byte[4096];
            while (true)
            {
                uint read;
                unsafe
                {
                    fixed (byte* p = buffer)
                    {
                        if (!Native.ReadFile(pipe, p, (uint)buffer.Length, out read, IntPtr.Zero) || read == 0)
                        {
                            break;   // the last writer let go: the program has finished with it
                        }
                    }
                }

                // Decoded as the console encoding rather than UTF-8: a console program writes bytes
                // in the code page, and a box where a character should be is a worse answer than a
                // slightly wrong character.
                pending.Append(Console.OutputEncoding.GetString(buffer, 0, (int)read));
                Flush(pending, isError, last: false);
            }

            Flush(pending, isError, last: true);
        })
        {
            IsBackground = true,
            Name = isError ? "spydate-debuggee-stderr" : "spydate-debuggee-stdout",
        };

        thread.Start();
    }

    /// <summary>
    /// Reports whole lines out of what has arrived, keeping any partial last line for the next read
    /// — a program's output arrives in whatever sized lumps the pipe gives, which is nothing to do
    /// with where its lines end.
    /// </summary>
    private void Flush(StringBuilder pending, bool isError, bool last)
    {
        string text = pending.ToString();
        int cut = text.LastIndexOf('\n');
        if (cut < 0)
        {
            if (last && text.Length > 0)
            {
                pending.Clear();
                Wrote?.Invoke(this, new ProgramOutput(text.TrimEnd('\r'), isError));
            }

            return;
        }

        string whole = text[..cut];
        pending.Remove(0, cut + 1);

        foreach (string line in whole.Split('\n'))
        {
            Wrote?.Invoke(this, new ProgramOutput(line.TrimEnd('\r'), isError));
        }

        if (last && pending.Length > 0)
        {
            string tail = pending.ToString();
            pending.Clear();
            Wrote?.Invoke(this, new ProgramOutput(tail.TrimEnd('\r'), isError));
        }
    }

    private void Loop(string path, string? arguments, string? workingDirectory, TaskCompletionSource<Exception?> ready)
    {
        var startup = new Native.STARTUPINFO { cb = (uint)Marshal.SizeOf<Native.STARTUPINFO>() };
        IntPtr outWrite = IntPtr.Zero, errWrite = IntPtr.Zero;

        if (CaptureOutput)
        {
            var security = new Native.SECURITY_ATTRIBUTES
            {
                nLength = (uint)Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>(),
                bInheritHandle = 1,
            };

            if (Native.CreatePipe(out _stdoutRead, out outWrite, ref security, 0)
                && Native.CreatePipe(out _stderrRead, out errWrite, ref security, 0))
            {
                // The child must not inherit the ends this side reads from, or the pipes never
                // reach end-of-file and the reader threads wait for a writer that has exited.
                Native.SetHandleInformation(_stdoutRead, Native.HANDLE_FLAG_INHERIT, 0);
                Native.SetHandleInformation(_stderrRead, Native.HANDLE_FLAG_INHERIT, 0);

                startup.dwFlags |= Native.STARTF_USESTDHANDLES;
                startup.hStdOutput = outWrite;
                startup.hStdError = errWrite;
                startup.hStdInput = IntPtr.Zero;
            }
        }

        // Quoted, and writable: CreateProcessW may modify the buffer it is given.
        char[] command = ($"\"{path}\""
                          + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments)
                          + '\0').ToCharArray();

        Native.PROCESS_INFORMATION info;
        bool started;
        unsafe
        {
            fixed (char* line = command)
            {
                started = Native.CreateProcess(
                    null, line, IntPtr.Zero, IntPtr.Zero, CaptureOutput,
                    Native.DEBUG_ONLY_THIS_PROCESS
                        | (ShowConsole && !CaptureOutput ? Native.CREATE_NEW_CONSOLE : Native.CREATE_NO_WINDOW),
                    IntPtr.Zero, workingDirectory, ref startup, out info);
            }
        }

        // This side's copies of the write ends go now, whether or not the start worked. While any
        // of them is open the pipe has a writer, so the reader would never see end-of-file even
        // after the debuggee had gone.
        if (outWrite != IntPtr.Zero)
        {
            Native.CloseHandle(outWrite);
        }

        if (errWrite != IntPtr.Zero)
        {
            Native.CloseHandle(errWrite);
        }

        if (!started)
        {
            ClosePipes();
            ready.SetResult(new InvalidOperationException(
                $"could not start {path}: {Marshal.GetLastPInvokeErrorMessage()}"));
            return;
        }

        if (_stdoutRead != IntPtr.Zero)
        {
            Drain(_stdoutRead, isError: false);
            Drain(_stderrRead, isError: true);
        }

        _process = info.hProcess;
        _processId = info.dwProcessId;
        _overlay = new ManagedOverlay(_processId, () => State == DebugState.Running);

        // Asked of the running process rather than taken from the file being read: under a host the
        // two are different programs, and it is the host's width that decides how a thread is read.
        _wow64 = Native.IsWow64Process(info.hProcess, out bool wow64) && wow64;
        State = DebugState.Running;
        ready.SetResult(null);

        try
        {
            Pump();
        }
        finally
        {
            State = DebugState.Exited;
            Native.CloseHandle(info.hThread);
            Native.CloseHandle(info.hProcess);
            _process = IntPtr.Zero;

            // After the process has gone, so whatever it printed on its way out has already been
            // read: the pipe holds it until somebody takes it, and closing first would throw away
            // the last thing a program said, which is usually the reason it was being watched.
            ClosePipes();
        }
    }

    private void ClosePipes()
    {
        foreach (ref var pipe in new[] { _stdoutRead, _stderrRead }.AsSpan())
        {
            if (pipe != IntPtr.Zero)
            {
                Native.CloseHandle(pipe);
            }
        }

        _stdoutRead = IntPtr.Zero;
        _stderrRead = IntPtr.Zero;
    }

    private void Pump()
    {
        while (!_stopping.IsCancellationRequested)
        {
            if (!Native.WaitForDebugEvent(out var e, 200))
            {
                continue;
            }

            _threadId = e.dwThreadId;
            _stand = 0;
            uint status = Native.DBG_CONTINUE;
            bool stop = false;

            switch (e.dwDebugEventCode)
            {
                case Native.CREATE_PROCESS_DEBUG_EVENT:
                    _mainThread = e.dwThreadId;
                    _processEntry = e.CreateProcessStartAddress;
                    Remember(e.dwThreadId, e.CreateProcessStartAddress);
                    OnModuleLoaded(e.CreateProcessFile, e.CreateProcessImageBase, main: true);
                    break;

                case Native.CREATE_THREAD_DEBUG_EVENT:
                    Remember(e.dwThreadId, e.CreateThreadStartAddress);
                    break;

                case Native.EXIT_THREAD_DEBUG_EVENT:
                    lock (_threads)
                    {
                        _threads.Remove(e.dwThreadId);
                    }

                    _held.Remove(e.dwThreadId);
                    break;

                case Native.LOAD_DLL_DEBUG_EVENT:
                    OnModuleLoaded(e.LoadDllFile, e.LoadDllBase, main: false);
                    break;

                case Native.UNLOAD_DLL_DEBUG_EVENT:
                    OnModuleUnloaded(e.UnloadDllBase);
                    break;

                case Native.EXIT_PROCESS_DEBUG_EVENT:
                    // In hex as well as decimal, because a crash exit code is an NTSTATUS and only
                    // one of the two forms is recognisable: 0xC0000005 is an access violation at a
                    // glance, and 3221225477 is not.
                    Report("exited", e.ExitCode == 0
                        ? "exited normally"
                        : $"exited with code {e.ExitCode} (0x{e.ExitCode:X8})");
                    Native.ContinueDebugEvent(e.dwProcessId, e.dwThreadId, Native.DBG_CONTINUE);
                    return;

                case Native.OUTPUT_DEBUG_STRING_EVENT:
                    break;

                case Native.EXCEPTION_DEBUG_EVENT:
                    (stop, status) = OnException(e);
                    break;
            }

            if (stop)
            {
                State = DebugState.Stopped;
                _resume.Wait();

                if (_stopping.IsCancellationRequested)
                {
                    return;
                }

                while (_commands.TryDequeue(out var work))
                {
                    work();
                }

                State = DebugState.Running;
            }

            Native.ContinueDebugEvent(e.dwProcessId, e.dwThreadId, status);
        }
    }

    /// <summary>
    /// Notes a thread. Quiet: a program can make hundreds, and a line each would drown the log that
    /// the module loads and breakpoints have to be found in. The list is where they are read.
    /// </summary>
    private void Remember(uint id, ulong startAddress)
    {
        lock (_threads)
        {
            _threads[id] = new DebugThread(id, startAddress);
        }
    }

    /// <summary>
    /// The registers of one particular thread, or null.
    ///
    /// Every thread is suspended while a debug event is being handled, so any of them can be read at
    /// a stop — not only the one that stopped. That is the whole point of having the list: a
    /// breakpoint hit in a worker says nothing about what the other threads were in the middle of.
    /// </summary>
    /// <summary>True when the debuggee runs 32-bit code. Decided by the process, not by the file.</summary>
    public bool Is32Bit => _wow64;

    /// <summary>Where a thread is, whatever its width. Null unless it is stopped and readable.</summary>
    public ulong? InstructionPointerOf(uint threadId) => Of(threadId, c => c.InstructionPointer);

    /// <summary>The top of a thread's stack, whatever its width.</summary>
    public ulong? StackPointerOf(uint threadId) => Of(threadId, c => c.StackPointer);

    private ulong? Of(uint threadId, Func<ThreadContext, ulong> read)
    {
        var thread = Native.OpenThread(Native.THREAD_ALL_ACCESS, false, threadId);
        if (thread == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            using var context = ThreadContext.For(_wow64);
            return context.Read(thread) ? read(context) : null;
        }
        finally
        {
            Native.CloseHandle(thread);
        }
    }

    public IReadOnlyList<(string Name, ulong Value)>? RegistersOf(uint threadId)
    {
        if (State != DebugState.Stopped)
        {
            return null;
        }

        using var context = ThreadContext.For(_wow64);
        var thread = Native.OpenThread(Native.THREAD_ALL_ACCESS, false, threadId);
        if (thread == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!context.Read(thread))
            {
                return null;
            }

            return context.General();
        }
        finally
        {
            Native.CloseHandle(thread);
        }
    }

    /// <summary>
    /// Writes one register of a stopped thread. Null on success, otherwise why not.
    ///
    /// Read, set, write — the whole context goes back, because that is the only granularity
    /// SetThreadContext has. Done from the calling thread rather than posted to the loop, the same
    /// way <see cref="RegistersOf"/> reads: the debuggee is stopped, so its threads are not moving,
    /// and posting would resume the process to do it.
    ///
    /// Nothing here stops the caller writing rip. Putting execution somewhere it was never going is
    /// the point of being able to write registers at all, and a debugger that allowed every register
    /// except the interesting one would be refusing the reason people ask.
    /// </summary>
    public string? SetRegister(uint threadId, string name, ulong value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (State != DebugState.Stopped)
        {
            return "registers can only be written while it is stopped";
        }

        using var context = ThreadContext.For(_wow64);
        var thread = Native.OpenThread(Native.THREAD_ALL_ACCESS, false, threadId);
        if (thread == IntPtr.Zero)
        {
            return $"could not open thread {threadId}";
        }

        try
        {
            if (!context.Read(thread))
            {
                return "could not read the thread's registers";
            }

            if (!context.TrySet(name.Trim().ToLowerInvariant(), value))
            {
                return $"{name} is not a register this can write";
            }

            return context.Write(thread)
                ? null
                : $"the write was refused: {Marshal.GetLastPInvokeErrorMessage()}";
        }
        finally
        {
            Native.CloseHandle(thread);
        }
    }

    /// <summary>
    /// Records a module and, when it is the one the listing is about, fixes the load bias and puts
    /// the waiting breakpoints in.
    ///
    /// The handle is closed here whatever happens. Windows hands the debugger a file handle with
    /// every one of these events and it is the debugger's to close — a process that loads two
    /// hundred modules leaks two hundred handles otherwise.
    /// </summary>
    private void OnModuleLoaded(IntPtr file, ulong loadBase, bool main)
    {
        string? path = Native.PathOf(file);
        Native.CloseHandle(file);

        if (loadBase == 0)
        {
            return;
        }

        var module = new LoadedModule(path ?? string.Empty, loadBase);
        lock (_modules)
        {
            _modules[loadBase] = module;
        }

        string name = module.Name.Length > 0 ? module.Name : "an unnamed module";
        string where = $"at 0x{loadBase:X}"
                       + (ImageBase != 0 && loadBase != ImageBase ? $" (file says 0x{ImageBase:X})" : string.Empty);

        if (main)
        {
            Report("started", $"running {name} {where}");
        }

        // The runtime just mapped, and a cold managed breakpoint may already be waiting from before the
        // run: this is the moment its prestub can be found and armed, ahead of the method it is for.
        if (module.Name.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase)
            || module.Name.Equals("clr.dll", StringComparison.OrdinalIgnoreCase))
        {
            EnablePrestubCatch(atModuleLoad: true);
        }

        // Any module arriving is the moment breakpoints naming it can go in, whether or not it is the
        // one the listing is about. That is the whole of debugging several modules at once, and it
        // happens here rather than at a stop because the loader announces the mapping before it runs
        // the module's own code — so a breakpoint planted now is already standing in its entry point.
        if (module.Name.Length > 0)
        {
            // Patches before breakpoints, so a breakpoint landing on a patched byte records the
            // patched byte and restores that rather than the file's.
            WritePatchesNaming(module.Name);

            if (PlantNaming(module.Name) is var named and > 0)
            {
                Report("module", $"{name} loaded {where}; "
                                 + $"{named} breakpoint{(named == 1 ? string.Empty : "s")} in it armed");
            }
        }

        // The main image when nothing else was named, or whichever module was: under a host, the
        // process's own executable is not the thing being read.
        bool wanted = _target is null ? main : string.Equals(module.Name, _target, StringComparison.OrdinalIgnoreCase);
        if (!wanted)
        {
            return;
        }

        LoadedBase = loadBase;

        // Its breakpoints could not go in before this moment, so they go in now rather than waiting
        // for a stop that may never come. Nothing to do for the main image — the loader break is
        // still ahead, and planting is what that is for.
        if (main)
        {
            return;
        }

        PlantAll();

        // Module-entry break: the module being read has just landed under its host and the loader has
        // not yet called into it, so a one-shot planted now stands in its entry point before its own
        // code runs. This is the mixed-mode case the whole thing was for — stopping in a native DLL a
        // managed process loads, at the first instruction that DLL runs.
        if (_entryStop == EntryStop.ModuleEntry && _temporary is null)
        {
            ArmOneShot(EntryRuntime, "the module entry point");
        }

        int waiting;
        lock (_breakpoints)
        {
            waiting = _breakpoints.Count;
        }

        Report("module", $"{name} loaded {where}"
                         + (waiting > 0 ? $"; {waiting} breakpoint{(waiting == 1 ? string.Empty : "s")} armed" : string.Empty));
    }

    /// <summary>
    /// Drops a module, and if it was the one being read, stops pretending its addresses mean
    /// anything. A DLL can be freed and loaded again at a different address in one run.
    /// </summary>
    private void OnModuleUnloaded(ulong loadBase)
    {
        LoadedModule? gone;
        lock (_modules)
        {
            _modules.Remove(loadBase, out gone);
        }

        // Breakpoints that named this module lose their place whether or not it was the target: its
        // mapping is gone, and the bytes they saved belong to it.
        if (gone?.Name is { Length: > 0 } left)
        {
            Unplant(left);
        }

        if (loadBase != LoadedBase || LoadedBase == 0)
        {
            return;
        }

        LoadedBase = 0;
        Unplant(null);
        Report("module", $"{(gone?.Name is { Length: > 0 } name ? name : "the target module")} was unloaded; "
                         + "its breakpoints go back in if it is loaded again");
    }

    /// <summary>
    /// An exception the kernel raises only for a watching debugger, that the program has no handler for
    /// and never sees on its own — a stale handle closed, a thread named, a debug string printed. These
    /// are continued as handled rather than passed back unhandled, so a benign check does not escalate
    /// to a second chance and stop a process that would have run fine unattended. A real fault
    /// (an access violation, an unhandled managed exception) is not among them and still stops.
    /// </summary>
    private static bool IsBenignDebuggerException(uint code) => code is
        Native.EXCEPTION_INVALID_HANDLE
        or Native.DBG_PRINTEXCEPTION_C
        or Native.DBG_PRINTEXCEPTION_WIDE_C
        or Native.MS_VC_THREAD_NAME_EXCEPTION;

    private (bool Stop, uint Status) OnException(Native.DEBUG_EVENT e)
    {
        switch (e.ExceptionCode)
        {
            // The two codes mean one thing, and which arrives says only how wide the code is: an int3
            // in 32-bit code under WOW64 is reported as STATUS_WX86_BREAKPOINT - the one the debugger
            // planted as much as the one the loader raises.
            //
            // This used to swallow the first of them unconditionally, on the grounds that the 32-bit
            // loader raises one of its own. It does, but so does every breakpoint after it: those
            // fell through to the bottom of this switch, were handed back to the program as an
            // exception nobody had handled, and killed it - the process exited with 0x4000001F, the
            // breakpoint itself as an exit code. A 32-bit binary allowed exactly one resume per
            // launch. The loader's break is now told apart by whose it is rather than by being first.
            case Native.EXCEPTION_BREAKPOINT:
            case Native.EXCEPTION_WX86_BREAKPOINT:
                // The loader breaks once, before the program's own code runs. It is not one of ours,
                // and it is the moment the image is finally mapped — so breakpoints go in here.
                if (!_sawInitialBreak)
                {
                    _sawInitialBreak = true;
                    PlantAll();
                    return AtLoaderBreak(e.ExceptionAddress);
                }

                // The 32-bit loader's own break, which arrives after the 64-bit one on a WOW64
                // process. Nobody planted it and stopping a second time says nothing, so it is taken
                // and the program carries on - but only once, only when nothing of ours is at that
                // address, and never when a pause is the thing being waited for.
                if (e.ExceptionCode == Native.EXCEPTION_WX86_BREAKPOINT
                    && !_sawWow64Break
                    && !_pausing
                    && !Ours(e.ExceptionAddress))
                {
                    _sawWow64Break = true;
                    return (false, Native.DBG_CONTINUE);
                }

                return (HitBreakpoint(e.ExceptionAddress), Native.DBG_CONTINUE);

            case Native.EXCEPTION_SINGLE_STEP:
            case Native.EXCEPTION_WX86_SINGLE_STEP:
                // A step somebody asked for, or the one taken to get off a breakpoint — and both at
                // once whenever the step was asked for while sitting on one, which is the common
                // case and the one this used to get wrong.
                //
                // Re-arming has to happen first. But it also consumed the step and carried on, so
                // stepping off a breakpoint silently became continuing: the instruction ran, the
                // int3 went back, and execution went on to wherever it next stopped. For a function
                // called four hundred times that is the same breakpoint a moment later — which
                // looks precisely like a debugger whose instruction pointer refuses to move.
                //
                // Both belong to a thread, and a trap is either only when it comes from that thread.
                // Taken from anywhere, the thread stepping off its own breakpoint was reported as the
                // end of a step asked of another - and a step on another thread re-planted the int3
                // under the first one, which then hit it again without moving.
                if (_reArm is { } armed && armed.Thread == _threadId)
                {
                    _reArm = null;

                    // Only if it is still wanted. Removed while its thread was stepping off it, it came
                    // back anyway: an int3 nothing knew about, which the next pass through reported as
                    // not ours, and which had already cost the instruction its first byte.
                    bool wanted = Find(armed.Address) is not null;

                    if (wanted)
                    {
                        Plant(armed.Address);
                    }
                }

                if (_stepThread != _threadId)
                {
                    // Nobody asked this thread to step: a re-arm, or a step abandoned when something
                    // else stopped first. Nothing to report.
                    return (false, Native.DBG_CONTINUE);
                }

                // One instruction has run on the thread that was asked to step, so this is where it stops.
                _stepThread = null;

                // A managed step is many of these — single-steps that go by unmentioned until the IL
                // offset changes. Only the one that lands on the new offset falls through to report.
                if (_managedStep is { } stepping && stepping.Thread == _threadId && !AdvanceManagedStep(e.ExceptionAddress))
                {
                    return (false, Native.DBG_CONTINUE);
                }

                // The step is over, so whatever was held for it goes free. Before the stop is
                // reported, because what is reported next is a session that can be continued.
                ReleaseOthers();
                CurrentAddress = e.ExceptionAddress;
                // A waiting thread that lands where it began has not run an instruction: this is the
                // moment it came back from the kernel, about to run the next one. "Stepped to" the
                // address it was already at read as a step that had done nothing.
                Report("stopped", _stepWasWaiting && e.ExceptionAddress == _stepFrom
                    ? $"thread {_threadId} came back from the kernel at 0x{ToStatic(e.ExceptionAddress):X}; its next instruction is there"
                    : $"stepped to 0x{ToStatic(e.ExceptionAddress):X}", Reportable(e.ExceptionAddress));
                return (true, Native.DBG_CONTINUE);

            default:
                // Anything else belongs to the program. It is offered its own handlers first, so a
                // first chance is only news; an unhandled one is where the program dies, and the
                // loop stops there so it can be looked at.
                //
                // That stop is reported as a stop. It used to be reported as "exception" whether or
                // not it ended the program, and nothing listening treated that as having stopped —
                // so on a crash the loop sat waiting while the panel went on saying "running", with
                // Continue greyed out and no way to do anything but stop. An access violation is
                // the single most interesting place a debugger ever pauses, and it was the one
                // place the panel could not tell you it had.
                // A CLR DAC notification (a method was jitted, a module loaded). It carries a magic
                // first parameter and, for a JIT notification, the MethodDesc and the fresh native code
                // — the deterministic way to catch a method that had no address until it compiled. It is
                // first-chance and belongs to the runtime, so it is taken silently after being acted on.
                if (e.ExceptionCode == ClrDataNotifyException && e.FirstChance)
                {
                    OnClrNotification(e);
                    return (false, Native.DBG_CONTINUE);
                }

                // Exceptions the OS raises only because a debugger is attached — a stale handle closed,
                // a thread named, a debug string printed. The program has no handler for them and never
                // meets them unattended, so passing them back unhandled escalates a benign check to a
                // second chance and stops, or kills, a process that would have run fine. That is what
                // halted this .NET target at a STATUS_INVALID_HANDLE moments after launch, its runtime
                // closing handles as runtimes do. They are continued as handled — what a debugger is
                // meant to do — so the program runs on. A genuine unhandled fault is not in this set and
                // still stops.
                if (e.FirstChance && IsBenignDebuggerException(e.ExceptionCode))
                {
                    return (false, Native.DBG_CONTINUE);
                }

                // A managed or C++ throw the runtime catches itself — 0xE0434352 on .NET Framework,
                // 0xE06D7363 on CoreCLR and in C++. It has to go back unhandled so that handler runs,
                // but it is ordinary control flow a runtime does thousands of times, so it is not
                // reported: a log line each buries module loads, breakpoints and the actual stop under a
                // runtime's internal exception traffic — which is what made a .NET target look like a
                // fault storm rather than a running program. Only an *unhandled* one (below) is a real
                // crash and gets reported.
                if (e.FirstChance && e.ExceptionCode is Native.EXCEPTION_CPP_EH or Native.EXCEPTION_CLR_MANAGED)
                {
                    return (false, Native.DBG_EXCEPTION_NOT_HANDLED);
                }

                bool fatal = !e.FirstChance;
                if (fatal)
                {
                    _stepThread = null;
                    CurrentAddress = e.ExceptionAddress;
                }

                Report(fatal ? "stopped" : "exception",
                    $"0x{e.ExceptionCode:X8} at 0x{ToStatic(e.ExceptionAddress):X}"
                    + (fatal ? " (unhandled — the program would die here)" : " (first chance)"),
                    Reportable(e.ExceptionAddress));

                return (fatal, Native.DBG_EXCEPTION_NOT_HANDLED);
        }
    }

    /// <summary>
    /// What to do once the loader break has been taken and breakpoints are in: report it, let go of
    /// it, or run on silently to a one-shot at the program's — or the opened module's — entry point.
    ///
    /// The one that runs on returns "do not stop", so the loader break is never reported and the next
    /// stop the panel sees is the entry it asked for. A module-entry under a host has nothing to arm
    /// yet — its module has not loaded — so it runs on and <see cref="OnModuleLoaded"/> arms the
    /// one-shot when the module lands. Anything that cannot be armed falls back to the loader break
    /// rather than running to an exit with no stop at all, which would read as the setting doing
    /// nothing.
    /// </summary>
    private (bool Stop, uint Status) AtLoaderBreak(ulong loaderBreakAddress)
    {
        switch (_entryStop)
        {
            case EntryStop.DontBreak:
                return (false, Native.DBG_CONTINUE);

            case EntryStop.ProcessEntry when ArmOneShot(_processEntry, "the entry point"):
                return (false, Native.DBG_CONTINUE);

            // The main image is the module being read (no host), and it is already mapped, so its
            // entry can be armed now. Under a host the target loads later; leave it to OnModuleLoaded.
            case EntryStop.ModuleEntry when _target is null && ArmOneShot(EntryRuntime, "the module entry point"):
                return (false, Native.DBG_CONTINUE);

            case EntryStop.ModuleEntry when _target is not null:
                return (false, Native.DBG_CONTINUE);
        }

        // LoaderBreak, or an entry one-shot that could not be planted.
        // Reportable, not ToStatic. The loader break is in ntdll — never in the image being read —
        // and ToStatic hands back an address it cannot translate unchanged, so this once reported a
        // runtime address in another module as though it were a place in the listing.
        CurrentAddress = loaderBreakAddress;
        Report("stopped", "stopped at the loader break, before the program's own code", Reportable(loaderBreakAddress));
        return (true, Native.DBG_CONTINUE);
    }

    /// <summary>The opened module's entry point as a runtime address, or zero when it cannot be formed.</summary>
    private ulong EntryRuntime => _entryRva != 0 && LoadedBase != 0 ? LoadedBase + _entryRva : 0;

    /// <summary>
    /// Plants a one-shot at a runtime address and records it, the way run-to-cursor does. Returns
    /// whether it went in — a caller falling back to the loader break needs to know it did not.
    /// </summary>
    private bool ArmOneShot(ulong runtime, string what)
    {
        if (runtime == 0)
        {
            return false;
        }

        if (Plant(runtime, temporary: true))
        {
            _temporary = runtime;
            return true;
        }

        Report("problem", $"could not set a one-shot at {what}; stopping at the loader break instead");
        return false;
    }

    /// <summary>Whether the int3 at a runtime address is one of ours, planted or one-shot.</summary>
    private bool Ours(ulong runtime) => _temporary == runtime || Find(runtime) is not null;

    /// <summary>An int3 we planted: put the byte back, wind RIP back onto it, and stop.</summary>
    /// <summary>The runtime module — coreclr on .NET, clr on Framework — once it has loaded, or null.</summary>
    private LoadedModule? RuntimeModule()
    {
        lock (_modules)
        {
            return _modules.Values.FirstOrDefault(m =>
                m.Name.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase) ||
                m.Name.Equals("clr.dll", StringComparison.OrdinalIgnoreCase));
        }
    }

    private HeldManaged? FindPending(string type, int token)
    {
        lock (_pendingManaged)
        {
            return _pendingManaged.FirstOrDefault(h => h.Token == token && string.Equals(h.Type, type, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// One register of a thread, read straight from its context. Unlike <see cref="RegistersOf"/> this
    /// does not wait for the session to be marked stopped: the prestub dance reads rdx and rsp from
    /// inside the event handler, before a stop is reported, and every thread is already suspended there.
    /// </summary>
    private ulong RegisterValue(uint threadId, string name)
    {
        var thread = Native.OpenThread(Native.THREAD_ALL_ACCESS, false, threadId);
        if (thread == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            using var context = ThreadContext.For(_wow64);
            if (!context.Read(thread))
            {
                return 0;
            }

            foreach (var (registerName, value) in context.General())
            {
                if (registerName == name)
                {
                    return value;
                }
            }

            return 0;
        }
        finally
        {
            Native.CloseHandle(thread);
        }
    }

    /// <summary>
    /// Resolves PreStubWorker from the runtime's PDB — fetched once from the symbol server, then cached —
    /// and arms an int3 on it, so a cold managed breakpoint is caught on its <em>first</em> call rather
    /// than a later one. Started when a cold breakpoint is first held; the resolve runs off the loop, and
    /// arming follows once the address is known and the runtime is loaded. Silently a no-op when no PDB
    /// can be had — the held breakpoint is then planted by <see cref="PlantPending"/> on a later call.
    /// </summary>
    /// <param name="atModuleLoad">
    /// True when called from the runtime's own load event, where the whole target is frozen and no
    /// managed method has run yet. There the prestub is resolved on the loop thread from a PDB already on
    /// disk and armed before the process is let go — the only way to catch a once-called startup method
    /// (App.OnStartup) on its very first call, because the alternative, a background resolve, takes ~0.9 s
    /// to load the runtime PDB and loses the race to a method that JITs sooner. A cold cache (no PDB on
    /// disk) falls through to the background fetch, which warms the cache for the next run.
    /// </param>
    private void EnablePrestubCatch(bool atModuleLoad = false)
    {
        if (_prestubRva != 0)
        {
            ArmPrestubIfPending();
            return;
        }

        if (_prestubResolving || RuntimeModule() is not { } runtime)
        {
            return;
        }

        bool coldWaiting;
        lock (_pendingManaged)
        {
            coldWaiting = _pendingManaged.Count > 0;
        }

        if (atModuleLoad && coldWaiting)
        {
            var local = CoreClrSymbols.ResolvePrestub(runtime.Path, allowFetch: false);
            if (local.Ok)
            {
                _prestubRegister = local.MethodDescRegister;
                _prestubRva = local.Rva;
                ArmPrestubIfPending(announce: true);
                return;
            }
        }

        _prestubResolving = true;
        string path = runtime.Path;
        Task.Run(() =>
        {
            var info = CoreClrSymbols.ResolvePrestub(path);
            _prestubRegister = info.MethodDescRegister.Length > 0 ? info.MethodDescRegister : "rdx";
            _prestubRva = info.Rva;   // volatile write publishes the register set just above it
            _prestubResolving = false;
            if (info.Ok)
            {
                ArmPrestubIfPending(announce: true);
            }
        });
    }

    /// <summary>Plants the PreStubWorker int3 once it is resolved, the runtime is loaded and a cold
    /// breakpoint is still waiting — and not already armed, nor mid-dance.</summary>
    private void ArmPrestubIfPending(bool announce = false)
    {
        if (_prestubVa != 0 || _dancing is not null || _prestubRva == 0)
        {
            return;
        }

        int waiting;
        lock (_pendingManaged)
        {
            waiting = _pendingManaged.Count;
        }

        if (waiting == 0)
        {
            return;
        }

        if (RuntimeModule() is not { } runtime)
        {
            return;
        }

        ulong va = runtime.Base + _prestubRva;
        _prestubVa = va;
        if (!AddBreakpoint(va))
        {
            _prestubVa = 0;
            return;
        }

        // Only the first arm, from a resolve, announces itself — not the silent re-arms a dance does
        // while it steps off the worker. This is the analyst's sign the first-call catch is live before
        // any breakpoint stops, so a cold startup method not stopping is a real miss, not "not armed yet".
        if (announce)
        {
            string these = waiting == 1 ? "a cold managed breakpoint" : $"{waiting} cold managed breakpoints";
            Report("module", $"first-call catch armed for {these} — its method will stop on its first call");
        }
    }

    private void DisarmPrestub()
    {
        if (_prestubVa == 0)
        {
            return;
        }

        ulong va = _prestubVa;
        _prestubVa = 0;
        RemoveBreakpoint(va);
    }

    /// <summary>
    /// A hit on the JIT's shared prestub worker. Its second argument — rdx on x64 — is the MethodDesc
    /// being compiled. When that is a method a first-call breakpoint is waiting for, the worker is
    /// disarmed for the duration and a one-shot is set at the prestub's return, where the method will
    /// have native code; otherwise the worker re-arms and the run goes on. Never a reported stop.
    /// </summary>
    private bool PrestubHit(ulong address)
    {
        // Lift our int3 off the worker's own first byte, wind RIP back onto it, and step off so it can be
        // re-planted for the next JIT — the mechanics any breakpoint uses.
        byte original = 0;
        if (Find(address) is { } key)
        {
            lock (_breakpoints)
            {
                if (_breakpoints.TryGetValue(key, out var bp))
                {
                    original = bp.Original;
                }
            }
        }

        WriteByte(address, original);
        Rewind(address, thenStep: true);
        _reArm = (address, _threadId);
        _stepThread = null;

        ulong methodDesc = RegisterValue(_threadId, _prestubRegister);
        HeldManaged? target = _overlay?.MethodByHandle(methodDesc) is { } who ? FindPending(who.Type, who.Token) : null;
        if (target is null)
        {
            // Not one whose first call is wanted: the worker re-arms (via _reArm) and the run continues.
            return false;
        }

        // Ours. Stop watching the worker while this method compiles — its JIT can trigger others, and only
        // this one is being caught — and plant a breakpoint at the prestub's return (the address on the
        // top of the stack), recording the stack pointer so a nested JIT's return through the same shared
        // address is told apart from this method's own.
        _reArm = null;
        DisarmPrestub();

        ulong rsp = RegisterValue(_threadId, "rsp");
        ulong ret = rsp != 0 && ReadMemory(rsp, 8) is { Length: 8 } stack ? BitConverter.ToUInt64(stack) : 0;
        if (ret != 0 && AddBreakpoint(ret))
        {
            _danceReturn = ret;
            _danceRsp = rsp;
            _dancing = target;
        }
        else
        {
            // Could not set the return breakpoint; leave the method to the later-call fallback and re-arm.
            ArmPrestubIfPending();
        }

        return false;
    }

    /// <summary>
    /// A hit on the prestub's return. When the stack has unwound back to where the method's own prestub
    /// was called — not a deeper nested JIT returning through the same shared address — the method has
    /// been compiled, so the breakpoint the analyst asked for is planted now, in time for this first call
    /// to reach it. Never a reported stop of its own; the reported stop is that planted breakpoint, an
    /// instant later.
    /// </summary>
    private bool DanceReturnHit(ulong address)
    {
        // Lift the int3, wind RIP back onto it, and step off — the mechanics any breakpoint uses.
        byte original = 0;
        if (Find(address) is { } key)
        {
            lock (_breakpoints)
            {
                if (_breakpoints.TryGetValue(key, out var bp))
                {
                    original = bp.Original;
                }
            }
        }

        WriteByte(address, original);
        Rewind(address, thenStep: true);
        _reArm = (address, _threadId);
        _stepThread = null;

        // A nested JIT returning through the same shared address: its stack is still deeper than the
        // method we are catching (whose prestub was called at _danceRsp). Leave the return breakpoint
        // armed — via _reArm — and wait for the return that unwinds back to our frame.
        if (RegisterValue(_threadId, "rsp") <= _danceRsp)
        {
            return false;
        }

        // Our method's return. Take the return breakpoint out and plant the real one.
        _reArm = null;
        ulong danceReturn = _danceReturn;
        _danceReturn = 0;
        RemoveBreakpoint(danceReturn);

        var cold = _dancing;
        _dancing = null;

        if (cold is not null && _overlay is { } overlay)
        {
            // Where to plant. The method was just compiled inside the prestub, and the DAC may not have
            // caught up: on CoreCLR it has, so the resolved address lands the breakpoint exactly, at an
            // interior offset too; on .NET Framework the worker returns before the MethodDesc shows any
            // native code, so the DAC still reads it cold. But the worker's own return value — the
            // method's native entry — is in rax on both, so that is the fallback: it breaks at the entry
            // rather than an interior offset, which for a first-call catch (usually the method's start) is
            // the same place. Flush first so CoreCLR takes the exact path rather than this one.
            overlay.MarkMoved();
            var resolved = cold.Resolve(overlay);
            ulong at = resolved.Ok ? resolved.Address : RegisterValue(_threadId, "rax");
            if (at != 0 && AddBreakpoint(at))
            {
                lock (_pendingManaged)
                {
                    _pendingManaged.Remove(cold);
                }

                Report("module", $"{cold.Label} caught at its first call; its breakpoint is planted at 0x{ToStatic(at):X}");
            }
        }

        // Keep watching the worker if other cold breakpoints are still waiting.
        ArmPrestubIfPending();
        return false;
    }

    private bool HitBreakpoint(ulong address)
    {
        // The first-call machinery, before anything else and never reported: the JIT's shared prestub
        // worker (armed while a cold managed breakpoint waits) and the return one-shot of a dance in
        // progress. Each does its mechanical part and continues; the stop the analyst sees is the managed
        // breakpoint that gets planted at the end of it.
        if (_danceReturn != 0 && address == _danceReturn)
        {
            return DanceReturnHit(address);
        }

        if (_prestubVa != 0 && address == _prestubVa)
        {
            return PrestubHit(address);
        }

        // A one-shot first: step-over and run-to-cursor put it there to get here, and it goes away
        // whether or not a real breakpoint happens to be at the same address.
        if (_temporary == address)
        {
            bool oursToLift = _temporaryPlanted;
            _temporary = null;
            _temporaryPlanted = false;

            // A one-shot that rode an int3 already here does not own the byte under it — a breakpoint
            // the analyst set, or the program's own. It restores nothing; control falls through to the
            // breakpoint path below, which knows the true byte and re-arms it. The one-shot's whole job,
            // stopping here, the same int3 already did.
            if (oursToLift)
            {
                // Nothing tracks this byte any more — the one-shot has just been forgotten — so a
                // restore that fails leaves an int3 in the program for the rest of the run, with no
                // record left of what it replaced and nothing that could put it back.
                if (!WriteByte(address, _temporaryOriginal))
                {
                    Report("problem", $"the one-shot breakpoint at 0x{ToStatic(address):X} could not be taken back out");
                }

                Rewind(address, thenStep: false);

                // A managed step's one-shot — planted after a call to step over it, or at the return for
                // a step out. It goes back into the step rather than reporting a stop of its own: the
                // step is over only when the IL offset has changed or the method has returned.
                if (_managedStep is { } stepping && stepping.Thread == _threadId && !AdvanceManagedStep(address))
                {
                    return false;
                }

                // Not re-armed — that is the whole difference from a breakpoint someone set.
                CurrentAddress = address;
                Report("stopped", $"stopped at 0x{ToStatic(address):X}", Reportable(address));
                return true;
            }
        }

        // Found by which breakpoint resolves to this runtime address, rather than by keying on the
        // static one. The int3 is at a runtime address; what that translates back to is only unique
        // when there is a single module in play, and the whole point of naming modules is that there
        // is not.
        BreakpointAt? found = Find(address);
        Breakpoint? hit = null;
        if (found is { } key)
        {
            lock (_breakpoints)
            {
                _breakpoints.TryGetValue(key, out hit);
            }
        }

        if (hit is null)
        {
            // Not ours: the pause that was asked for, which Windows delivers as an int3 on a thread it
            // starts for the purpose, or one the program has of its own - a __debugbreak, an assert.
            // Either way it is a stop, and is said to be one. Reported as an "exception" it stopped
            // the loop while the panel went on saying running, with nothing to press but Stop.
            //
            // The int3 is real code here rather than one of ours over something else, so RIP stays
            // where it is, past it, instead of being wound back onto it.
            _stepThread = null;
            ReleaseOthers();
            CurrentAddress = address;

            if (_pausing)
            {
                _pausing = false;
                lock (_threads)
                {
                    _stand = _threads.ContainsKey(_mainThread) ? _mainThread : 0;
                }

                Report("stopped", "paused");
                return true;
            }

            Report("stopped", $"a breakpoint instruction at 0x{ToStatic(address):X} that we did not plant", Reportable(address));
            return true;
        }

        // Lifted so the step below carries execution off this address. If the byte will not go back the
        // int3 is still there, so the step lands on it again: the program stops in the same place
        // however many times it is continued, which reads as stepping that refuses to move.
        if (!WriteByte(address, hit.Original))
        {
            Report("problem", $"the breakpoint byte at {Describe(address)} could not be lifted to step off it");
        }

        Rewind(address, thenStep: true);

        // Re-planted after the single step that carries execution off this address; planting it now
        // would break on the instruction we are about to resume.
        _reArm = (address, _threadId);

        // A stop from here ends any step still waiting to land. That thread may trap later, and by
        // then nobody is waiting for it.
        _stepThread = null;
        CurrentAddress = address;

        // Named as it was set. Reportable is still the listing's own address and still null outside
        // the image, so a breakpoint in another module stops without pretending to be a place in the
        // one being read.
        Report("stopped", $"breakpoint at {Where(found!.Value)}", Reportable(address));
        return true;
    }

    /// <summary>
    /// Puts RIP back on the instruction the int3 replaced, and asks for a single step off it.
    ///
    /// RIP is one past the int3 that just executed, which is the middle of whatever instruction used
    /// to be there. Resuming from it would run the tail of that instruction as if it were a whole
    /// one — the same failure as a patch that overwrites half of something.
    /// </summary>
    private void Rewind(ulong address, bool thenStep)
    {
        var thread = Native.OpenThread(Native.THREAD_ALL_ACCESS, false, _threadId);
        if (thread == IntPtr.Zero)
        {
            return;
        }

        using var context = ThreadContext.For(_wow64);
        if (context.Read(thread))
        {
            context.InstructionPointer = address;
            context.SetTrapFlag(thenStep);
            context.Write(thread);
        }

        Native.CloseHandle(thread);
    }

    /// <summary>
    /// The address to hand a listing, or null when the stop is not in this image at all — in ntdll, in
    /// a system call, in another module. A number there would be read as a place in this program.
    /// </summary>
    private ulong? Reportable(ulong runtimeVa) => InsideImage(runtimeVa) ? ToStatic(runtimeVa) : null;

    /// <summary>
    /// Puts in every breakpoint whose module is loaded. Called at the loader break and again each
    /// time the module the listing is about is loaded, which for a DLL is the only moment its
    /// addresses mean anything — and can happen more than once in a run.
    /// </summary>
    private void PlantAll()
    {
        // Patches first, breakpoints on top: see WritePatches for why the order is the whole point.
        // Every patch whose module is loaded, which is no longer only the target's — WritePatches
        // skips the ones with nowhere to go yet.
        WritePatches();

        // Every breakpoint whose module is here, which is no longer the same question as whether the
        // target is. One naming another module goes in when that module arrives; one naming the target
        // still waits for the target; and RuntimeOf is what tells the two apart.
        foreach (var at in Keys())
        {
            PlantOne(at);
        }
    }

    /// <summary>Plants one breakpoint wherever it is right now, if it is anywhere yet.</summary>
    private bool PlantOne(BreakpointAt at)
    {
        ulong runtime = RuntimeOf(at);
        return runtime != 0 && Plant(runtime);
    }

    /// <summary>
    /// Plants every breakpoint that names a module, for the moment it loads. Returns how many went in.
    ///
    /// This is what makes a breakpoint in a second or third DLL possible at all: the loader announces
    /// each mapping before it runs any of that module's code, so a breakpoint planted here is already
    /// standing in the module's entry point the first time it is entered.
    /// </summary>
    private int PlantNaming(string module)
    {
        int armed = 0;
        foreach (var at in Keys())
        {
            if (at.Module is not null
                && string.Equals(at.Module, module, StringComparison.OrdinalIgnoreCase)
                && PlantOne(at))
            {
                armed++;
            }
        }

        return armed;
    }

    /// <summary>
    /// Writes the patches that name a module, for the moment it loads.
    ///
    /// Called before that module's breakpoints are planted, for the reason
    /// <see cref="WritePatches"/> gives: a breakpoint landing on a patched byte has to save the
    /// patched byte, so that a removal puts the patch back rather than the file's own byte.
    /// </summary>
    private void WritePatchesNaming(string module)
    {
        List<PatchAt> held;
        lock (_patches)
        {
            held = _patches.Keys.ToList();
        }

        foreach (var at in held)
        {
            if (at.Module is null || !string.Equals(at.Module, module, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            LivePatch? patch;
            lock (_patches)
            {
                _patches.TryGetValue(at, out patch);
            }

            ulong start = RuntimeOf(at);
            if (patch is not null && start != 0 && !WriteBytes(start, patch.Bytes))
            {
                Report("problem", $"the patch at {Where(at)} could not be written");
            }
        }
    }

    /// <summary>
    /// Forgets where a module's breakpoints were, without forgetting the breakpoints.
    ///
    /// Used when that module goes away. The bytes they saved belong to a mapping that no longer
    /// exists, and writing one back later would put a byte from the last load into whatever occupies
    /// that address now. Only that module's: a breakpoint in another one is still exactly where it
    /// was, and forgetting its byte would mean restoring a zero over a live instruction later.
    /// </summary>
    /// <param name="module">The module that went, or null for the one the listing is about.</param>
    private void Unplant(string? module)
    {
        lock (_breakpoints)
        {
            foreach (var at in _breakpoints.Keys.ToList())
            {
                bool theirs = module is null
                    ? at.Module is null
                    : string.Equals(at.Module, module, StringComparison.OrdinalIgnoreCase);

                if (theirs)
                {
                    _breakpoints[at] = _breakpoints[at] with { Original = 0, Planted = false };
                }
            }
        }
    }

    /// <summary>
    /// Puts an int3 at an address and remembers the byte it replaced. A temporary one is remembered
    /// separately, because it is removed on the first hit rather than kept and re-armed.
    /// </summary>
    private bool Plant(ulong address, bool temporary = false)
    {
        byte[] existing = ReadMemory(address, 1);
        if (existing.Length != 1)
        {
            Report("problem", $"could not read {Describe(address)} to put a breakpoint there");
            return false;
        }

        // Which breakpoint this address belongs to, resolved once. A one-shot belongs to none of them,
        // which is the whole difference between it and a breakpoint somebody set.
        BreakpointAt? key = temporary ? null : Find(address);

        // A one-shot replacing one that was set but never hit — a run-to abandoned when another stop
        // came first, a step begun over the top of it. Its int3 goes back now, before this plant
        // overwrites the record of what it replaced, so it is not left orphaned in the code.
        if (temporary && _temporary is { } pending && pending != address && _temporaryPlanted)
        {
            WriteByte(pending, _temporaryOriginal);
            _temporary = null;
            _temporaryPlanted = false;
        }

        if (existing[0] == 0xCC)
        {
            // Already an int3. Either ours from an earlier plant, in which case the byte it replaced
            // is recorded and must not be overwritten with 0xCC; or the program's own, in which case
            // 0xCC is genuinely what belongs here and is what has to go back when this is hit or
            // removed. Recording nothing left Original at zero, and restoring it wrote a zero byte
            // over the program's instruction — a debugger corrupting the thing it is watching.
            if (key is { } already)
            {
                lock (_breakpoints)
                {
                    if (_breakpoints.TryGetValue(already, out var had) && !had.Planted)
                    {
                        _breakpoints[already] = had with { Original = 0xCC, Planted = true };
                    }
                }
            }

            // A one-shot onto an int3 already here plants nothing and records nothing: the byte under
            // it is the owner's, not the one-shot's to restore.
            if (temporary)
            {
                _temporaryPlanted = false;
            }

            return true;
        }

        if (temporary)
        {
            _temporaryOriginal = existing[0];
            _temporaryPlanted = true;
        }
        else if (key is null)
        {
            // Asked before the write, not after. Nothing to record the byte against means nothing may
            // replace it: an int3 whose original is kept nowhere is a byte of the program lost for
            // good.
            return false;
        }

        // Said rather than assumed. This used to write and return true regardless, because WriteByte
        // discarded the result of WriteProcessMemory — so a page that would not take the byte produced
        // a breakpoint that listed as planted, never fired, and gave nobody a reason why.
        if (!WriteByte(address, 0xCC))
        {
            Report("problem", $"could not put a breakpoint at {Describe(address)}: the write was refused");
            return false;
        }

        // Marked planted only now that the int3 is really there, and the byte it replaced recorded
        // beside it — which is what a removal puts back.
        if (key is { } mine)
        {
            lock (_breakpoints)
            {
                if (_breakpoints.TryGetValue(mine, out var breakpoint))
                {
                    _breakpoints[mine] = breakpoint with { Original = existing[0], Planted = true };
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Writes bytes into the debuggee, lifting the page's protection if it will not take them
    /// otherwise. False when they did not all go in.
    ///
    /// A plain <c>WriteProcessMemory</c> is enough for an ordinary code section — mapped
    /// execute-read, and the kernel writes through it — which is why this was never needed before.
    /// It is not enough everywhere. A JIT's code pages are execute-read through a shared double
    /// mapping under W^X, and read-only data is read-only, and both refuse the write with
    /// ERROR_NOACCESS. So a refusal is retried with the page made execute-readwrite, and the
    /// protection is put straight back: leaving it open would quietly undo the runtime's own
    /// guarantees about its code for the rest of the run.
    ///
    /// Every caller has to look at the answer. A write that silently did nothing is a breakpoint
    /// reported as planted that never fires, or a patch reported as applied that is not there.
    /// </summary>
    private unsafe bool Write(ulong address, byte* bytes, nuint length)
    {
        if (_process == IntPtr.Zero || length == 0)
        {
            return false;
        }

        if (Native.WriteProcessMemory(_process, address, bytes, length, out nuint wrote) && wrote == length)
        {
            Native.FlushInstructionCache(_process, address, length);
            return true;
        }

        if (!Native.VirtualProtectEx(_process, address, length, Native.PageExecuteReadWrite, out uint previous))
        {
            return false;
        }

        bool written;
        try
        {
            written = Native.WriteProcessMemory(_process, address, bytes, length, out wrote) && wrote == length;
        }
        finally
        {
            Native.VirtualProtectEx(_process, address, length, previous, out _);
        }

        if (written)
        {
            Native.FlushInstructionCache(_process, address, length);
        }

        return written;
    }

    private bool WriteByte(ulong address, byte value)
    {
        unsafe
        {
            byte b = value;
            return Write(address, &b, 1);
        }
    }

    private bool WriteBytes(ulong address, IReadOnlyList<byte> bytes)
    {
        byte[] buffer = bytes as byte[] ?? bytes.ToArray();
        unsafe
        {
            fixed (byte* p = buffer)
            {
                return Write(address, p, (nuint)buffer.Length);
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _loop?.Join(TimeSpan.FromSeconds(2));
        _stopping.Dispose();
        _resume.Dispose();
    }
}
