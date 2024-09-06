using Microsoft.Extensions.Logging;
using Npgsql;
using NUnit.Framework.Internal;
using OneOf;
using Testcontainers.PostgreSql;

namespace MyDotNetEventStore.Tests;

// TODO:
// - Test that we are properly releasing connections

[SetUpFixture]
public class PostgresEventStoreSetup
{
    private readonly PostgreSqlContainer _postgresContainer = new PostgreSqlBuilder()
        .Build();

    public static NpgsqlConnection Connection;

    [OneTimeSetUp]
    public async Task OneTimeSetup()
    {
        await _postgresContainer.StartAsync();

        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Trace));
        NpgsqlLoggingConfiguration.InitializeLogging(loggerFactory);
        NpgsqlLoggingConfiguration.InitializeLogging(loggerFactory, parameterLoggingEnabled: true);

        Connection = new NpgsqlConnection(_postgresContainer.GetConnectionString());

        await Connection.OpenAsync();

        var command = new NpgsqlCommand($"""
                                         CREATE TABLE IF NOT EXISTS events (
                                             position SERIAL PRIMARY KEY,
                                             stream_id TEXT NOT NULL,
                                             revision BIGINT NOT NULL,
                                             event_type TEXT NOT NULL,
                                             data JSONB,
                                             metadata JSONB,
                                             UNIQUE (stream_id, revision)
                                         );
                                         """, Connection);

        await command.ExecuteNonQueryAsync();
    }


    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await Connection.DisposeAsync();
        await _postgresContainer.DisposeAsync();
    }
}

public class EventStoreTest
{
    private EventStore _eventStore;

    [SetUp]
    public void Setup()
    {
        _eventStore = new EventStore(PostgresEventStoreSetup.Connection);
    }

    [TearDown]
    public async Task TearDown()
    {
        var command = new NpgsqlCommand("DELETE FROM events", PostgresEventStoreSetup.Connection);
        await command.ExecuteNonQueryAsync();
        command = new NpgsqlCommand("ALTER SEQUENCE events_position_seq RESTART WITH 1;", PostgresEventStoreSetup.Connection);
        await command.ExecuteNonQueryAsync();
    }

    [TestFixture]
    public class KnowingIfAStreamExists : EventStoreTest
    {
        
        public class WhenTheStreamDoesntExist : KnowingIfAStreamExists
        {

            [Test]
            public async Task returns_StreamExistence_NotFound()
            {
                var streamExist = await _eventStore.StreamExist("a-stream-that-doesnt-exists");

                Assert.That(streamExist, Is.EqualTo(StreamExistence.NotFound));
            }
        }

        public class WhenTheStreamExists : KnowingIfAStreamExists
        {

            [Test]
            public async Task returns_StreamExistence_Exists()
            {
                await _eventStore.AppendAsync("a-stream", AnEvent().ToEventData());

                var streamExist = await _eventStore.StreamExist("a-stream");

                Assert.That(streamExist, Is.EqualTo(StreamExistence.Exists));
            }
        }
        
    }

    [TestFixture]
    public class ReadingStream : EventStoreTest
    {
        public class WhenTheStreamDoesntExists : ReadingStream
        {

            [Test]
            public async Task returns_a_ReadStreamResult_without_any_events()
            {
                var readStreamResult = _eventStore.ReadStreamAsync("a-stream-that-doesnt-exists");

                var resolvedEvents = await ToListAsync(readStreamResult);

                Assert.That(resolvedEvents.Count(),  Is.EqualTo(0));
            }
        }

        public class WhenTheStreamExists : ReadingStream
        {

            [Test]
            public async Task returns_all_events_appended_to_the_stream_in_order()
            {
                var evt1 = AnEvent();
                var evt2 = AnEvent();
                var evt3 = AnEvent();

                await _eventStore.AppendAsync("stream-id", evt1.ToEventData());
                await _eventStore.AppendAsync("stream-id", evt2.ToEventData());
                await _eventStore.AppendAsync("stream-id", evt3.ToEventData());

                var readStreamResult = _eventStore.ReadStreamAsync("stream-id");

                var resolvedEvents = await ToListAsync(readStreamResult);

                Assert.That(resolvedEvents, Is.EqualTo(new List<ResolvedEvent>
                {
                    evt1.ToResolvedEvent(1,1),
                    evt2.ToResolvedEvent(2, 2),
                    evt3.ToResolvedEvent(3, 3)
                }));
            }

