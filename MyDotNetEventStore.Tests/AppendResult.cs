namespace MyDotNetEventStore.Tests;

public record AppendResult
{
    private readonly long _position;
    private readonly long _revision;

    public AppendResult(long position, long revision)
    {
        _position = position;
        _revision = revision;
    }
}