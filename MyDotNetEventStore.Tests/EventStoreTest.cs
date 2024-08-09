using System.Collections;
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

public record EventData
{
    public string EventType { get; }

    public EventData(string eventType)
    {
        EventType = eventType;
    }
}

public enum ReadState
{
    StreamNotFound,
    Ok
}

public class EventStore
{
    private readonly NpgsqlConnection _npgsqlConnection;

    public EventStore(NpgsqlConnection npgsqlConnection)
    {
        _npgsqlConnection = npgsqlConnection;
    }

    public async Task<ReadStreamResult> ReadStreamAsync(string streamId)
    {
        var command = new NpgsqlCommand("""
                                        SELECT event_type FROM events
                                        WHERE stream_id = @stream_id
                                        ORDER BY position ASC;
                                        """, _npgsqlConnection);

        command.Parameters.AddWithValue("stream_id", streamId);

        var events = new List<EventData>();

        await using var reader = await command.ExecuteReaderAsync();

        if (!reader.HasRows)
        {
            return ReadStreamResult.StreamNotFound(streamId);
        }

        while (await reader.ReadAsync())
        {
            var eventType = reader.GetString(0);

            // Assuming EventData has a constructor or factory method that takes eventType
            events.Add(new EventData(eventType));
        }

        return ReadStreamResult.StreamFound(streamId, events);
    }

    public async Task AppendAsync(string streamId, EventData evt)
    {
        var command = new NpgsqlCommand("INSERT INTO events (stream_id, event_type) VALUES (@stream_id, @event_type);", _npgsqlConnection);
        command.Parameters.AddWithValue("stream_id", streamId);
        command.Parameters.AddWithValue("event_type", evt.EventType);

        await command.ExecuteNonQueryAsync();
    }
}

public class ReadStreamResult : IEnumerable<EventData>
{
    private readonly ReadState _state;
    private readonly List<EventData> _events;

    private ReadStreamResult(ReadState state, List<EventData> events)
    {
        _state = state;
        _events = events;
    }

    public ReadState State()
    {
        return _state;
    }

    public static ReadStreamResult StreamNotFound(string streamId)
    {
        return new(ReadState.StreamNotFound, new List<EventData>());
    }

    public static ReadStreamResult StreamFound(string streamId, List<EventData> events)
    {
        return new(ReadState.Ok, events);
    }

    public IEnumerator<EventData> GetEnumerator()
    {
        foreach (var evt in _events)
        {
            yield return evt;
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}