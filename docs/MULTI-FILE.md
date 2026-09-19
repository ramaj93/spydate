# MULTI-FILE.md — several binaries open at once

Spydate opens one file. This is the design for opening several, each with its own explorer tree,
its own documents, its own annotations and its own debuggee, switched between by a tab strip below
the toolbar.

The shape is Notepad++'s: one strip of file tabs across the top, and everything below it belongs to
whichever tab is forward.

---

## 1. The constraint people expect to block this

A Windows process has exactly one **debug port**, and `docs/MIXED-MODE.md` §1 says the part that
matters: one process can have only one debugger, and that is unavoidable.

That is a rule about the **debuggee**, not about the debugger. Five tabs debugging five different
processes is five ports, one each, with Spydate the sole debugger of every one. Nothing in Windows
limits how many processes a debugger may own — it is how Visual Studio debugs a multi-project
solution.

The detail that does shape the design is a level below that. The debug object lives in the
**debugger thread's** TEB, created on that thread's first attach. Every process a given thread
debugs shares that one object, and `WaitForDebugEvent` on it returns their events *interleaved*.
Two sessions sharing a pump thread would therefore see each other's stops, and each would try to
continue a process it does not own.

They do not share one. `DebugSession` starts a dedicated `spydate-debug` thread per instance and
has no mutable static state, so N sessions are N debug objects and N independent event streams.
The managed side is the same: `ManagedDebugSession` owns its own `ICorDebug`, its startup callback
carries its session as a parameter rather than through a static, and `Interop` hops to the pool per
call rather than funnelling onto a shared thread.

This was argued from the API contract before it was observed. `ConcurrentSessionTests` is what
turns it into an observed fact, and it was written before the refactor below rather than after.

Where the one-debugger rule still bites, under tabs:

- **The same pid twice.** Starting the same executable in two tabs launches two separate processes,
  which is fine. `Attach to Process…` must refuse a pid another tab already owns — that is the
  forbidden case, and it is the reason the menu item stays disabled until it can check.
- **Native against managed within one tab** is unchanged. Still one engine per debuggee, for the
  .NET Core interop-removal reason in `MIXED-MODE.md` §1.

## 2. What belongs to a file, and what belongs to the window

`CloseFile()` was already an accidental specification of this. The eleven things it clears are
exactly the things a tab owns:

```
Documents · _modules · _imports · _symbolsWarmed · Explorer
Warnings · ActiveDocument · history · Binary · AnalysisText · _analysisCts
```

**Per file** — the `OpenedBinary` (image, analysis, decompiler, patches, breakpoints, annotations),
the explorer tree, the document tabs and the active one, back/forward history, Xrefs, Warnings, the
Patches list, analysis status and its cancellation, the import map, the module cache, **and its own
debugger**.

**Per window** — the Output log, recent files, the status bar, the assistant, the tool-window
layout, preferences.

The dividing question is not "is there one of these on screen" but "would two files disagree about
it". There is one Output panel, but two files do not disagree about what was logged — it is a
transcript of the session, not a property of a binary. There is also one Debug panel, but two files
very much do disagree about where they are stopped, so the panel shows the active tab's debugger
rather than being one.

## 3. The design

`MainViewModel` splits. Per-file state moves into `FileViewModel`, one per tab; `MainViewModel`
keeps the window-wide state and a `Files` collection with an `Active`. Roughly 1,400 of its 2,167
lines move, and about 18 bindings re-target from `{Binding Documents}` to `{Binding Active.Documents}`.

`WorkspaceService.Current` becomes `Open` plus `Current`, with one project-file watcher per open
file instead of one in total.

There is a cheaper route that was considered and rejected: keep one `MainViewModel` and save and
restore its collections as tabs switch. It loses scroll position and caret per document, pays a
rebuild on every switch, and saves work only in the first commit. The extraction is the honest shape
and it is where the code was already heading.

### The debugger gets smaller

`DebuggerViewModel` stops being a singleton and is born with its file, taking the `OpenedBinary`
instead of the `WorkspaceService`. This is a simplification rather than a cost:

- Twenty `if (_workspace.Current is not { } binary)` guards collapse to a readonly field.
- `RecallTarget()` runs once in the constructor instead of on every workspace change.
- The `CurrentChanged` → `StopSession()` handler is **deleted rather than fixed**. It is the reason
  switching tabs would otherwise kill your debuggee, and per-file ownership removes the question
  instead of answering it.

The `StoppedAt`, `NavigateRequested` and `BreakpointsChanged` wiring moves out of `MainViewModel`'s
constructor into the file that owns it, so `ShowWhereItStopped` uses *its own* analysis rather than
reaching for whatever is current. `ReloadDocuments()` narrows to the owning file at the same time;
redrawing every document in every tab on each breakpoint toggle would be quadratic in open files.

`AssistantViewModel` stays window-wide and reaches the active file's debugger through an
indirection. It uses it in exactly one place, building `PanelDebugSettings`.

### A stop in a tab that is not forward

