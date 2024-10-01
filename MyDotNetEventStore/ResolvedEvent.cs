namespace MyDotNetEventStore;

public record ResolvedEvent
{
    public long Position { get; }
    public string Data { get; }
    public string MetaData { get; }
    public string EventType { get; }
    public long Revision { get; }
    public string StreamId { get; }

    public ResolvedEvent(long position, long revision, string eventType, string data, string metaData, string streamId)
    {
        Position = position;
        Data = data;
        MetaData = metaData;
        EventType = eventType;
        Revision = revision;
        StreamId = streamId;
    }
}