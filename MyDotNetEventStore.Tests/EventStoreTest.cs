using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;
using NUnit.Framework.Internal;
using OneOf;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Testcontainers.PostgreSql;

namespace MyDotNetEventStore.Tests;

// TODO:
// - Test that we are properly releasing connections
// - Test cancellation token stops enumeration

[SetUpFixture]
public class PostgresEventStoreSetup
{
    private readonly PostgreSqlContainer _postgresContainer = new PostgreSqlBuilder()
        .Build();

    private TracerProvider _tracerProvider;
    private MeterProvider _metricProvider;

    public static NpgsqlConnection Connection;

    [OneTimeSetUp]
    public async Task OneTimeSetup()
    {
         _tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("MyDotNetEventStore")).AddSource("MyDotNetEventStore", "EventStoreTest")
            .AddOtlpExporter(exporter =>
            {
                exporter.Endpoint = new Uri("http://localhost:5341/ingest/otlp/v1/traces");
                exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
            })
            .AddOtlpExporter(exporter =>
            {
                exporter.Endpoint = new Uri("http://localhost:4317");
                exporter.Protocol = OtlpExportProtocol.Grpc;
            })
            .Build();

         _metricProvider = Sdk.CreateMeterProviderBuilder()
             .AddMeter("MyDotNetEventStore")
             .ConfigureResource(resource =>
             {
                 resource.AddService("MyDotNetEventStore");
             })
             .AddOtlpExporter(exporter =>
             {
                 exporter.Endpoint = new Uri("http://localhost:4317");
                 exporter.Protocol = OtlpExportProtocol.Grpc;
             })
             .Build();

        await _postgresContainer.StartAsync();

        // var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Trace));
        // NpgsqlLoggingConfiguration.InitializeLogging(loggerFactory);
        // NpgsqlLoggingConfiguration.InitializeLogging(loggerFactory, parameterLoggingEnabled: true);

        Connection = new NpgsqlConnection(_postgresContainer.GetConnectionString());

        await Connection.OpenAsync();

        await EventStoreSchema.BuildSchema(Connection);
    }


    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        _tracerProvider.Dispose();
        _metricProvider.Dispose();
        await Connection.DisposeAsync();
        await _postgresContainer.DisposeAsync();
    }
}

public class EventStoreTest
{
    private static readonly ActivitySource ActivitySource = new("EventStoreTest");

    private EventStore _eventStore;

    private Activity _activity;

    [SetUp]
    public void Setup()
    {
        var testName = TestContext.CurrentContext.Test.Name;

        _activity = ActivitySource.StartActivity(testName);

        _eventStore = new EventStore(PostgresEventStoreSetup.Connection);
    }

    [TearDown]
    public async Task TearDown()
    {
        var outcome = TestContext.CurrentContext.Result.Outcome.Status;

        switch (outcome)
        {
            case NUnit.Framework.Interfaces.TestStatus.Passed:
                _activity?.SetStatus(ActivityStatusCode.Ok, "Test passed successfully.");
                break;

            case NUnit.Framework.Interfaces.TestStatus.Failed:
                _activity?.SetStatus(ActivityStatusCode.Error, "Test failed.");
                break;

            case NUnit.Framework.Interfaces.TestStatus.Skipped:
                _activity?.SetStatus(ActivityStatusCode.Unset, "Test was skipped.");
                break;

            case NUnit.Framework.Interfaces.TestStatus.Inconclusive:
                _activity?.SetStatus(ActivityStatusCode.Unset, "Test result is inconclusive.");
                break;

            default:
                _activity?.SetStatus(ActivityStatusCode.Unset, "Test completed with unknown status.");
                break;
        }

        var command = new NpgsqlCommand("DELETE FROM events", PostgresEventStoreSetup.Connection);
        await command.ExecuteNonQueryAsync();
        command = new NpgsqlCommand("ALTER SEQUENCE events_position_seq RESTART WITH 1;",
            PostgresEventStoreSetup.Connection);
        await command.ExecuteNonQueryAsync();


        _activity.Dispose();
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
        public class ForwardWithoutProvidingAPosition : ReadingStream
        {
            [Test]
            public async Task returns_a_ReadStreamResult_without_any_events_when_the_stream_does_not_exist()
            {
                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "a-stream-that-doesnt-exists");

                var resolvedEventCount = await readStreamResult.CountAsync();

                Assert.That(resolvedEventCount, Is.EqualTo(0));
            }

            [Test]
            public async Task returns_a_ReadStreamResult_with_all_events_appended_to_the_stream_in_order(
                [Values(1, 50, 100, 270, 336)] int eventCount)
            {
                var eventBuilders = ListOfNBuilders(eventCount, (e) => e.InStream("stream-id"))
                    .ToList();

                await _eventStore.AppendAsync("stream-id", eventBuilders.ToEventData());

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "stream-id");

                var resolvedEvents = await readStreamResult.ToListAsync();

                Assert.That(resolvedEvents, Is.EqualTo(eventBuilders.ToResolvedEvents()));
            }

