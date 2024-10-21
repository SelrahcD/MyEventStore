using System.Diagnostics.Metrics;

namespace MyDotNetEventStore;

public static class Metrics
{
    private static readonly Meter Meter = new("MyDotNetEventStore");

    public static readonly Counter<long> AppendedEventCounter = Meter.CreateCounter<long>("AppendedEvents", "event", "Number of events appended to the Event Store");
}