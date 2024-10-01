namespace MyDotNetEventStore;

public enum StreamStateType
{
    NoStream,
    Any,
    StreamExists,
    AtRevision
}

public record StreamState
{
    public StreamStateType Type { get; }
    public long ExpectedRevision { get; }

    private StreamState(StreamStateType type, long expectedRevision)
    {
        Type = type;
        ExpectedRevision = expectedRevision;
    }

    public static StreamState NoStream() => new(StreamStateType.NoStream, 0L);
    public static StreamState Any() => new(StreamStateType.Any, 0L);
    public static StreamState StreamExists() => new(StreamStateType.StreamExists, 0L);

    public static StreamState AtRevision(long revision) => new StreamState(StreamStateType.AtRevision, revision);
}