using System.Runtime.ExceptionServices;
using System.Text;
using Spydate.Core.Jvm;
using Spydate.Core.Project;
using Spydate.Decompiler.Native.IR;
using Spydate.Decompiler.Native.Structuring;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>
/// The in-house Java view: a class or one member as Java-shaped pseudo-code. Each method is lifted
/// (<see cref="JvmLifter"/>), tidied (<see cref="JavaInliner"/>), structured with its try regions
/// (<see cref="JavaRegions"/>) and printed (<see cref="JavaEmitter"/>) under the names the project gave its
/// classes and members.
///
/// The work runs on a thread of its own with a large stack. Every tree is bounded where it is built, and this is
/// the second guard: the structurer recurses once per nesting level of the method, and a crafted method nested
/// thousands deep must cost a slow answer, not the process. One method that fails any other way is printed as a
/// comment saying so, and the rest of the class still prints.
/// </summary>
internal static class JavaDecompiler
{
    private const int StackSize = 256 * 1024 * 1024;

    public static string Type(JvmReading reading, JvmType type, CancellationToken cancellationToken)
        => OnLargeStack(() => new JavaClassWriter(reading, type, cancellationToken).Class(), cancellationToken);

    public static string Member(JvmReading reading, JvmType type, JvmMember member, CancellationToken cancellationToken)
        => OnLargeStack(() => new JavaClassWriter(reading, type, cancellationToken).Member(member), cancellationToken);

    private static string OnLargeStack(Func<string> work, CancellationToken cancellationToken)
    {
        string? result = null;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    result = work();
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
            },
            StackSize)
        {
            IsBackground = true,
            Name = "Java decompiler",
        };

        thread.Start();
        thread.Join();
        failure?.Throw();
        cancellationToken.ThrowIfCancellationRequested();
        return result ?? string.Empty;
    }
}