            [Test]
            public async Task doesnt_return_events_appended_to_another_stream()
            {
                var evtInStream = AnEvent().InStream("stream-id");

                await _eventStore.AppendAsync("stream-id", evtInStream.ToEventData());
                await _eventStore.AppendAsync("another-stream-id", AnEvent().ToEventData());

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "stream-id");

                var readEvents = await readStreamResult.ToListAsync();

                Assert.That(readEvents, Is.EqualTo(new List<ResolvedEvent>
                {
                    evtInStream.ToResolvedEvent(1, 1),
                }));
            }
        }

        public class ForwardProvidingAPosition : ReadingStream
        {
            [Test]
            public async Task returns_a_ReadStreamResult_without_any_events_when_the_stream_does_not_exist()
            {
                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "a-stream-that-doesnt-exists", 10);

                var resolvedEventCount = await readStreamResult.CountAsync();

                Assert.That(resolvedEventCount, Is.EqualTo(0));
            }

            [Test]
            public async Task returns_a_ReadStreamResult_without_any_events_when_the_stream_has_less_events_than_the_requested_revision()
            {
                var eventBuilders = ListOfNBuilders(5, (e) => e.InStream("stream-id"))
                    .ToList();

                await _eventStore.AppendAsync("stream-id", eventBuilders.ToEventData());

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "stream-id", 6);

                var resolvedEventCount = await readStreamResult.CountAsync();

                Assert.That(resolvedEventCount, Is.EqualTo(0));
            }

            [Test]
            public async Task returns_a_ReadStreamResult_with_all_events_with_a_revision_greater_or_equal_to_the_requested_revision()
            {
                var eventsBeforeRequestedRevision = ListOfNBuilders(5, (e) => e.InStream("stream-id"))
                    .ToList();
                var eventAfterRequestedRevision = ListOfNBuilders(115, (e) => e.InStream("stream-id"))
                    .ToList();

                await _eventStore.AppendAsync("stream-id", eventsBeforeRequestedRevision.ToEventData());
                await _eventStore.AppendAsync("stream-id", eventAfterRequestedRevision.ToEventData());

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "stream-id", 6);

                var resolvedEvents = await readStreamResult.ToListAsync();

                Assert.That(resolvedEvents, Is.EqualTo(eventAfterRequestedRevision.ToResolvedEvents(6, 6)));
            }

            [Test]
            public async Task returns_a_ReadStreamResult_with_all_events_when_the_requested_revision_is_StreamRevision_Start()
            {
                var events = ListOfNBuilders(115, (e) => e.InStream("stream-id"))
                    .ToList();

                await _eventStore.AppendAsync("stream-id", events.ToEventData());

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "stream-id", StreamRevision.Start);

                var resolvedEvents = await readStreamResult.ToListAsync();

                Assert.That(resolvedEvents, Is.EqualTo(events.ToResolvedEvents()));
            }

            [Test]
            public async Task returns_a_ReadStreamResult_without_events_when_the_requested_revision_is_StreamRevision_End()
            {
                var events = ListOfNBuilders(5, (e) => e.InStream("stream-id"))
                    .ToList();

                await _eventStore.AppendAsync("stream-id", events.ToEventData());

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "stream-id", StreamRevision.End);

                var resolvedEventCount = await readStreamResult.CountAsync();

                Assert.That(resolvedEventCount, Is.EqualTo(0));
            }
        }

        public class BackwardWithoutProvidingAPosition : ReadingStream
        {
            [Test]
            public async Task returns_a_ReadStreamResult_without_any_events_when_the_stream_does_not_exist()
            {
                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Backward, "a-stream-that-doesnt-exists");

                var resolvedEventCount = await readStreamResult.CountAsync();

                Assert.That(resolvedEventCount, Is.EqualTo(0));
            }

            [Test]
            public async Task returns_a_ReadStreamResult_with_all_events_appended_to_the_stream_in_reverse_order(
                [Values(1, 50, 100, 270, 336)] int eventCount)
            {
                var eventBuilders = ListOfNBuilders(eventCount, (e) => e.InStream("stream-id"))
                    .ToList();

                await _eventStore.AppendAsync("stream-id", eventBuilders.ToEventData());

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Backward, "stream-id");

                var resolvedEvents = await readStreamResult.ToListAsync();

                var expectedEvents = eventBuilders.ToResolvedEvents();
                expectedEvents.Reverse();

                Assert.That(resolvedEvents, Is.EqualTo(expectedEvents));
            }

            [Test]
            public async Task doesnt_return_events_appended_to_another_stream()
            {
                var evtInStream = AnEvent().InStream("stream-id");

                await _eventStore.AppendAsync("stream-id", evtInStream.ToEventData());
                await _eventStore.AppendAsync("another-stream-id", AnEvent().ToEventData());

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Backward, "stream-id");

                var readEvents = await readStreamResult.ToListAsync();

                Assert.That(readEvents, Is.EqualTo(new List<ResolvedEvent>
                {
                    evtInStream.ToResolvedEvent(1, 1),
                }));
            }
        }

        public class BackwardProvidingAPosition : ReadingStream
        {
            [Test]
            public async Task returns_a_ReadStreamResult_without_any_events_when_the_stream_does_not_exist()
            {
                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Backward, "a-stream-that-doesnt-exists", 10);

                var resolvedEventCount = await readStreamResult.CountAsync();

                Assert.That(resolvedEventCount, Is.EqualTo(0));
            }

            [Test]
            public async Task returns_a_ReadStreamResult_with_all_events_in_reverse_order_when_the_requested_revision_is_greater_than_the_current_revision()
            {
                var eventBuilders = ListOfNBuilders(5, (e) => e.InStream("stream-id"))
                    .ToList();

                await _eventStore.AppendAsync("stream-id", eventBuilders.ToEventData());

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Backward, "stream-id", 10);

                var resolvedEvents = await readStreamResult.ToListAsync();

                var expectedEvents = eventBuilders.ToResolvedEvents();
                expectedEvents.Reverse();
                Assert.That(resolvedEvents, Is.EqualTo(expectedEvents));
            }

            [Test]
            public async Task returns_a_ReadStreamResult_with_all_events_with_a_revision_lesser_or_equal_to_the_requested_revision_in_reverse_order()
            {
                var eventsBeforeRequestedRevision = ListOfNBuilders(115, (e) => e.InStream("stream-id"))
                    .ToList();
                var eventAfterRequestedRevision = ListOfNBuilders(20, (e) => e.InStream("stream-id"))
                    .ToList();

                await _eventStore.AppendAsync("stream-id", eventsBeforeRequestedRevision.ToEventData());
                await _eventStore.AppendAsync("stream-id", eventAfterRequestedRevision.ToEventData());

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Backward, "stream-id", 115);

                var resolvedEvents = await readStreamResult.ToListAsync();

                var expectedEvents = eventsBeforeRequestedRevision.ToResolvedEvents();
                expectedEvents.Reverse();

                Assert.That(resolvedEvents, Is.EqualTo(expectedEvents));
            }

            [Test]
            public async Task returns_a_ReadStreamResult_with_all_events_in_reverse_order_when_the_requested_revision_is_StreamRevision_End()
            {
                var eventBuilders = ListOfNBuilders(115, (e) => e.InStream("stream-id"))
                    .ToList();

                await _eventStore.AppendAsync("stream-id", eventBuilders.ToEventData());

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Backward, "stream-id", StreamRevision.End);

                var resolvedEvents = await readStreamResult.ToListAsync();

                var expectedEvents = eventBuilders.ToResolvedEvents();
                expectedEvents.Reverse();
                Assert.That(resolvedEvents, Is.EqualTo(expectedEvents));
            }

            [Test]
            public async Task returns_a_ReadStreamResult_without_events_in_reverse_order_when_the_requested_revision_is_StreamRevision_Start()
            {
                var eventBuilders = ListOfNBuilders(115, (e) => e.InStream("stream-id"))
                    .ToList();

                await _eventStore.AppendAsync("stream-id", eventBuilders.ToEventData());

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Backward, "stream-id", StreamRevision.Start);

                var resolvedEventCount = await readStreamResult.CountAsync();

                Assert.That(resolvedEventCount, Is.EqualTo(0));
            }

        }

    }

    [TestFixture]
    public class ReadingAllStream : EventStoreTest
    {
        public class ForwardWithoutProvidingAPosition : ReadingAllStream
        {
            [Test]
            public async Task returns_all_events_appended_to_all_streams_in_order(
                [Values(1, 3, 50, 100, 187, 200, 270, 600)]
                int eventCount)
            {
                var eventBuilders = ListOfNBuilders(eventCount).ToList();

                foreach (var eventBuilder in eventBuilders)
                {
                    await _eventStore.AppendAsync(eventBuilder.StreamId(), eventBuilder.ToEventData());
                }

                var readStreamResult = _eventStore.ReadAllAsync(Direction.Forward);

                var resolvedEvents = await readStreamResult.ToListAsync();

                var resolvedEventsOfMultiplesStreams = eventBuilders.ToResolvedEvents();
                Assert.That(resolvedEvents, Is.EqualTo(resolvedEventsOfMultiplesStreams));
            }

        }

        public class ForwardProvidingAPosition : ReadingAllStream
        {
            [Test]
            public async Task
                returns_a_ReadStreamResult_with_all_events_when_the_requested_revision_is_StreamRevision_Start()
            {
                var eventBuilders = ListOfNBuilders(10).ToList();

                foreach (var eventBuilder in eventBuilders)
                {
                    await _eventStore.AppendAsync(eventBuilder.StreamId(), eventBuilder.ToEventData());
                }

                var readStreamResult = _eventStore.ReadAllAsync(Direction.Forward, StreamRevision.Start);

                var resolvedEventCount = await readStreamResult.CountAsync();

                Assert.That(resolvedEventCount, Is.EqualTo(10));
            }

            // [Test]
            // public async Task returns_a_ReadStreamResult_without_any_events_when_the_stream_has_less_events_than_the_requested_revision()
            // {
            //     var eventBuilders = ListOfNBuilders(5, (e) => e.InStream("stream-id"))
            //         .ToList();
            //
            //     await _eventStore.AppendAsync("stream-id", eventBuilders.ToEventData());
            //
            //     var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "stream-id", 6);
            //
            //     var resolvedEventCount = await readStreamResult.CountAsync();
            //
            //     Assert.That(resolvedEventCount, Is.EqualTo(0));
            // }
            //
            // [Test]
            // public async Task returns_a_ReadStreamResult_with_all_events_with_a_revision_greater_or_equal_to_the_requested_revision()
            // {
            //     var eventsBeforeRequestedRevision = ListOfNBuilders(5, (e) => e.InStream("stream-id"))
            //         .ToList();
            //     var eventAfterRequestedRevision = ListOfNBuilders(115, (e) => e.InStream("stream-id"))
            //         .ToList();
            //
            //     await _eventStore.AppendAsync("stream-id", eventsBeforeRequestedRevision.ToEventData());
            //     await _eventStore.AppendAsync("stream-id", eventAfterRequestedRevision.ToEventData());
            //
            //     var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "stream-id", 6);
            //
            //     var resolvedEvents = await readStreamResult.ToListAsync();
            //
            //     Assert.That(resolvedEvents, Is.EqualTo(eventAfterRequestedRevision.ToResolvedEvents(6, 6)));
            // }
            //
            // [Test]
            // public async Task returns_a_ReadStreamResult_with_all_events_when_the_requested_revision_is_StreamRevision_Start()
            // {
            //     var events = ListOfNBuilders(115, (e) => e.InStream("stream-id"))
            //         .ToList();
            //
            //     await _eventStore.AppendAsync("stream-id", events.ToEventData());
            //
            //     var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "stream-id", StreamRevision.Start);
            //
            //     var resolvedEvents = await readStreamResult.ToListAsync();
            //
            //     Assert.That(resolvedEvents, Is.EqualTo(events.ToResolvedEvents()));
            // }
            //
            // [Test]
            // public async Task returns_a_ReadStreamResult_without_events_when_the_requested_revision_is_StreamRevision_End()
            // {
            //     var events = ListOfNBuilders(5, (e) => e.InStream("stream-id"))
            //         .ToList();
            //
            //     await _eventStore.AppendAsync("stream-id", events.ToEventData());
            //
            //     var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "stream-id", StreamRevision.End);
            //
            //     var resolvedEventCount = await readStreamResult.CountAsync();
            //
            //     Assert.That(resolvedEventCount, Is.EqualTo(0));
            // }
        }


        public class BackwardWithoutProvidingAPosition : ReadingAllStream
        {
            [Test]
            public async Task returns_all_events_appended_to_all_streams_in_reverse_order(
                [Values(1, 3, 50, 100, 187, 200, 270, 600)]
                int eventCount)
            {
                var eventBuilders = ListOfNBuilders(eventCount).ToList();

                foreach (var eventBuilder in eventBuilders)
                {
                    await _eventStore.AppendAsync(eventBuilder.StreamId(), eventBuilder.ToEventData());
                }

                var readStreamResult = _eventStore.ReadAllAsync(Direction.Backward);

                var resolvedEvents = await readStreamResult.ToListAsync();

                var resolvedEventsOfMultiplesStreams = eventBuilders.ToResolvedEvents();
                resolvedEvents.Reverse();
                Assert.That(resolvedEvents, Is.EqualTo(resolvedEventsOfMultiplesStreams));
            }
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
                        await _eventStore.AppendAsync("a-non-existing-id",
                            (dynamic)BuildEvents(countEvents).ToEventData(), StreamState.NoStream());
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
                        await _eventStore.AppendAsync("a-non-existing-id",
                            (dynamic)BuildEvents(countEvents).ToEventData(), StreamState.StreamExists()));

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
                        await _eventStore.AppendAsync("stream-id", (dynamic)BuildEvents(countEvents).ToEventData(),
                            StreamState.StreamExists());
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
                        await _eventStore.AppendAsync("stream-id", (dynamic)BuildEvents(countEvents).ToEventData(),
                            StreamState.Any());
                    });
                }

                [Test]
                public Task Allows_to_write_to_a_non_existing_stream(
                    [Values] CountOfEvents countEvents)
                {
                    Assert.DoesNotThrowAsync(async () =>
                    {
                        await _eventStore.AppendAsync("a-non-existing-id",
                            (dynamic)BuildEvents(countEvents).ToEventData(), StreamState.Any());
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
                        await _eventStore.AppendAsync("a-non-existing-stream-id", (dynamic)events,
                            StreamState.AtRevision(1)));

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
                        await _eventStore.AppendAsync("stream-id", (dynamic)BuildEvents(countEvents).ToEventData(),
                            StreamState.AtRevision(triedRevision)));

                    Assert.That(exception.Message,
                        Is.EqualTo(
                            $"Stream 'stream-id' is at revision {alreadyAppendedEventCount}. You tried appending events at revision {triedRevision}."));
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
                        await _eventStore.AppendAsync("stream-id", (dynamic)BuildEvents(countEvents).ToEventData(),
                            StreamState.AtRevision(alreadyAppendedEventCount));
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

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "stream-id");

                var resolvedEventList = await ToListAsync(readStreamResult);

                Assert.That(resolvedEventList.First().Revision, Is.EqualTo(1));
            }

            [Test]
            public async Task
                Last_event_in_stream_revision_is_equal_to_the_count_of_inserted_events_when_all_events_are_added_at_once(
                    [Random(0, 1000, 5)] int eventCount)
            {
                var appendedEvent = ListOfNEvents(eventCount);
                await _eventStore.AppendAsync("stream-id", appendedEvent);

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "stream-id");

                var resolvedEvents = await readStreamResult.ToListAsync();

                Assert.That(resolvedEvents.Last().Revision, Is.EqualTo(eventCount));
            }

            [Test]
            public async Task
                Event_revision_is_the_position_of_the_event_in_the_stream()
            {
                var event1 = AnEvent();
                await _eventStore.AppendAsync("stream-id", event1.ToEventData());
                await _eventStore.AppendAsync("another-stream-id", AnEvent().ToEventData());
                var event2 = AnEvent();
                await _eventStore.AppendAsync("stream-id", event2.ToEventData());
                await _eventStore.AppendAsync("yet-another-stream-id", AnEvent().ToEventData());
                var event3 = AnEvent();
                await _eventStore.AppendAsync("stream-id", event3.ToEventData());

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "stream-id");

                var resolvedEvents = await readStreamResult.ToListAsync();

                Assert.That(resolvedEvents[0], Is.EqualTo(event1.InStream("stream-id").ToResolvedEvent(1, 1)));
                Assert.That(resolvedEvents[1], Is.EqualTo(event2.InStream("stream-id").ToResolvedEvent(3, 2)));
                Assert.That(resolvedEvents[2], Is.EqualTo(event3.InStream("stream-id").ToResolvedEvent(5, 3)));
            }


            [Test]
            public async Task
                Last_event_in_stream_revision_is_equal_to_the_count_of_inserted_events_when_events_are_in_multiple_times(
                    [Random(0, 100, 2)] int eventCount1,
                    [Random(0, 100, 1)] int eventCount2,
                    [Random(0, 100, 1)] int eventCount3
                )
            {
                await _eventStore.AppendAsync("stream-id", ListOfNEvents(eventCount1));
                await _eventStore.AppendAsync("stream-id", ListOfNEvents(eventCount2));
                await _eventStore.AppendAsync("stream-id", ListOfNEvents(eventCount3));

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "stream-id");

                var resolvedEvents = await readStreamResult.ToListAsync();

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

    delegate EventBuilder EventBuilderConfigurator(EventBuilder eventBuilder);

    private static IEnumerable<EventBuilder> ListOfNBuilders(int eventCount)
    {
        return ListOfNBuilders(eventCount, (e) => e);
    }

    private static IEnumerable<EventBuilder> ListOfNBuilders(int eventCount,
        EventBuilderConfigurator eventBuilderConfiguratorConfigurator)
    {
        for (int i = 0; i < eventCount; i++)
        {
            yield return eventBuilderConfiguratorConfigurator(new EventBuilder());
        }
    }

    public enum CountOfEvents
    {
        One,
        Multiple
    }

    private static EventBuilder AnEvent()
    {
        return new EventBuilder();
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