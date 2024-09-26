namespace MyDotNetEventStore.Tests;

public record AppendResult
{
    public readonly long Position;
    public readonly long Revision;

    public AppendResult(long position, long revision)
    {
        Position = position;
        Revision = revision;
    }
}