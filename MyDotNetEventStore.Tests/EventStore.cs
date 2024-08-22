using System.Collections;
using Npgsql;
using NpgsqlTypes;

namespace MyDotNetEventStore.Tests;

public class ConcurrencyException : Exception
{
    public ConcurrencyException(string message) : base(message)
    {
    }

    public static ConcurrencyException StreamDoesntExist(string streamId)
    {
        return new ConcurrencyException($"Stream '{streamId}' doesn't exists.");
    }

    public static ConcurrencyException StreamAlreadyExists(string streamId)
    {
        return new ConcurrencyException($"Stream '{streamId}' already exists.");
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
    public string Data { get; }
    public string MetaData { get; }
    public string EventType { get; }
    public long? Revision { get; set; }

    public EventData(string eventType, string data, string metaData, long? revision = null)
    {
        Data = data;
        MetaData = metaData;
        EventType = eventType;
        Revision = revision;
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
                                        SELECT event_type, revision, data, metadata
                                        FROM events
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
            var revision = reader.GetInt64(1);
            var data = reader.GetString(2);
            var metaData = reader.GetString(3);

            events.Add(new EventData(eventType, data, metaData, revision));
        }

        return ReadStreamResult.StreamFound(streamId, events);
    }

    public async Task AppendAsync(string streamId, EventData evt, StreamState streamState = StreamState.Any)
    {
        await AppendAsync(streamId, [evt], streamState);
    }

    public async Task AppendAsync(string streamId, List<EventData> events, StreamState streamState = StreamState.Any)
    {
        if (streamState == StreamState.NoStream || streamState == StreamState.StreamExists)
        {
            var checkStreamCommand = new NpgsqlCommand("SELECT 1 FROM events WHERE stream_id = @stream_id LIMIT 1;",
                _npgsqlConnection);
            checkStreamCommand.Parameters.AddWithValue("stream_id", streamId);

            var streamExists = await checkStreamCommand.ExecuteScalarAsync() != null;

            switch (streamExists)
            {
                case true when streamState == StreamState.NoStream:
                    throw ConcurrencyException.StreamAlreadyExists(streamId);
                case false when streamState == StreamState.StreamExists:
                    throw ConcurrencyException.StreamDoesntExist(streamId);
            }
        }

        var lastRevisionCommand = new NpgsqlCommand("SELECT revision FROM events WHERE stream_id = @stream_id ORDER BY revision DESC LIMIT 1;",
            _npgsqlConnection);
        lastRevisionCommand.Parameters.AddWithValue("stream_id", streamId);

        long lastRevision = 0;

        var executeScalarAsync = await lastRevisionCommand.ExecuteScalarAsync();
        if (executeScalarAsync != null)
        {
            lastRevision = (long)executeScalarAsync;
        }

        foreach (var evt in events)
        {
            var command = new NpgsqlCommand("INSERT INTO events (stream_id, revision, event_type, data, metadata) VALUES (@stream_id, @revision, @event_type, @event_data, @event_metadata);",
                _npgsqlConnection);
            command.Parameters.AddWithValue("stream_id", streamId);
            command.Parameters.AddWithValue("revision", ++lastRevision);
            command.Parameters.AddWithValue("event_type", evt.EventType);
            command.Parameters.AddWithValue("event_data", NpgsqlDbType.Jsonb, evt.Data);
            command.Parameters.AddWithValue("event_metadata", NpgsqlDbType.Jsonb, evt.MetaData);

            await command.ExecuteNonQueryAsync();
        }
    }
}