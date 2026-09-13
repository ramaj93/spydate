namespace Spydate.Debugger.Managed;

/// <summary>
/// One thread of a stopped .NET process, as a Threads window shows it.
/// </summary>
/// <param name="Id">The operating system's thread id.</param>
/// <param name="ManagedId">
/// <c>Thread.ManagedThreadId</c>, read out of the thread object. Null for a thread the runtime knows
/// about but that has no <c>Thread</c> object yet.
/// </param>
/// <param name="Category">Main Thread, Worker Thread, Thread Pool, or Unknown.</param>
/// <param name="Name"><c>Thread.Name</c>, or empty.</param>
/// <param name="Location">Where it is: module, method and IL offset, or that it is not in managed code.</param>
/// <param name="Priority">Its scheduling priority, as Windows names it.</param>
/// <param name="AppDomain">The app domain it is in, with its id.</param>
/// <param name="State">What the runtime says it is doing: Background, WaitSleepJoin, and so on.</param>
/// <param name="IsStopped">The thread whose event stopped the process.</param>
/// <param name="IsSelected">The thread being looked at — what values, the arrow and stepping follow.</param>
public sealed record ManagedThread(
    uint Id,
    int? ManagedId,
    string Category,
    string Name,
    string Location,
    string Priority,
    string AppDomain,
    string State,
    bool IsStopped,
    bool IsSelected);
