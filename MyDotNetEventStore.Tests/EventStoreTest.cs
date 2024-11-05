using System.Diagnostics;
using Npgsql;
using NUnit.Framework.Interfaces;
using OneOf;

namespace MyDotNetEventStore.Tests;

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
            case TestStatus.Passed:
                _activity?.SetStatus(ActivityStatusCode.Ok, "Test passed successfully.");
                break;

            case TestStatus.Failed:
                _activity?.SetStatus(ActivityStatusCode.Error, "Test failed.");
                break;

            case TestStatus.Skipped:
                _activity?.SetStatus(ActivityStatusCode.Unset, "Test was skipped.");
                break;

            case TestStatus.Inconclusive:
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
        [Test]
        public async Task returns_StreamExistence_NotFound_when_the_stream_doesnt_exist()
        {
            var streamExist = await _eventStore.StreamExist("a-stream-that-doesnt-exists");

            Assert.That(streamExist, Is.EqualTo(StreamExistence.NotFound));
        }

        [Test]
        public async Task returns_StreamExistence_Exists_when_the_stream_exists()
        {
            await _eventStore.AppendAsync("a-stream", An.Event());

            var streamExist = await _eventStore.StreamExist("a-stream");

            Assert.That(streamExist, Is.EqualTo(StreamExistence.Exists));
        }
    }

    [TestFixture]
    public static class ReadingStream
    {
        public class ForwardWithoutProvidingAPosition : EventStoreTest
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
                var events = A.ListOfNEvents(eventCount, e => e.InStream("stream-id"));

                await _eventStore.AppendAsync("stream-id", events);

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "stream-id");

                var resolvedEvents = await readStreamResult.ToListAsync();

                Assert.That(resolvedEvents, Is.EqualTo(events.ToResolvedEvents()));
            }

            [Test]
            public async Task doesnt_return_events_appended_to_another_stream()
            {
                var evtInStream = An.Event().InStream("stream-id");

                await _eventStore.AppendAsync("stream-id", evtInStream);
                await _eventStore.AppendAsync("another-stream-id", An.Event());

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "stream-id");

                var readEvents = await readStreamResult.ToListAsync();

                Assert.That(readEvents, Is.EqualTo(new List<ResolvedEvent>
                {
                    evtInStream
                }));
            }
        }

        [TestFixture]
        public class ForwardProvidingAPosition : EventStoreTest
        {
            [Test]
            public async Task returns_a_ReadStreamResult_without_any_events_when_the_stream_does_not_exist()
            {
                var readStreamResult =
                    _eventStore.ReadStreamAsync(Direction.Forward, "a-stream-that-doesnt-exists", 10);

                var resolvedEventCount = await readStreamResult.CountAsync();

                Assert.That(resolvedEventCount, Is.EqualTo(0));
            }

            [Test]
            public async Task
                returns_a_ReadStreamResult_without_any_events_when_the_stream_has_less_events_than_the_requested_revision()
            {
                var events = A.ListOfNEvents(5, e => e.InStream("stream-id"));

                await _eventStore.AppendAsync("stream-id", events);

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "stream-id", 6);

                var resolvedEventCount = await readStreamResult.CountAsync();

                Assert.That(resolvedEventCount, Is.EqualTo(0));
            }

            [Test]
            public async Task
                returns_a_ReadStreamResult_with_all_events_with_a_revision_greater_or_equal_to_the_requested_revision()
            {
                var revisions = EventBuilder.RevisionTracker();

                var eventsBeforeRequestedRevision = A.ListOfNEvents(5, e => e.InStream("stream-id"), revisions);
                var eventAfterRequestedRevision = A.ListOfNEvents(115, e => e.InStream("stream-id"), revisions);

                await _eventStore.AppendAsync("stream-id", eventsBeforeRequestedRevision);
                await _eventStore.AppendAsync("stream-id", eventAfterRequestedRevision);

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "stream-id", 6);

                var resolvedEvents = await readStreamResult.ToListAsync();

                Assert.That(resolvedEvents, Is.EqualTo(eventAfterRequestedRevision.ToResolvedEvents()));
            }

            [Test]
            public async Task
                returns_a_ReadStreamResult_with_all_events_when_the_requested_revision_is_StreamRevision_Start()
            {
                var events = A.ListOfNEvents(115, e => e.InStream("stream-id"));

                await _eventStore.AppendAsync("stream-id", events);

                var readStreamResult =
                    _eventStore.ReadStreamAsync(Direction.Forward, "stream-id", StreamRevision.Start);

                var resolvedEvents = await readStreamResult.ToListAsync();

                Assert.That(resolvedEvents, Is.EqualTo(events.ToResolvedEvents()));
            }

            [Test]
            public async Task
                returns_a_ReadStreamResult_without_events_when_the_requested_revision_is_StreamRevision_End()
            {
                var events = A.ListOfNEvents(5, e => e.InStream("stream-id"));

                await _eventStore.AppendAsync("stream-id", events);

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "stream-id", StreamRevision.End);

                var resolvedEventCount = await readStreamResult.CountAsync();

                Assert.That(resolvedEventCount, Is.EqualTo(0));
            }
        }

        [TestFixture]
        public class BackwardWithoutProvidingAPosition : EventStoreTest
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
                var events = A.ListOfNEvents(eventCount, e => e.InStream("stream-id"));

                await _eventStore.AppendAsync("stream-id", events);

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Backward, "stream-id");

                var resolvedEvents = await readStreamResult.ToListAsync();

                var expectedEvents = events.ToResolvedEvents();
                expectedEvents.Reverse();

                Assert.That(resolvedEvents, Is.EqualTo(expectedEvents));
            }

            [Test]
            public async Task doesnt_return_events_appended_to_another_stream()
            {
                var evtInStream = An.Event().InStream("stream-id");

                await _eventStore.AppendAsync("stream-id", evtInStream);
                await _eventStore.AppendAsync("another-stream-id", An.Event());

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Backward, "stream-id");

                var readEvents = await readStreamResult.ToListAsync();

                Assert.That(readEvents, Is.EqualTo(new List<ResolvedEvent>
                {
                    evtInStream
                }));
            }
        }

        [TestFixture]
        public class BackwardProvidingAPosition : EventStoreTest
        {
            [Test]
            public async Task returns_a_ReadStreamResult_without_any_events_when_the_stream_does_not_exist()
            {
                var readStreamResult =
                    _eventStore.ReadStreamAsync(Direction.Backward, "a-stream-that-doesnt-exists", 10);

                var resolvedEventCount = await readStreamResult.CountAsync();

                Assert.That(resolvedEventCount, Is.EqualTo(0));
            }

            [Test]
            public async Task
                returns_a_ReadStreamResult_with_all_events_in_reverse_order_when_the_requested_revision_is_greater_than_the_current_revision()
            {
                var events = A.ListOfNEvents(5, e => e.InStream("stream-id"));

                await _eventStore.AppendAsync("stream-id", events);

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Backward, "stream-id", 10);

                var resolvedEvents = await readStreamResult.ToListAsync();

                var expectedEvents = events.ToResolvedEvents();
                expectedEvents.Reverse();
                Assert.That(resolvedEvents, Is.EqualTo(expectedEvents));
            }

            [Test]
            public async Task
                returns_a_ReadStreamResult_with_all_events_with_a_revision_lesser_or_equal_to_the_requested_revision_in_reverse_order()
            {
                var eventsBeforeRequestedRevision = A.ListOfNEvents(115, e => e.InStream("stream-id"));
                var eventAfterRequestedRevision = A.ListOfNEvents(20, e => e.InStream("stream-id"));

                await _eventStore.AppendAsync("stream-id", eventsBeforeRequestedRevision);
                await _eventStore.AppendAsync("stream-id", eventAfterRequestedRevision);

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Backward, "stream-id", 115);

                var resolvedEvents = await readStreamResult.ToListAsync();

                var expectedEvents = eventsBeforeRequestedRevision.ToResolvedEvents();
                expectedEvents.Reverse();

                Assert.That(resolvedEvents, Is.EqualTo(expectedEvents));
            }

            [Test]
            public async Task
                returns_a_ReadStreamResult_with_all_events_in_reverse_order_when_the_requested_revision_is_StreamRevision_End()
            {
                var events = A.ListOfNEvents(115, e => e.InStream("stream-id"));

                await _eventStore.AppendAsync("stream-id", events);

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Backward, "stream-id", StreamRevision.End);

                var resolvedEvents = await readStreamResult.ToListAsync();

                var expectedEvents = events.ToResolvedEvents();
                expectedEvents.Reverse();
                Assert.That(resolvedEvents, Is.EqualTo(expectedEvents));
            }

            [Test]
            public async Task
                returns_a_ReadStreamResult_without_events_in_reverse_order_when_the_requested_revision_is_StreamRevision_Start()
            {
                var events= A.ListOfNEvents(115, e => e.InStream("stream-id"));

                await _eventStore.AppendAsync("stream-id", events);

                var readStreamResult =
                    _eventStore.ReadStreamAsync(Direction.Backward, "stream-id", StreamRevision.Start);

                var resolvedEventCount = await readStreamResult.CountAsync();

                Assert.That(resolvedEventCount, Is.EqualTo(0));
            }
        }
    }

    [TestFixture]
    public static class ReadingAllStream
    {
        [TestFixture]
        public class ForwardWithoutProvidingAPosition : EventStoreTest
        {
            [Test]
            public async Task returns_all_events_appended_to_all_streams_in_order(
                [Values(1, 3, 50, 100, 187, 200, 270, 600)]
                int eventCount)
            {
                var events = A.ListOfNEvents(eventCount);

                await events.AppendTo(_eventStore);

                var readStreamResult = _eventStore.ReadAllAsync(Direction.Forward);

                var resolvedEvents = await readStreamResult.ToListAsync();

                var resolvedEventsOfMultiplesStreams = events.ToResolvedEvents();
                Assert.That(resolvedEvents, Is.EqualTo(resolvedEventsOfMultiplesStreams));
            }
        }

        [TestFixture]
        public class ForwardProvidingAPosition : EventStoreTest
        {
            [Test]
            public async Task
                returns_a_ReadStreamResult_with_all_events_when_the_requested_revision_is_StreamRevision_Start()
            {
                var events = A.ListOfNEvents(10);

                await events.AppendTo(_eventStore);

                var readStreamResult = _eventStore.ReadAllAsync(Direction.Forward, StreamRevision.Start);

                var resolvedEvents = await readStreamResult.ToListAsync();

                var expectedEvents = events.ToResolvedEvents();
                Assert.That(resolvedEvents, Is.EqualTo(expectedEvents));
            }

            [Test]
            public async Task
                returns_a_ReadStreamResult_without_any_events_when_the_stream_has_less_events_than_the_requested_revision()
            {
                var events = A.ListOfNEvents(10);

                await events.AppendTo(_eventStore);

                var readStreamResult = _eventStore.ReadAllAsync(Direction.Forward, 11);

                var resolvedEventCount = await readStreamResult.CountAsync();

                Assert.That(resolvedEventCount, Is.EqualTo(0));
            }

            [Test]
            public async Task
                returns_a_ReadStreamResult_with_all_events_with_a_revision_greater_or_equal_to_the_requested_revision()
            {
                var events = A.ListOfNEvents(215);

                await events.AppendTo(_eventStore);

                var readStreamResult = _eventStore.ReadAllAsync(Direction.Forward, 100);

                var resolvedEventCount = await readStreamResult.CountAsync();
                var resolvedEvents = await readStreamResult.ToListAsync();

                Assert.That(resolvedEventCount, Is.EqualTo(115));
                Assert.That(resolvedEvents, Is.EqualTo(events.GetRange(100, 115).ToResolvedEvents()));
            }

            [Test]
            public async Task
                returns_a_ReadStreamResult_without_any_events_when_the_requested_revision_is_StreamRevision_End()
            {
                var events = A.ListOfNEvents(115, e => e.InStream("stream-id"));

                await _eventStore.AppendAsync("stream-id", events);

                var readStreamResult = _eventStore.ReadAllAsync(Direction.Forward, StreamRevision.End);

                var resolvedEventCount = await readStreamResult.CountAsync();

                Assert.That(resolvedEventCount, Is.EqualTo(0));
            }
        }

        public class BackwardWithoutProvidingAPosition : EventStoreTest
        {
            [Test]
            public async Task returns_all_events_appended_to_all_streams_in_reverse_order(
                [Values(1, 3, 50, 100, 187, 200, 270, 600)]
                int eventCount)
            {
                var events = A.ListOfNEvents(eventCount);

                await events.AppendTo(_eventStore);

                var readStreamResult = _eventStore.ReadAllAsync(Direction.Backward);

                var resolvedEvents = await readStreamResult.ToListAsync();

                var resolvedEventsOfMultiplesStreams = events.ToResolvedEvents();
                resolvedEvents.Reverse();
                Assert.That(resolvedEvents, Is.EqualTo(resolvedEventsOfMultiplesStreams));
            }
        }

        public class BackwardProvidingAPosition : EventStoreTest
        {
            [Test]
            public async Task
                returns_a_ReadStreamResult_with_all_events_in_reverse_order_when_the_requested_revision_is_greater_than_the_current_revision()
            {
                var events = A.ListOfNEvents(10);

                await events.AppendTo(_eventStore);

                var readStreamResult = _eventStore.ReadAllAsync(Direction.Backward, 5);

                var resolvedEvents = await readStreamResult.ToListAsync();

                var expectedEvents = events.GetRange(0, 4).ToResolvedEvents();
                expectedEvents.Reverse();
                Assert.That(resolvedEvents, Is.EqualTo(expectedEvents));
            }

            [Test]
            public async Task
                returns_a_ReadStreamResult_with_all_events_with_a_revision_lesser_or_equal_to_the_requested_revision_in_reverse_order()
            {
                var events = A.ListOfNEvents(215);

                await events.AppendTo(_eventStore);

                var readStreamResult = _eventStore.ReadAllAsync(Direction.Backward, 115);

                var resolvedEvents = await readStreamResult.ToListAsync();

                var expectedEvents = events.GetRange(0, 114).ToResolvedEvents();
                expectedEvents.Reverse();
                Assert.That(resolvedEvents, Is.EqualTo(expectedEvents));
            }

            [Test]
            public async Task
                returns_a_ReadStreamResult_with_all_events_in_reverse_order_when_the_requested_revision_is_StreamRevision_End()
            {
                var events = A.ListOfNEvents(115);

                await events.AppendTo(_eventStore);

                var readStreamResult = _eventStore.ReadAllAsync(Direction.Backward, StreamRevision.End);

                var resolvedEvents = await readStreamResult.ToListAsync();

                var expectedEvents = events.ToResolvedEvents();
                expectedEvents.Reverse();
                Assert.That(resolvedEvents, Is.EqualTo(expectedEvents));
            }

            [Test]
            public async Task
                returns_a_ReadStreamResult_without_events_in_reverse_order_when_the_requested_revision_is_StreamRevision_Start()
            {
                var events = A.ListOfNEvents(115);

                await events.AppendTo(_eventStore);

                await _eventStore.AppendAsync("stream-id", events);

                var readStreamResult =
                    _eventStore.ReadAllAsync(Direction.Backward, StreamRevision.Start);

                var resolvedEventCount = await readStreamResult.CountAsync();

                Assert.That(resolvedEventCount, Is.EqualTo(0));
            }
        }
    }

    [TestFixture]
    public class AppendingEvents : EventStoreTest
    {
        [Test]
        public void necessitates_at_least_one_event_to_be_appended()
        {
            var exception = Assert.ThrowsAsync<CannotAppendAnEmptyListException>(async () =>
                await _eventStore.AppendAsync("stream-id", []));

            Assert.That(exception.Message, Is.EqualTo("Cannot append an empty list to stream 'stream-id'."));
        }

        [TestFixture]
        public class PerformsConcurrencyChecks : EventStoreTest
        {
            [TestFixture]
            public class WithStreamStateNoStream : PerformsConcurrencyChecks
            {
                [Test]
                public async Task Doesnt_allow_to_write_to_an_existing_stream(
                    [Values] OneOrMultipleEvents oneOrMultipleEvents)
                {
                    var events = BuildEvents(oneOrMultipleEvents).ToEventData();

                    await _eventStore.AppendAsync("stream-id", (dynamic)events);

                    var exception = Assert.ThrowsAsync<ConcurrencyException>(async () =>
                        await _eventStore.AppendAsync("stream-id", (dynamic)events, StreamState.NoStream()));

                    Assert.That(exception.Message, Is.EqualTo("Stream 'stream-id' already exists."));
                }

                [Test]
                public Task Allows_to_write_to_a_non_existing_stream(
                    [Values] OneOrMultipleEvents oneOrMultipleEvents)
                {
                    Assert.DoesNotThrowAsync(async () =>
                    {
                        await _eventStore.AppendAsync("a-non-existing-id",
                            (dynamic)BuildEvents(oneOrMultipleEvents).ToEventData(), StreamState.NoStream());
                    });

                    return Task.CompletedTask;
                }
            }

            public class WithStreamStateStreamExists : PerformsConcurrencyChecks
            {
                [Test]
                public Task Doesnt_allow_to_write_to_an_non_existing_stream(
                    [Values] OneOrMultipleEvents oneOrMultipleEvents)
                {
                    var exception = Assert.ThrowsAsync<ConcurrencyException>(async () =>
                        await _eventStore.AppendAsync("a-non-existing-id",
                            (dynamic)BuildEvents(oneOrMultipleEvents).ToEventData(), StreamState.StreamExists()));

                    Assert.That(exception.Message, Is.EqualTo("Stream 'a-non-existing-id' doesn't exists."));
                    return Task.CompletedTask;
                }

                [Test]
                public async Task Allows_to_write_to_an_existing_stream(
                    [Values] OneOrMultipleEvents oneOrMultipleEvents)
                {
                    await _eventStore.AppendAsync("stream-id", (dynamic)BuildEvents(oneOrMultipleEvents).ToEventData());

                    Assert.DoesNotThrowAsync(async () =>
                    {
                        await _eventStore.AppendAsync("stream-id", (dynamic)BuildEvents(oneOrMultipleEvents).ToEventData(),
                            StreamState.StreamExists());
                    });
                }
            }

            public class WithStreamStateAny : PerformsConcurrencyChecks
            {
                [Test]
                public async Task Allow_to_write_to_an_existing_stream(
                    [Values] OneOrMultipleEvents oneOrMultipleEvents)
                {
                    await _eventStore.AppendAsync("stream-id", (dynamic)BuildEvents(oneOrMultipleEvents).ToEventData());

                    Assert.DoesNotThrowAsync(async () =>
                    {
                        await _eventStore.AppendAsync("stream-id", (dynamic)BuildEvents(oneOrMultipleEvents).ToEventData(),
                            StreamState.Any());
                    });
                }

                [Test]
                public Task Allows_to_write_to_a_non_existing_stream(
                    [Values] OneOrMultipleEvents oneOrMultipleEvents)
                {
                    Assert.DoesNotThrowAsync(async () =>
                    {
                        await _eventStore.AppendAsync("a-non-existing-id",
                            (dynamic)BuildEvents(oneOrMultipleEvents).ToEventData(), StreamState.Any());
                    });

                    return Task.CompletedTask;
                }
            }

            [TestFixture]
            public class WithARevisionNumber : PerformsConcurrencyChecks
            {
                [Test]
                public Task Doesnt_allow_to_write_to_non_existing_stream(
                    [Values] OneOrMultipleEvents oneOrMultipleEvents)
                {
                    var events = BuildEvents(oneOrMultipleEvents).ToEventData();

                    var exception = Assert.ThrowsAsync<ConcurrencyException>(async () =>
                        await _eventStore.AppendAsync("a-non-existing-stream-id", (dynamic)events,
                            StreamState.AtRevision(1)));

                    Assert.That(exception.Message, Is.EqualTo("Stream 'a-non-existing-stream-id' doesn't exists."));
                    return Task.CompletedTask;
                }

                [Test]
                public async Task Doesnt_allow_to_write_to_stream_at_a_greater_revision(
                    [Values] OneOrMultipleEvents oneOrMultipleEvents
                    )
                {
                    var alreadyAppendedEventCount = 3;
                    var pastEvents = A.ListOfNEvents(alreadyAppendedEventCount);
                    var triedRevision = 4;

                    await _eventStore.AppendAsync("stream-id", pastEvents, StreamState.NoStream());

                    var exception = Assert.ThrowsAsync<ConcurrencyException>(async () =>
                        await _eventStore.AppendAsync("stream-id", (dynamic)BuildEvents(oneOrMultipleEvents).ToEventData(),
                            StreamState.AtRevision(triedRevision)));

                    Assert.That(exception.Message,
                        Is.EqualTo(
                            $"Stream 'stream-id' is at revision {alreadyAppendedEventCount}. You tried appending events at revision {triedRevision}."));
                }

                [Test]
                public async Task Doesnt_allow_to_write_to_stream_at_a_lower_revision(
                    [Values] OneOrMultipleEvents oneOrMultipleEvents)
                {
                    var alreadyAppendedEventCount = 3;
                    var pastEvents = A.ListOfNEvents(alreadyAppendedEventCount);
                    var triedRevision = 2;

                    await _eventStore.AppendAsync("stream-id", pastEvents, StreamState.NoStream());

                    var exception = Assert.ThrowsAsync<ConcurrencyException>(async () =>
                        await _eventStore.AppendAsync("stream-id", (dynamic)BuildEvents(oneOrMultipleEvents).ToEventData(),
                            StreamState.AtRevision(triedRevision)));

                    Assert.That(exception.Message,
                        Is.EqualTo(
                            $"Stream 'stream-id' is at revision {alreadyAppendedEventCount}. You tried appending events at revision {triedRevision}."));
                }

                [Test]
                public async Task Allows_to_write_to_a_stream_at_the_expected_revision(
                    [Values] OneOrMultipleEvents oneOrMultipleEvents,
                    [Random(0, 1000, 1)] int alreadyAppendedEventCount)
                {
                    var pastEvents = A.ListOfNEvents(alreadyAppendedEventCount);

                    await _eventStore.AppendAsync("stream-id", pastEvents, StreamState.NoStream());

                    Assert.DoesNotThrowAsync(async () =>
                    {
                        await _eventStore.AppendAsync("stream-id", (dynamic)BuildEvents(oneOrMultipleEvents).ToEventData(),
                            StreamState.AtRevision(alreadyAppendedEventCount));
                    });
                }
            }
        }

        [TestFixture]
        public class MaintainsStreamRevision : EventStoreTest
        {
            [Test]
            public async Task Adds_first_event_in_stream_at_revision_1()
            {
                await _eventStore.AppendAsync("stream-id", An.Event());

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "stream-id");

                var resolvedEventList = await ToListAsync(readStreamResult);

                Assert.That(resolvedEventList.First().Revision, Is.EqualTo(1));
            }

            [Test]
            public async Task
                Last_event_in_stream_revision_is_equal_to_the_count_of_inserted_events_when_all_events_are_added_at_once(
                    [Random(0, 1000, 5)] int eventCount)
            {
                await _eventStore.AppendAsync("stream-id", A.ListOfNEvents(eventCount));

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "stream-id");

                var resolvedEvents = await readStreamResult.ToListAsync();

                Assert.That(resolvedEvents.Last().Revision, Is.EqualTo(eventCount));
            }

            [Test]
            public async Task
                Event_revision_is_the_position_of_the_event_in_the_stream()
            {
                var event1 = An.Event();
                await _eventStore.AppendAsync("stream-id", event1);
                await _eventStore.AppendAsync("another-stream-id", An.Event());
                var event2 = An.Event();
                await _eventStore.AppendAsync("stream-id", event2);
                await _eventStore.AppendAsync("yet-another-stream-id", An.Event());
                var event3 = An.Event();
                await _eventStore.AppendAsync("stream-id", event3);

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "stream-id");

                var resolvedEvents = await readStreamResult.ToListAsync();

                Assert.That(resolvedEvents[0], Is.EqualTo(event1.InStream("stream-id").ToResolvedEvent(1, 1)));
                Assert.That(resolvedEvents[1], Is.EqualTo(event2.InStream("stream-id").ToResolvedEvent(3, 2)));
                Assert.That(resolvedEvents[2], Is.EqualTo(event3.InStream("stream-id").ToResolvedEvent(5, 3)));
            }


            [Test]
            public async Task
                Last_event_in_stream_revision_is_equal_to_the_count_of_inserted_events_when_events_are_in_multiple_times(
                    [Random(1, 100, 2)] int eventCount1,
                    [Random(1, 100, 1)] int eventCount2,
                    [Random(1, 100, 1)] int eventCount3
                )
            {
                await _eventStore.AppendAsync("stream-id", A.ListOfNEvents(eventCount1));
                await _eventStore.AppendAsync("stream-id", A.ListOfNEvents(eventCount2));
                await _eventStore.AppendAsync("stream-id", A.ListOfNEvents(eventCount3));

                var readStreamResult = _eventStore.ReadStreamAsync(Direction.Forward, "stream-id");

                var resolvedEvents = await readStreamResult.ToListAsync();

                Assert.That(resolvedEvents.Last().Revision, Is.EqualTo(eventCount1 + eventCount2 + eventCount3));
            }

            [Test]
            public async Task Returns_a_AppendResult_with_Position_and_Revision()
            {
                await _eventStore.AppendAsync("stream-1", A.ListOfNEvents(10));

                await _eventStore.AppendAsync("stream-2", A.ListOfNEvents(10));

                await _eventStore.AppendAsync("stream-3", A.ListOfNEvents(10));

                var appendResult = await _eventStore.AppendAsync("stream-2", A.ListOfNEvents(10).ToEventData());

                Assert.That(appendResult, Is.EqualTo(new AppendResult(40, 20)));
            }
        }

    }


    [TestFixture]
    public class ReadingHeadPosition : EventStoreTest
    {

        [Test]
        public async Task returns_0_as_the_current_position_when_the_event_store_is_empty()
        {
            var actualHeadPosition = await _eventStore.HeadPosition();

            Assert.That(actualHeadPosition, Is.EqualTo(0));
        }

        [Test]
        public async Task returns_the_current_position_across_all_streams(
            [Random(1, 100, 1)] int eventCount)
        {
            var events = A.ListOfNEvents(eventCount);

            await events.AppendTo(_eventStore);

            var actualHeadPosition = await _eventStore.HeadPosition();

            Assert.That(actualHeadPosition, Is.EqualTo(eventCount));
        }
    }

    [TestFixture]
    public static class ReadingStreamHead
    {
        [TestFixture]
        public class WhenTheStreamDoesntExist : EventStoreTest
        {
            [Test]
            public async Task returns_0_as_stream_revision()
            {
                var streamHead = await _eventStore.StreamHead("stream-that-does-not-exist");

                Assert.That(streamHead.Revision, Is.EqualTo(0));
            }

            [Test]
            public async Task returns_0_as_stream_position()
            {
                var streamHead = await _eventStore.StreamHead("stream-that-does-not-exist");

                Assert.That(streamHead.Position, Is.EqualTo(0));
            }
        }

        [TestFixture]
        public class WhenTheStreamExists : EventStoreTest
        {
            [Test]
            public async Task returns_the_stream_revision(
                [Random(1, 100, 1)] int eventCount)
            {
                await _eventStore.AppendAsync("stream-id", A.ListOfNEvents(eventCount));

                var streamHead = await _eventStore.StreamHead("stream-id");

                Assert.That(streamHead.Revision, Is.EqualTo(eventCount));
            }

            [Test]
            [TestCaseSource(nameof(ExamplesOfHistoryForStreamHead))]
            public async Task returns_the_stream_last_position(
                (List<(string, EventBuilders)> History, long ExpectedPosition) td
            )
            {
                await WithHistory(td.History);

                var streamHead = await _eventStore.StreamHead("stream-id");

                Assert.That(streamHead.Position, Is.EqualTo(td.ExpectedPosition));
            }

            [Test]
            public async Task stream_head_has_same_information_as_the_last_append_result_to_the_stream(
                [Random(1, 100, 1)] int eventCount)
            {
                await _eventStore.AppendAsync("another-stream-id", A.ListOfNEvents(10));
                var lastAppendResult = await _eventStore.AppendAsync("stream-id", A.ListOfNEvents(10));
                await _eventStore.AppendAsync("another-stream-id", A.ListOfNEvents(1));

                var streamHead = await _eventStore.StreamHead("stream-id");

                Assert.That(streamHead.Position, Is.EqualTo(lastAppendResult.Position));
                Assert.That(streamHead.Revision, Is.EqualTo(lastAppendResult.Revision));
            }

            private static readonly (List<(string, EventBuilders)>, long)[] ExamplesOfHistoryForStreamHead = [
                (
                    [
                        ("stream-id", A.ListOfNEvents(10)),
                    ],
                    10
                ),
                (
                    [
                        ("stream-id", A.ListOfNEvents(10)),
                        ("another-stream-id", A.ListOfNEvents(1))
                    ],
                    10
                ),
                (
                    [
                        ("another-stream-id", A.ListOfNEvents(1)),
                        ("stream-id", A.ListOfNEvents(10)),
                    ],
                    11
                ),
                (
                    [
                        ("another-stream-id", A.ListOfNEvents(1)),
                        ("stream-id", A.ListOfNEvents(10)),
                        ("another-stream-id", A.ListOfNEvents(1)),
                    ],
                    11
                ),
            ];
        }

    }

    private static OneOf<EventBuilder, EventBuilders> BuildEvents(OneOrMultipleEvents countEvents)
    {
        if (countEvents == OneOrMultipleEvents.One)
            return An.Event();

        return A.ListOfNEvents(3);
    }

    public enum OneOrMultipleEvents
    {
        One,
        Multiple
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

    private async Task WithHistory(List<(string, EventBuilders)> history)
    {
        foreach (var historyItem in history)
        {
            await _eventStore.AppendAsync(historyItem.Item1, historyItem.Item2);
        }
    }
}