            [Test]
            public async Task returns_all_events_appended_to_the_stream_in_order_PARAMETRIZABLE(
                [Values(3, 200, 600)] int eventCount)
            {
                var eventBuilders = ListOfNBuilders(eventCount);

                await _eventStore.AppendAsync("stream-id", eventBuilders.ToEventData());

                var readStreamResult = _eventStore.ReadStreamAsync("stream-id");

                var resolvedEvents = await ToListAsync(readStreamResult);

                Assert.That(resolvedEvents, Is.EqualTo(eventBuilders.ToResolvedEventsOfSameStream()));
            }

            [Test]
            public async Task doesnt_return_events_appended_to_another_stream()
            {
                var evtInStream = AnEvent();

                await _eventStore.AppendAsync("stream-id", evtInStream.ToEventData());
                await _eventStore.AppendAsync("another-stream-id", AnEvent().ToEventData());

                var readStreamResult = _eventStore.ReadStreamAsync("stream-id");

                var readEvents = await ToListAsync(readStreamResult);

                Assert.That(readEvents, Is.EqualTo(new List<ResolvedEvent>
                {
                    evtInStream.ToResolvedEvent(1, 1),
                }));
            }
            
            [Test]
            // This is probably not a good way to test memory consumption but at least that test forced me to
            // fetch events by batch
            public async Task keeps_memory_footprint_low_even_with_a_lot_of_events()
            {
                var eventCount = 378;
                await _eventStore.AppendAsync("stream-id", ListOfNEvents(eventCount));

                long memoryBefore = GC.GetTotalMemory(true);

                var readStreamResult = _eventStore.ReadStreamAsync("stream-id");

                var count = 0;
                await foreach (var _ in readStreamResult)
                {
                    count++;
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                var memoryAfter = GC.GetTotalMemory(true);

                var memoryUsed = memoryAfter - memoryBefore;

                var acceptableMemoryUsage = 1 * 1024 * 1024; // 1 MB

                Assert.Less(memoryUsed, acceptableMemoryUsage, $"Memory usage exceeded: {memoryUsed} bytes used, but the limit is {acceptableMemoryUsage} bytes.");
                Assert.That(count, Is.EqualTo(eventCount));
            }
        }
    }

    [TestFixture]
    public class ReadingAllStream : EventStoreTest
    {
        // Todo: Test cancellation token stops enumeration

        [Test]
        public async Task returns_all_events_appended_to_all_streams_in_order()
        {
            var evt1 = AnEvent();
            var evt2 = AnEvent();
            var evt3 = AnEvent();
            var evt4 = AnEvent();

            await _eventStore.AppendAsync("stream-id1", evt1.ToEventData());
            await _eventStore.AppendAsync("stream-id2", evt2.ToEventData());
            await _eventStore.AppendAsync("stream-id3", evt3.ToEventData());
            await _eventStore.AppendAsync("stream-id1", evt4.ToEventData());

            var readAllStreamResult = _eventStore.ReadAllAsync();

            var resolvedEventList = await ToListAsync<ResolvedEvent>(readAllStreamResult);

            Assert.That(resolvedEventList, Is.EqualTo(new List<ResolvedEvent>
            {
                evt1.ToResolvedEvent(1, 1),
                evt2.ToResolvedEvent(2, 1),
                evt3.ToResolvedEvent(3, 1),
                evt4.ToResolvedEvent(4, 2),
            }));
        }

