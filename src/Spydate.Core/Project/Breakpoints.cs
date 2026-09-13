namespace Spydate.Core.Project;

/// <summary>
/// The breakpoints recorded for one image, as RVAs.
///
/// A breakpoint is only a place to stop, so — unlike a <see cref="Patch"/> — it carries nothing but
/// where. It is kept as an RVA for the same reason everything else in a project is: that is the
/// address the file itself states, stable whatever base the image is examined or loaded at. A managed
/// breakpoint's method and IL offset are not stored; they are re-derived from the RVA through the body
/// map when the breakpoint is restored, exactly as they were when it was set.
///
/// It exists so the marks in the gutter survive closing and reopening the binary, which is what a
/// person expects of them and what every other debugger does. It mirrors <see cref="PatchStore"/> so a
/// save can merge rather than overwrite: two windows on one file, or a window beside an agent, each
/// keep the breakpoints the other set.
/// </summary>
public sealed class BreakpointStore
{
    private readonly SortedSet<uint> _rvas = new();
    private readonly HashSet<uint> _changed = new();
    private readonly Lock _lock = new();

    public bool IsDirty { get; private set; }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _rvas.Count;
            }
        }
    }

    /// <summary>Addresses touched this session, so a save merges rather than overwrites.</summary>
    public IReadOnlyCollection<uint> ChangedAddresses
    {
        get
        {
            lock (_lock)
            {
                return _changed.ToArray();
            }
        }
    }

    public bool Contains(uint rva)
    {
        lock (_lock)
        {
            return _rvas.Contains(rva);
        }
    }

    /// <summary>Records a breakpoint. True when it was not already there.</summary>
    public bool Add(uint rva)
    {
        lock (_lock)
        {
            if (!_rvas.Add(rva))
            {
                return false;
            }

            _changed.Add(rva);
            IsDirty = true;
            return true;
        }
    }

    /// <summary>Takes one back out. True when it was there.</summary>
    public bool Remove(uint rva)
    {
        lock (_lock)
        {
            if (!_rvas.Remove(rva))
            {
                return false;
            }

            _changed.Add(rva);
            IsDirty = true;
            return true;
        }
    }

    /// <summary>Every breakpoint, in address order.</summary>
    public IReadOnlyList<uint> Snapshot()
    {
        lock (_lock)
        {
            return _rvas.ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (uint rva in _rvas)
            {
                _changed.Add(rva);
            }

            IsDirty |= _rvas.Count > 0;
            _rvas.Clear();
        }
    }

    public void MarkSaved()
    {
        lock (_lock)
        {
            _changed.Clear();
            IsDirty = false;
        }
    }
}
