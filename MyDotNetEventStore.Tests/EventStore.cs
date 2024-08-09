using System.Collections;
using Npgsql;

namespace MyDotNetEventStore.Tests;

public class ConcurrencyException: Exception
{
    public ConcurrencyException(string message) : base(message)
    {
    }
}

public enum StreamState
{
    NoStream,
    Any,
    StreamExists
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

public enum ReadState
{
    StreamNotFound,
    Ok
}

public record EventData
{
    public string EventType { get; }

    public EventData(string eventType)
    {
        EventType = eventType;
    }
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

    public async Task AppendAsync(string streamId, EventData evt, StreamState streamState =  StreamState.Any)
    {

        if (streamState == StreamState.NoStream || streamState == StreamState.StreamExists)
        {
            var checkStreamCommand = new NpgsqlCommand("SELECT 1 FROM events WHERE stream_id = @stream_id LIMIT 1;", _npgsqlConnection);
            checkStreamCommand.Parameters.AddWithValue("stream_id", streamId);

            var streamExists = await checkStreamCommand.ExecuteScalarAsync() != null;

            switch (streamExists)
            {
                case true when streamState == StreamState.NoStream:
                    throw new ConcurrencyException($"Stream '{streamId}' already exists.");
                case false when streamState == StreamState.StreamExists:
                    throw new ConcurrencyException($"Stream '{streamId}' doesn't exists.");
            }
        }

        var command = new NpgsqlCommand("INSERT INTO events (stream_id, event_type) VALUES (@stream_id, @event_type);",
            _npgsqlConnection);
        command.Parameters.AddWithValue("stream_id", streamId);
        command.Parameters.AddWithValue("event_type", evt.EventType);

        await command.ExecuteNonQueryAsync();
    }

    public async Task AppendAsync(string streamId, List<EventData> events)
    {
        foreach (var evt in events)
        {
            await AppendAsync(streamId, evt);
        }
    }
}