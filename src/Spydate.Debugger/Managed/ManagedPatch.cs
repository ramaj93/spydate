using System.Collections.Immutable;

namespace Spydate.Debugger.Managed;

/// <summary>
/// A byte change to make to a method's IL as its module loads, before any of its code has run.
///
/// Named by a method token and an offset into its IL rather than by an address, for two reasons. The
/// first is the same one breakpoints have: a method's IL has no fixed address until the runtime hands
/// one out, and the token and offset are what survive the file being rebuilt and the method being
/// recompiled. The second is that the IL is not where a file RVA would put it — a managed image is
/// not mapped section-for-section into the process the way a native one is, so the only reliable way
/// to the bytes is through the runtime's own IL-code object for the method. See
/// <see cref="ManagedDebugSession.ApplyOnLoad"/>.
///
/// The moment matters more than for a native patch. A managed method is not run from its IL; it is
/// run from the native code the JIT produces the first time it is called, and once that has happened
/// changing the IL changes nothing. So a managed patch is only ever written at module load, which is
/// the one point at which the IL is in memory and none of the module's methods has been compiled.
/// </summary>
/// <param name="Module">The file name of the module the method is in, as the runtime reports it.</param>
/// <param name="MethodToken">The method's metadata token (a <c>mdMethodDef</c>).</param>
/// <param name="IlOffset">Where in the method's IL to write, as the offset a listing prints.</param>
/// <param name="Bytes">What to write.</param>
/// <param name="Original">
/// What the patch expects to find there. When it is the same length as <see cref="Bytes"/> the IL is
/// read back and checked against it first, so a patch cut against IL the running method no longer has
/// — a recompiled or precompiled method — is refused rather than written blind. Empty skips the check.
/// </param>
public sealed record ManagedPatch(
    string Module,
    uint MethodToken,
    uint IlOffset,
    ImmutableArray<byte> Bytes,
    ImmutableArray<byte> Original = default)
{
    /// <summary>The bytes expected to be there to compare against, or empty when none was given.</summary>
    public ImmutableArray<byte> Expected => Original.IsDefault ? ImmutableArray<byte>.Empty : Original;
}