        [Test]
        // This is probably not a good way to test memory consumption but at least that test forced me to
        // fetch events by batch
        public async Task keeps_memory_footprint_low_even_with_a_lot_of_events()
        {
            await _eventStore.AppendAsync("stream-id1", ListOfNEvents(1000));
            await _eventStore.AppendAsync("stream-id2", ListOfNEvents(1000));
            await _eventStore.AppendAsync("stream-id3", ListOfNEvents(1000));
            await _eventStore.AppendAsync("stream-id1", ListOfNEvents(1000));

            long memoryBefore = GC.GetTotalMemory(true);

            var readAllStreamResult =  _eventStore.ReadAllAsync();

            var count = 0;

            await foreach (var _ in readAllStreamResult)
            {
                count++;
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var memoryAfter = GC.GetTotalMemory(true);

            var memoryUsed = memoryAfter - memoryBefore;

            var acceptableMemoryUsage = 2 * 1024 * 1024; // 1 MB

            Assert.Less(memoryUsed, acceptableMemoryUsage, $"Memory usage exceeded: {memoryUsed} bytes used, but the limit is {acceptableMemoryUsage} bytes.");
            Assert.That(count, Is.EqualTo(4000));
        }
    }

    [TestFixture]
    public class AppendingEvents : EventStoreTest
    {
        public class PerformsConcurrencyChecks : AppendingEvents
        {
            [TestFixture]
            public class WithStreamStateNoStream : PerformsConcurrencyChecks
            {
                [Test]
                public async Task Doesnt_allow_to_write_to_an_existing_stream(
                    [Values] CountOfEvents countEvents)
                {
                    var events = BuildEvents(countEvents).ToEventData();

                    await _eventStore.AppendAsync("stream-id", (dynamic)events);

                    var exception = Assert.ThrowsAsync<ConcurrencyException>(async () =>
                        await _eventStore.AppendAsync("stream-id", (dynamic)events, StreamState.NoStream()));

                    Assert.That(exception.Message, Is.EqualTo("Stream 'stream-id' already exists."));
                }

                [Test]
                public Task Allows_to_write_to_a_non_existing_stream(
                    [Values] CountOfEvents countEvents)
                {
                    Assert.DoesNotThrowAsync(async () =>
                    {
                        await _eventStore.AppendAsync("a-non-existing-id", (dynamic)BuildEvents(countEvents).ToEventData(), StreamState.NoStream());
                    });

                    return Task.CompletedTask;
                }
            }

            public class WithStreamStateStreamExists : PerformsConcurrencyChecks
            {
                [Test]
                public Task Doesnt_allow_to_write_to_an_non_existing_stream(
                    [Values] CountOfEvents countEvents)
                {
                    var exception = Assert.ThrowsAsync<ConcurrencyException>(async () =>
                        await _eventStore.AppendAsync("a-non-existing-id", (dynamic)BuildEvents(countEvents).ToEventData(), StreamState.StreamExists()));

                    Assert.That(exception.Message, Is.EqualTo("Stream 'a-non-existing-id' doesn't exists."));
                    return Task.CompletedTask;
                }

                [Test]
                public async Task Allows_to_write_to_an_existing_stream(
                    [Values] CountOfEvents countEvents)
                {
                    await _eventStore.AppendAsync("stream-id", (dynamic)BuildEvents(countEvents).ToEventData());

                    Assert.DoesNotThrowAsync(async () =>
                    {
                        await _eventStore.AppendAsync("stream-id", (dynamic)BuildEvents(countEvents).ToEventData(), StreamState.StreamExists());
                    });
                }
            }

            public class WithStreamStateAny : PerformsConcurrencyChecks
            {
                [Test]
                public async Task Allow_to_write_to_an_existing_stream(
                    [Values] CountOfEvents countEvents)
                {
                    await _eventStore.AppendAsync("stream-id", (dynamic)BuildEvents(countEvents).ToEventData());

                    Assert.DoesNotThrowAsync(async () =>
                    {
                        await _eventStore.AppendAsync("stream-id", (dynamic)BuildEvents(countEvents).ToEventData(), StreamState.Any());
                    });
                }

                [Test]
                public Task Allows_to_write_to_a_non_existing_stream(
                    [Values] CountOfEvents countEvents)
                {
                    Assert.DoesNotThrowAsync(async () =>
                    {
                        await _eventStore.AppendAsync("a-non-existing-id", (dynamic)BuildEvents(countEvents).ToEventData(), StreamState.Any());
                    });

                    return Task.CompletedTask;
                }
            }

