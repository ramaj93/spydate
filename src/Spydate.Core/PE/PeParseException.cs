namespace Spydate.Core.PE;

/// <summary>Thrown when a file cannot be interpreted as a PE image (fatal structural error).</summary>
public sealed class PeParseException : Exception
{
    public PeParseException(string message) : base(message)
    {
    }

    public PeParseException(string message, Exception inner) : base(message, inner)
    {
    }
}
