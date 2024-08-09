using Npgsql;
using NUnit.Framework.Internal;
using Testcontainers.PostgreSql;

namespace MyDotNetEventStore.Tests;

[TestFixture]
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
            public async Task returns_all_events_appended_to_the_stream()
            {
                var evt = AnEvent();

                await _eventStore.AppendAsync("stream-id", evt);

                var readStreamResult = await _eventStore.ReadStreamAsync("stream-id");

                Assert.That(readStreamResult.ToList(), Is.EqualTo(new List<EventData>
                {
                    evt
                }));
            }
        }

    }

    private EventData AnEvent()
    {
        var random = new Random();
        var fakeEventTypes = new List<string> {"event-type-1", "event-type-2", "event-type-3"};
        var randomIndex = random.Next(fakeEventTypes.Count);

        var eventType = fakeEventTypes[randomIndex];
        return new EventData(eventType);
    }
    
}