            [TestFixture]
            public class WithARevisionNumber : PerformsConcurrencyChecks
            {
                [Test]
                public Task Doesnt_allow_to_write_to_non_existing_stream(
                    [Values] CountOfEvents countEvents)
                {
                    var events = BuildEvents(countEvents).ToEventData();

                    var exception = Assert.ThrowsAsync<ConcurrencyException>(async () =>
                        await _eventStore.AppendAsync("a-non-existing-stream-id", (dynamic)events, StreamState.AtRevision(1)));

                    Assert.That(exception.Message, Is.EqualTo("Stream 'a-non-existing-stream-id' doesn't exists."));
                    return Task.CompletedTask;
                }

                [Test]
                public async Task Doesnt_allow_to_write_to_stream_at_a_different_revision(
                    [Values] CountOfEvents countEvents,
                    [Random(0, 1000, 1)] int alreadyAppendedEventCount,
                    [Random(1, 100, 1)] int deltaRevision)
                {
                    var pastEvents = ListOfNEvents(alreadyAppendedEventCount);
                    var triedRevision = alreadyAppendedEventCount + deltaRevision;

                    await _eventStore.AppendAsync("stream-id", (dynamic)pastEvents, StreamState.NoStream());

                    var exception = Assert.ThrowsAsync<ConcurrencyException>(async () =>
                        await _eventStore.AppendAsync("stream-id", (dynamic) BuildEvents(countEvents).ToEventData(), StreamState.AtRevision(triedRevision)));

                    Assert.That(exception.Message, Is.EqualTo($"Stream 'stream-id' is at revision {alreadyAppendedEventCount}. You tried appending events at revision {triedRevision}."));
                }

                [Test]
                public async Task Allows_to_write_to_a_stream_at_the_expected_revision(
                    [Values] CountOfEvents countEvents,
                    [Random(0, 1000, 1)] int alreadyAppendedEventCount)
                {
                    var pastEvents = ListOfNEvents(alreadyAppendedEventCount);

                    await _eventStore.AppendAsync("stream-id", (dynamic)pastEvents, StreamState.NoStream());

                    Assert.DoesNotThrowAsync(async () =>
                    {
                        await _eventStore.AppendAsync("stream-id", (dynamic)BuildEvents(countEvents).ToEventData(), StreamState.AtRevision(alreadyAppendedEventCount));
                    });
                }
            }

        }

        [TestFixture]
        public class MaintainsStreamRevision : AppendingEvents
        {
            [Test]
            public async Task Adds_first_event_in_stream_at_revision_1()
            {
                var appendedEvent = AnEvent().ToEventData();
                await _eventStore.AppendAsync("stream-id", appendedEvent);

                var readStreamResult = _eventStore.ReadStreamAsync("stream-id");

                var resolvedEventList = await ToListAsync<ResolvedEvent>(readStreamResult);

                Assert.That(resolvedEventList.First().Revision, Is.EqualTo(1));
            }

            [Test]
            public async Task Last_event_in_stream_revision_is_equal_to_the_count_of_inserted_events_when_all_events_are_added_at_once(
                [Random(0, 1000, 5)] int eventCount)
            {
                var appendedEvent = ListOfNEvents(eventCount);
                await _eventStore.AppendAsync("stream-id", appendedEvent);

                var readStreamResult = _eventStore.ReadStreamAsync("stream-id");

                var resolvedEvents = await ToListAsync(readStreamResult);

                Assert.That(resolvedEvents.Last().Revision, Is.EqualTo(eventCount));
            }

            [Test]
            public async Task Last_event_in_stream_revision_is_equal_to_the_count_of_inserted_events_when_events_are_in_multiple_times(
                [Random(0, 100, 2)] int eventCount1,
                [Random(0, 100, 1)] int eventCount2,
                [Random(0, 100, 1)] int eventCount3
                )
            {
                await _eventStore.AppendAsync("stream-id", ListOfNEvents(eventCount1));
                await _eventStore.AppendAsync("stream-id", ListOfNEvents(eventCount2));
                await _eventStore.AppendAsync("stream-id", ListOfNEvents(eventCount3));

                var readStreamResult = _eventStore.ReadStreamAsync("stream-id");

                var resolvedEvents = await ToListAsync(readStreamResult);

                Assert.That(resolvedEvents.Last().Revision, Is.EqualTo(eventCount1 + eventCount2 + eventCount3));
            }

