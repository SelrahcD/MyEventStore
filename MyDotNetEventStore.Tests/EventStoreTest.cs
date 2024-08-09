using Npgsql;
using NUnit.Framework.Internal;
using Testcontainers.PostgreSql;

namespace MyDotNetEventStore.Tests;

public abstract class EventStoreTests
{
    private readonly PostgreSqlContainer _postgresContainer = new PostgreSqlBuilder()
        .Build();

    private EventStore _eventStore;

    private NpgsqlConnection _connection;

    [OneTimeSetUp]
    public async Task OneTimeSetup()
    {
        await _postgresContainer.StartAsync();

        await using var connection = new NpgsqlConnection(_postgresContainer.GetConnectionString());

        await connection.OpenAsync();

        var command = new NpgsqlCommand($"""
                                         CREATE TABLE IF NOT EXISTS events (
                                             position SERIAL PRIMARY KEY,
                                             stream_id TEXT NOT NULL,
                                             event_type TEXT NOT NULL
                                         );
                                         """, connection);

        await command.ExecuteNonQueryAsync();
    }


    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        // Stop and dispose of the container
        await _postgresContainer.DisposeAsync();
    }

    [SetUp]
    public async Task Setup()
    {
        _connection = new NpgsqlConnection(_postgresContainer.GetConnectionString());

        await _connection.OpenAsync();

        _eventStore = new EventStore(_connection);
    }

    [TearDown]
    public async Task TearDown()
    {
        var command = new NpgsqlCommand("DELETE FROM events", _connection);
        await command.ExecuteNonQueryAsync();

        await _connection.CloseAsync();
    }

    [TestFixture]
    public class ReadingAStream : EventStoreTests
    {

        public class WhenTheStreamDoesntExists : EventStoreTests
        {

            [Test]
            public async Task returns_a_ReadStreamResult_with_State_equals_to_StreamNotFound()
            {
                var readStreamResult = await _eventStore.ReadStreamAsync("a-stream-that-doesnt-exists");

                Assert.That(readStreamResult.State, Is.EqualTo(ReadState.StreamNotFound));
            }
        }

        public class WhenTheStreamExists: EventStoreTests
        {
            [Test]
            public async Task returns_a_ReadStreamResult_with_a_State_equals_to_Ok()
            {
                await _eventStore.AppendAsync("stream-id", AnEvent());

                var readStreamResult = await _eventStore.ReadStreamAsync("stream-id");

                Assert.That(readStreamResult.State, Is.EqualTo(ReadState.Ok));
            }

            [Test]
            public async Task returns_all_events_appended_to_the_stream_in_order()
            {
                var evt1 = AnEvent();
                var evt2 = AnEvent();
                var evt3 = AnEvent();

                await _eventStore.AppendAsync("stream-id", evt1);
                await _eventStore.AppendAsync("stream-id", evt2);
                await _eventStore.AppendAsync("stream-id", evt3);

                var readStreamResult = await _eventStore.ReadStreamAsync("stream-id");

                Assert.That(readStreamResult.ToList(), Is.EqualTo(new List<EventData>
                {
                    evt1,
                    evt2,
                    evt3
                }));
            }

            [Test]
            public async Task doesnt_return_events_appended_to_another_stream()
            {
                var evtInStream = AnEvent();
                var evtInAnotherStream = AnEvent();

                await _eventStore.AppendAsync("stream-id", evtInStream);
                await _eventStore.AppendAsync("another-stream-id", evtInAnotherStream);

                var readStreamResult = await _eventStore.ReadStreamAsync("stream-id");

                var readEvents = readStreamResult.ToList();

                Assert.That(readEvents, Is.EqualTo(new List<EventData>
                {
                    evtInStream,
                }));
            }
        }

    }

    [TestFixture]
    public class AppendingEvents
    {
        [TestFixture]
        public class AppendingOneEvent : EventStoreTests
        {
            [Test]
            public async Task allows_to_read_the_stream()
            {
                var evt1 = AnEvent();
                await _eventStore.AppendAsync("stream-id", evt1);

                var readStreamResult = await _eventStore.ReadStreamAsync("stream-id");

                Assert.That(readStreamResult.ToList(), Is.EqualTo(new List<EventData>
                {
                    evt1,
                }));
            }

            [TestFixture]
            public class PerformsConcurrencyChecks
            {

                [TestFixture]
                public class WithStreamStateNoStream : EventStoreTests
                {
                    [Test]
                    public async Task Doesnt_allow_to_write_to_an_existing_stream()
                    {
                        await _eventStore.AppendAsync("stream-id", AnEvent());

                        var exception = Assert.ThrowsAsync<ConcurrencyException>(async () =>
                            await _eventStore.AppendAsync("stream-id", AnEvent(), StreamState.NoStream));

                        Assert.That(exception.Message, Is.EqualTo("Stream 'stream-id' already exists."));
                    }

                    [Test]
                    public Task Allows_to_write_to_a_non_existing_stream()
                    {
                        Assert.DoesNotThrowAsync(async () =>
                        {
                            await _eventStore.AppendAsync("a-non-existing-id", AnEvent(), StreamState.NoStream);
                        });

                        return Task.CompletedTask;
                    }
                }

                [TestFixture]
                public class WithStreamStateAny : EventStoreTests
                {
                    [Test]
                    public async Task Allow_to_write_to_an_existing_stream()
                    {
                        await _eventStore.AppendAsync("stream-id", AnEvent());

                        Assert.DoesNotThrowAsync(async () =>
                        {
                            await _eventStore.AppendAsync("stream-id", AnEvent(), StreamState.Any);
                        });
                    }

                    [Test]
                    public Task Allows_to_write_to_a_non_existing_stream()
                    {
                        Assert.DoesNotThrowAsync(async () =>
                        {
                            await _eventStore.AppendAsync("a-non-existing-id", AnEvent(), StreamState.Any);
                        });

                        return Task.CompletedTask;
                    }
                }

            }
        }

        [TestFixture]
        public class AppendingMultipleEvents : EventStoreTests
        {
            [Test]
            public async Task allows_to_read_the_stream()
            {
                var evt1 = AnEvent();
                var evt2 = AnEvent();
                var evt3 = AnEvent();
                var events = new List<EventData>{evt1, evt2, evt3};

                await _eventStore.AppendAsync("stream-id", events);

                var readStreamResult = await _eventStore.ReadStreamAsync("stream-id");

                Assert.That(readStreamResult.ToList(), Is.EqualTo(events));
            }
        }
    }



    private EventData AnEvent()
    {
        var fakeEventTypes = new List<string> {"event-type-1", "event-type-2", "event-type-3"};

        return new EventData(SelectRandom(fakeEventTypes));
    }

    private static T SelectRandom<T>(List<T> elements)
    {
        var random = new Random();
        var randomIndex = random.Next(elements.Count);
        return elements[randomIndex];
    }
}