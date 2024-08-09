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
        return new EventData("event-type");
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
        var command = new NpgsqlCommand("SELECT 1 FROM events WHERE stream_id = @stream_id LIMIT 1;", _npgsqlConnection);
        command.Parameters.AddWithValue("stream_id", streamId);

        var result = await command.ExecuteScalarAsync();

        // If result is not null, the stream exists
        return result != null ? ReadStreamResult.StreamFound(streamId) : ReadStreamResult.StreamNotFound(streamId);
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

    private ReadStreamResult(ReadState state)
    {
        _state = state;
        _events = new List<EventData>()
        {
            new EventData("event-type"),
        };
    }

    public ReadState State()
    {
        return _state;
    }

    public static ReadStreamResult StreamNotFound(string streamId)
    {
        return new(ReadState.StreamNotFound);
    }

    public static ReadStreamResult StreamFound(string streamId)
    {
        return new(ReadState.Ok);
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