            [Test]
            public async Task Returns_a_AppendResult_with_Position_and_Revision()
            {
                await _eventStore.AppendAsync("stream-1", ListOfNEvents(10));

                await _eventStore.AppendAsync("stream-2", ListOfNEvents(10));

                await _eventStore.AppendAsync("stream-3", ListOfNEvents(10));

                var appendResult = await _eventStore.AppendAsync("stream-2", ListOfNEvents(10));

                Assert.That(appendResult, Is.EqualTo(new AppendResult(40, 20)));
            }

        }
    }

    private static OneOf<EventBuilder, List<EventBuilder>> BuildEvents(CountOfEvents countEvents)
    {
        if (countEvents == CountOfEvents.One)
            return AnEvent();

        return MultipleEvents();
    }

    private static List<EventBuilder> MultipleEvents()
    {
        var evt1 = AnEvent();
        var evt2 = AnEvent();
        var evt3 = AnEvent();
        return [evt1, evt2, evt3];
    }

    private static List<EventData> ListOfNEvents(int eventCount)
    {
        var events = new List<EventData>();

        for (int i = 0; i < eventCount; i++)
        {
            events.Add(AnEvent().ToEventData());
        }

        return events;
    }

    private static List<EventBuilder> ListOfNBuilders(int eventCount)
    {
        var events = new List<EventBuilder>();

        for (int i = 0; i < eventCount; i++)
        {
            events.Add(new EventBuilder());
        }

        return events;
    }

    public enum CountOfEvents
    {
        One,
        Multiple
    }

    private static T SelectRandom<T>(List<T> elements)
    {
        var random = new Random();
        var randomIndex = random.Next(elements.Count);
        return elements[randomIndex];
    }


    private static EventBuilder AnEvent()
    {
        return new EventBuilder();
    }


    public class EventBuilder
    {
        private readonly string _eventType;
        private readonly string _data;
        private readonly string _metadata;

        public EventBuilder()
        {
            var fakeEventTypes = new List<string> { "event-type-1", "event-type-2", "event-type-3" };
            var fakeEventData = new List<string> { "{}", "{\"id\": \"1234567\"}"};
            var fakeEventMetaData = new List<string> { "{}", "{\"userId\": \"u-345678\", \"causationId\": \"98697678\", \"correlationId\": \"12345\"}"};

            _eventType = SelectRandom(fakeEventTypes);
            _data = SelectRandom(fakeEventData);
            _metadata = SelectRandom(fakeEventMetaData);
        }

        public EventData ToEventData()
        {
            return new EventData(_eventType, _data, _metadata);
        }

        public ResolvedEvent ToResolvedEvent(int position, int revision)
        {
            return new ResolvedEvent(position, revision, _eventType, _data, _metadata);
        }
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> asyncEnumerable)
    {
        var list = new List<T>();

        await foreach (var item in asyncEnumerable)
        {
            list.Add(item);
        }

        return list;

    }
}

public enum StreamExistence
{
    Exists,
    NotFound
}

public static class EventBuilderExtensions
{
    public static object ToEventData(this OneOf<EventStoreTest.EventBuilder, List<EventStoreTest.EventBuilder>> oneOf)
    {
        return oneOf.Match<OneOf<EventData, List<EventData>>>(
            e => e.ToEventData(),
            e => e.ToEventData()).Value;
    }

    public static List<EventData> ToEventData(this List<EventStoreTest.EventBuilder> eventBuilders)
    {
        return eventBuilders.Select(builder => builder.ToEventData()).ToList();
    }

    public static List<ResolvedEvent> ToResolvedEventsOfSameStream(this List<EventStoreTest.EventBuilder> eventBuilders)
    {
        var i = 0;
        return eventBuilders.Select(builder =>
        {
            i++;
            return builder.ToResolvedEvent(i, i);
        }).ToList();
    }
}
