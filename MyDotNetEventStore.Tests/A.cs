namespace MyDotNetEventStore.Tests;

public static class A
{
    public static EventBuilders ListOfNEvents(int eventCount)
    {
        return ListOfNEvents(eventCount, e => e);
    }

    public static EventBuilders ListOfNEvents(int eventCount,
        EventBuilderConfigurator eventBuilderConfiguratorConfigurator)
    {
        return ListOfNEvents(eventCount, eventBuilderConfiguratorConfigurator, EventBuilder.RevisionTracker());
    }

    public static EventBuilders ListOfNEvents(int eventCount,
        EventBuilderConfigurator eventBuilderConfiguratorConfigurator, Dictionary<string, int> revisionTracker)
    {
        var eventBuilders = new List<EventBuilder>();
        for (var i = 0; i < eventCount; i++)
        {
            eventBuilders.Add(eventBuilderConfiguratorConfigurator(new EventBuilder())
                .WithCoherentRevisionsAndPositions(revisionTracker));
        }

        return new EventBuilders(eventBuilders);
    }

    public delegate EventBuilder EventBuilderConfigurator(EventBuilder eventBuilder);
}