With several live debuggees this becomes a real question, and the answer is not "always switch".
A background process hitting a breakpoint while you are reading another file should not yank the
window away from you.

**Switch when the stop answers a command issued in that tab** — start, step, continue, run to
cursor — which is nearly every stop. **Mark the tab and write the status bar** for a hit nobody
asked for, and the reader goes when they want. The `Arrive` path carries the flag.

## 4. Opening a file when one is already open

The prompt lives in `OpenPathAsync` rather than in the Open command, so File ▸ Open, Open recent and
drag-and-drop all behave the same way. Startup with a file on the command line never prompts,
because nothing is open yet, and a file already open in a tab does not prompt either — that focuses
the existing tab.

It asks after the file has been picked, so the file is chosen first and its destination second:

> **Where should `where.exe` go?**
> **[ Open in a new tab ]** · [ Replace current tab ] · [ Cancel ]

New tab is the default and the focused button: it is the non-destructive one, and it is what every
tabbed editor does. Replace closes the current tab in place, keeping its position in the strip.

Annotations need no warning here — they already save silently on close, and have since
`SaveAnnotationsIfDirty`. A **live debug session** does need one, because replacing its tab kills
the process, so that option carries the warning inline rather than firing a second modal after the
choice has been made.

This adds the first general-purpose choice dialog to `IFileDialogService`, built as a reusable
three-button chooser because closing a tab that owns a session wants the same thing.

## 5. Modules are a preference, not a default

Stepping into an imported DLL opens it as documents **inside the current tab**, exactly as it does
today. That stays the default. Tabs appearing unbidden while stepping is not a feature.

Under **Settings ▸ Preferences ▸ Debugging**:

> **When execution enters another module**
> ○ Open it in the current tab *(default)*
> ○ Open it as its own file tab

Because the default path stays, `_modules`, `ModuleNameOf` and the `module:`/`modulec:` key prefixes
all remain — they are live code, not leftovers — and `ExecutionAddress` stays window-wide rather
than becoming per-file.

`Preferences` joins `RecentFiles` and `DebugTargets` in `Spydate.Core/Project`: JSON under
`%LocalAppData%\Spydate\`, never throwing on a damaged file, because a corrupt preference is worth
losing silently and is certainly not worth failing to start over. Living in `Spydate.Core` also
means the store is reachable by tests, even though the window that edits it is not.

## 6. Phases

Each one ships and is verifiable on its own.

| # | What | Shape |
|---|------|-------|
| 0 | `ConcurrentSessionTests` — two debuggees at once | The claim in §1, observed |
| 1 | Extract `FileViewModel` | Pure refactor; one file open; window unchanged |
| 2 | Tab strip, multi-file `WorkspaceService`, open-destination dialog, `Preferences` store | The feature becomes visible |
| 3 | Concurrent debugging | 22 `{Binding Debugger.X}` → `Active.Debugger.X` |
| 4 | Preferences window — General and Debugging pages | Module-tab option, "don't ask again" |

All four are done. Two things came out differently from the sketch above:

- **A module tab does not run discovery.** Walking the whole of a system DLL to fill a tree nobody
  asked for costs seconds and hundreds of megabytes, and the functions actually wanted are the ones
  execution reaches, which arrive one at a time anyway. Its tree still has sections, imports and
  exports; what it lacks is a complete function list.
- **A module tab is read-only in a sharper sense than "no patches".** It is not in the workspace's
  open set, nothing writes a project file for it, and its own debugger never starts anything — the
  process belongs to the file that is running. The arrow is drawn there from an address, not from a
  session of its own.

Phase 0 is first because it is the cheapest thing that could invalidate the rest, and finding that
out after the phase 1 refactor would be the expensive order.

The "don't ask again" checkbox deliberately does **not** ship with the dialog in phase 2. A
suppressible prompt whose window cannot yet unsuppress it strands the reader with a hand-edited
`preferences.json`, so the checkbox appears in phase 4 alongside the page that can untick it.

## 7. What this costs, and what could still go wrong

- **Memory.** Each tab holds a `PeImage` and a full `BinaryAnalysis`. Several ntdll-sized modules is
  real memory, and the phase 4 preference can open them automatically. There is no eviction policy
  and this design does not propose one; it proposes knowing the number.
- **Processes.** Several live debuggees, each with a pump thread. Closing a tab terminates its
  session; closing the window terminates all of them. The disposal chain is the thing to get right,
  because the failure mode is an orphaned process running an untrusted binary.
- **`Spydate.App` has no tests**, so phases 1–4 are verified by driving the window (AGENTS.md §5.1).
  Phase 0 is the exception and that is the point of putting it first: the one genuinely unproven
  claim sits in `Spydate.Debugger`, which the test project references.

## 8. Not in this design

- **More than one window.** Tabs, not windows. Detaching a tab into its own window is a different
  feature and a much larger one.
- **A session list.** The Debug panel shows the active tab's debugger. It does not gain a chooser
  for looking at a background debuggee without switching to it.
- **Restoring the open set.** Reopening the tabs from last time is worth having and is not here.
