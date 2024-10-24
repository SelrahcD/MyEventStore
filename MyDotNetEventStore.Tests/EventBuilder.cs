using OneOf;

namespace MyDotNetEventStore.Tests;

public class EventBuilder
{
    private readonly string _eventType;
    private readonly string _data;
    private readonly string _metadata;
    private string _streamId;
    private int _position = 1;
    private int _revision = 1;

    public EventBuilder()
    {
        var fakeEventTypes = new List<string> { "event-type-1", "event-type-2", "event-type-3" };
        var fakeEventData = new List<string> { "{}", "{\"id\": \"1234567\"}" };
        var fakeEventMetaData = new List<string>
            { "{}", "{\"userId\": \"u-345678\", \"causationId\": \"98697678\", \"correlationId\": \"12345\"}" };

        _eventType = SelectRandom(fakeEventTypes);
        _data = SelectRandom(fakeEventData);
        _metadata = SelectRandom(fakeEventMetaData);
        _streamId = "stream-&" + new Random().Next(1, 100);
    }

    public EventData ToEventData()
    {
        return new EventData(_eventType, _data, _metadata);
    }

    public ResolvedEvent ToResolvedEvent(int position, int revision)
    {
        return new ResolvedEvent(position, revision, _eventType, _data, _metadata, _streamId);
    }

    public ResolvedEvent ToResolvedEvent()
    {
        return new ResolvedEvent(_position, _revision, _eventType, _data, _metadata, _streamId);
    }

    public string StreamId()
    {
        return _streamId;
    }

    public EventBuilder InStream(string streamId)
    {
        _streamId = streamId;

        return this;
    }

    private static T SelectRandom<T>(List<T> elements)
    {
        var random = new Random();
        var randomIndex = random.Next(elements.Count);
        return elements[randomIndex];
    }

    public EventBuilder WithCoherentRevisionsAndPositions(Dictionary<string, int> revisions)
    {
        var streamId = StreamId();
        revisions.TryAdd(streamId, 0);

        revisions[streamId]++;

        _revision = revisions[streamId];
        _position = revisions.Sum((x) => x.Value);

        return this;
    }

    public static Dictionary<string, int> RevisionTracker()
    {
        return new Dictionary<string, int>();
    }

    public static implicit operator EventData(EventBuilder b) => b.ToEventData();
    public static implicit operator ResolvedEvent(EventBuilder b) => b.ToResolvedEvent();
}

public class EventBuilders
{
    private readonly List<EventBuilder> _builders;

    public EventBuilders(List<EventBuilder> builders)
    {
        _builders = builders;
    }

    public List<EventBuilder> ToList()
    {
        return _builders;
    }

    public List<EventData> ToEventData()
    {
        return _builders.Select(builder => builder.ToEventData()).ToList();
    }

    public List<ResolvedEvent> ToResolvedEvents()
    {
        return _builders.Select(builder => builder.ToResolvedEvent()).ToList();
    }

    public async Task AppendTo(EventStore eventStore)
    {
        foreach (var eventBuilder in _builders)
        {
            await eventStore.AppendAsync(eventBuilder.StreamId(), eventBuilder.ToEventData());
        }
    }

    public EventBuilders GetRange(int i, int i1)
    {
        return new EventBuilders(_builders.GetRange(i, i1));
    }

    public static implicit operator List<EventData>(EventBuilders l) => l.ToEventData();

    public static implicit operator List<ResolvedEvent>(EventBuilders l) => l.ToResolvedEvents();
}

public static class EventBuilderExtensions
{
    public static object ToEventData(this OneOf<EventBuilder, List<EventBuilder>> oneOf)
    {
        return oneOf.Match<OneOf<EventData, List<EventData>>>(
            e => e.ToEventData(),
            e => e.ToEventData()).Value;
    }

    public static List<EventData> ToEventData(this List<EventBuilder> eventBuilders)
    {
        return eventBuilders.Select(builder => builder.ToEventData()).ToList();
    }

    public static List<ResolvedEvent> ToResolvedEvents(this List<EventBuilder> eventBuilders)
    {
        return eventBuilders.Select(builder => builder.ToResolvedEvent()).ToList();
    }

    public static async Task AppendTo(this List<EventBuilder> eventBuilders, EventStore eventStore)
    {
        foreach (var eventBuilder in eventBuilders)
        {
            await eventStore.AppendAsync(eventBuilder.StreamId(), eventBuilder.ToEventData());
        }
    }

}