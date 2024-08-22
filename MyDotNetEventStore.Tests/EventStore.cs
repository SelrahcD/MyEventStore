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

    public static Exception StreamIsNotAtExpectedRevision(object expectedRevision, long lastRevision)
    {
        return new ConcurrencyException($"Stream 'stream-id' is at revision {lastRevision}. You tried appending events at revision {expectedRevision}.");
    }
}

public record StreamState
{
    public StreamStateType Type { get; }
    public long ExpectedRevision { get; }

    private StreamState(StreamStateType type, long expectedRevision)
    {
        Type = type;
        ExpectedRevision = expectedRevision;
    }

    public static StreamState NoStream() => new(StreamStateType.NoStream, 0L);
    public static StreamState Any() => new(StreamStateType.Any, 0L);
    public static StreamState StreamExists() => new(StreamStateType.StreamExists, 0L);

    public static StreamState AtRevision(long revision) => new StreamState(StreamStateType.AtRevision, revision);
}

public enum StreamStateType
{
    NoStream,
    Any,
    StreamExists,
    AtRevision
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

    public async Task AppendAsync(string streamId, EventData evt, StreamState? streamState = null)
    {
        await AppendAsync(streamId, [evt], streamState ?? StreamState.Any());
    }

    public async Task AppendAsync(string streamId, List<EventData> events, StreamState? streamState = null)
    {
        await DoAppendAsync(streamId, events, streamState ?? StreamState.Any());
    }

    private async Task DoAppendAsync(string streamId, List<EventData> events, StreamState streamState)
    {
        if (streamState.Type == StreamStateType.NoStream || streamState.Type == StreamStateType.StreamExists || streamState.Type == StreamStateType.AtRevision)
        {
            var checkStreamCommand = new NpgsqlCommand("SELECT 1 FROM events WHERE stream_id = @stream_id LIMIT 1;",
                _npgsqlConnection);
            checkStreamCommand.Parameters.AddWithValue("stream_id", streamId);

            var streamExists = await checkStreamCommand.ExecuteScalarAsync() != null;

            switch (streamExists)
            {
                case true when streamState.Type == StreamStateType.NoStream:
                    throw ConcurrencyException.StreamAlreadyExists(streamId);
                case false when streamState.Type == StreamStateType.AtRevision:
                    throw ConcurrencyException.StreamDoesntExist(streamId);
                case false when streamState.Type == StreamStateType.StreamExists:
                    throw ConcurrencyException.StreamDoesntExist(streamId);
            }
        }

        var lastRevisionCommand = new NpgsqlCommand("SELECT revision FROM events WHERE stream_id = @stream_id ORDER BY revision DESC LIMIT 1;",
            _npgsqlConnection);
        lastRevisionCommand.Parameters.AddWithValue("stream_id", streamId);

        var lastRevision = (long) (await lastRevisionCommand.ExecuteScalarAsync() ?? 0L);

        if (streamState.Type == StreamStateType.AtRevision && streamState.ExpectedRevision != lastRevision)
        {
            throw ConcurrencyException.StreamIsNotAtExpectedRevision(streamState.ExpectedRevision, lastRevision);
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