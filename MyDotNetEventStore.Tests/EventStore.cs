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

public class ReadStreamResult : IEnumerable<ResolvedEvent>
{
    private readonly ReadState _state;
    private readonly List<ResolvedEvent> _events;

    private ReadStreamResult(ReadState state, List<ResolvedEvent> events)
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
        return new(ReadState.StreamNotFound, new List<ResolvedEvent>());
    }

    public static ReadStreamResult StreamFound(string streamId, List<ResolvedEvent> events)
    {
        return new(ReadState.Ok, events);
    }

    public IEnumerator<ResolvedEvent> GetEnumerator()
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

public class ReadAllStreamResult : IAsyncEnumerable<ResolvedEvent>
{
    private readonly EventStore _eventStore;

    public ReadAllStreamResult(EventStore eventStore)
    {
        _eventStore = eventStore;
    }

    public async IAsyncEnumerator<ResolvedEvent> GetAsyncEnumerator(CancellationToken cancellationToken = new CancellationToken())
    {
        long lastPosition = 0;
        const int batchSize = 100;

        while (true)
        {
            var (lastSeenPosition, events) = await _eventStore.FetchBatchOfEvents(batchSize, lastPosition);

            foreach (var evt in events)
            {
                yield return evt;
            }

            if (events.Count < batchSize)
            {
                break;
            }

            lastPosition = lastSeenPosition;
        }
    }
}

public record AppendResult
{
    private readonly long _position;
    private readonly long _revision;

    public AppendResult(long position, long revision)
    {
        _position = position;
        _revision = revision;
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

    public EventData(string eventType, string data, string metaData)
    {
        Data = data;
        MetaData = metaData;
        EventType = eventType;
    }
}

public record ResolvedEvent
{
    public long Position { get; }
    public string Data { get; }
    public string MetaData { get; }
    public string EventType { get; }
    public long Revision { get; }

    public ResolvedEvent(long position, long revision, string eventType, string data, string metaData)
    {
        Position = position;
        Data = data;
        MetaData = metaData;
        EventType = eventType;
        Revision = revision;
    }
}

public class ReadingCommandBuilder
{
    private NpgsqlConnection _npgsqlConnection;

    private int _batchSize;

    public ReadingCommandBuilder(NpgsqlConnection npgsqlConnection)
    {
        _npgsqlConnection = npgsqlConnection;
    }
    
    public ReadingCommandBuilder BatchSize(int batchSize)
    {
        _batchSize = batchSize;

        return this;
    }

    public NpgsqlCommand Build(int batchSize, long lastPosition)
    {
        BatchSize(batchSize);

        var command = new NpgsqlCommand($"""
                                         SELECT position, event_type, revision, data, metadata
                                         FROM events
                                         WHERE position > @lastPosition
                                         ORDER BY position ASC
                                         LIMIT @batchSize;
                                         """, _npgsqlConnection);

        command.Parameters.AddWithValue("@lastPosition", lastPosition);
        command.Parameters.AddWithValue("@batchSize", _batchSize);

        return command;
    }
}

public class EventStore
{
    public readonly NpgsqlConnection _npgsqlConnection;
    private readonly ReadingCommandBuilder _readingCommandBuilder;

    public EventStore(NpgsqlConnection npgsqlConnection)
    {
        _npgsqlConnection = npgsqlConnection;
        _readingCommandBuilder = new ReadingCommandBuilder(npgsqlConnection);
    }

    public async Task<ReadStreamResult> ReadStreamAsync(string streamId)
    {
        var command = new NpgsqlCommand("""
                                        SELECT position, event_type, revision, data, metadata
                                        FROM events
                                        WHERE stream_id = @stream_id
                                        ORDER BY position ASC;
                                        """, _npgsqlConnection);

        command.Parameters.AddWithValue("stream_id", streamId);


        await using var reader = await command.ExecuteReaderAsync();

        if (!reader.HasRows)
        {
            return ReadStreamResult.StreamNotFound(streamId);
        }

        var (_, events) = await BuildEvents(reader);

        return ReadStreamResult.StreamFound(streamId, events);
    }

    public async Task<AppendResult> AppendAsync(string streamId, EventData evt, StreamState streamState)
    {
        return await AppendAsync(streamId, [evt], streamState);
    }

    public async Task<AppendResult> AppendAsync(string streamId, List<EventData> events, StreamState streamState)
    {
        return await DoAppendAsync(streamId, events, streamState);
    }

    public async Task<AppendResult> AppendAsync(string streamId, EventData evt)
    {
        return await AppendAsync(streamId, [evt], StreamState.Any());
    }

    public async Task<AppendResult> AppendAsync(string streamId, List<EventData> events)
    {
        return await DoAppendAsync(streamId, events, StreamState.Any());
    }

    private async Task<AppendResult> DoAppendAsync(string streamId, List<EventData> events, StreamState streamState)
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

        long position = 0;
        long revision = 0;
        foreach (var evt in events)
        {
            var command = new NpgsqlCommand("INSERT INTO events (stream_id, revision, event_type, data, metadata) VALUES (@stream_id, @revision, @event_type, @event_data, @event_metadata) RETURNING position, revision;",
                _npgsqlConnection);
            command.Parameters.AddWithValue("stream_id", streamId);
            command.Parameters.AddWithValue("revision", ++lastRevision);
            command.Parameters.AddWithValue("event_type", evt.EventType);
            command.Parameters.AddWithValue("event_data", NpgsqlDbType.Jsonb, evt.Data);
            command.Parameters.AddWithValue("event_metadata", NpgsqlDbType.Jsonb, evt.MetaData);

            using var reader = await command.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                 position = reader.GetInt64(0);
                 revision = reader.GetInt64(1);
            }
        }

        return new AppendResult(position, revision);
    }

    public async Task<ReadAllStreamResult> ReadAllAsync()
    {
        return new ReadAllStreamResult(this);
    }

    // Todo: Remove from the public interface of the EventStore
    public async Task<(long, List<ResolvedEvent>)> FetchBatchOfEvents(int batchSize, long lastPosition)
    {
        var commandBuilder = new ReadingCommandBuilder(_npgsqlConnection);
        var command = commandBuilder.Build(batchSize, lastPosition);

        await using var reader = await command.ExecuteReaderAsync();

        return await BuildEvents(reader);
    }

    private static async Task<(long, List<ResolvedEvent>)> BuildEvents(NpgsqlDataReader reader)
    {
        long lastPosition = 0;
        var events = new List<ResolvedEvent>();
        while (await reader.ReadAsync())
        {
            var position = reader.GetInt64(0);
            var eventType = reader.GetString(1);
            var revision = reader.GetInt64(2);
            var data = reader.GetString(3);
            var metaData = reader.GetString(4);

            events.Add(new ResolvedEvent(position, revision, eventType, data, metaData));

            lastPosition = position;
        }

        return (lastPosition, events);
    }
}
