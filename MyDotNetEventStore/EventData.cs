namespace MyDotNetEventStore;

public record EventData
{
    public string Data { get; }
    public string MetaData { get; }
    public string EventType { get; }
    public Guid Id { get; }

    public EventData(string eventType, string data, string metaData, Guid id = new Guid())
    {
        Id = id;
        Data = data;
        MetaData = metaData;
        EventType = eventType;
    }
}