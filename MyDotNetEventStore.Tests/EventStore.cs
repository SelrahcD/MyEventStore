using System.Collections;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework.Constraints;

namespace MyDotNetEventStore.Tests;

public class ReadStreamResult : IAsyncEnumerable<ResolvedEvent>
{
    private const int BatchSize = 100;

    private string? _streamId = null!;
    private NpgsqlConnection? _npgsqlConnection = null!;
    private ReadingCommandBuilder _commandBuilder;

    private ReadStreamResult()
    {
    }

    private static async Task<ReadStreamResult> PrepareForReading(string streamId,
        NpgsqlConnection npgsqlConnection, ReadingCommandBuilder readingCommandBuilder)
    {
        var readStreamResult = new ReadStreamResult();
        readStreamResult._streamId = streamId;
        readStreamResult._npgsqlConnection = npgsqlConnection;
        readStreamResult._commandBuilder = readingCommandBuilder;

        return readStreamResult;
    }

    public async IAsyncEnumerator<ResolvedEvent> GetAsyncEnumerator(CancellationToken cancellationToken = new CancellationToken())
    {
        long lastPosition = 0;

        var readingCommandBuilder = _commandBuilder;

        while (true)
        {
            int eventCount = 0;

            await using var command = readingCommandBuilder.Build();

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var (position, resolvedEvent) = ReadingCommandBuilder.BuildOneEvent(reader);

                eventCount++;
                lastPosition = position;

                yield return resolvedEvent;
            }

            // Todo: Add test when batch size === count of fetched events
            if (eventCount < BatchSize)
            {
                break;
            }

            readingCommandBuilder = new ReadingCommandBuilder(_npgsqlConnection)
                .FromStream(_streamId)
                .StartingFromPosition(lastPosition)
                .BatchSize(BatchSize);

        }
    }

    public static async Task<ReadStreamResult> ForStream(NpgsqlConnection npgsqlConnection, string streamId)
    {
        var readingCommandBuilder = new ReadingCommandBuilder(npgsqlConnection)
            .FromStream(streamId)
            .StartingFromRevision(0)
            .BatchSize(BatchSize);


        return await ReadStreamResult.PrepareForReading(streamId, npgsqlConnection, readingCommandBuilder);
    }
}

public class ReadAllStreamResult : IAsyncEnumerable<ResolvedEvent>
{
    private readonly NpgsqlConnection _npgsqlConnection;

    public ReadAllStreamResult(NpgsqlConnection npgsqlConnection)
    {
        this._npgsqlConnection = npgsqlConnection;
    }

    public async IAsyncEnumerator<ResolvedEvent> GetAsyncEnumerator(CancellationToken cancellationToken = new CancellationToken())
    {
        long lastPosition = 0;
        const int batchSize = 100;

        while (true)
        {
            var eventCount = 0;

            await using var command = new ReadingCommandBuilder(_npgsqlConnection)
                .StartingFromPosition(lastPosition)
                .BatchSize(batchSize).Build();

            await using var reader =  await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var (position, resolvedEvent) = ReadingCommandBuilder.BuildOneEvent(reader);

                eventCount++;
                lastPosition = position;

                yield return resolvedEvent;
            }

            if (eventCount < batchSize)
            {
                break;
            }
        }
    }
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

    private int? _batchSize = null;
    private long? _position = null;
    private string? _streamId = null;
    private long? _revision = null;

    // We should inject the connection in the build method to avoid storing it
    // which could impeach closing it
    public ReadingCommandBuilder(NpgsqlConnection npgsqlConnection)
    {
        _npgsqlConnection = npgsqlConnection;
    }
    
    public ReadingCommandBuilder BatchSize(int batchSize)
    {
        _batchSize = batchSize;

        return this;
    }

    public ReadingCommandBuilder FromStream(string streamId)
    {
        _streamId = streamId;

        return this;
    }

    public ReadingCommandBuilder StartingFromPosition(long lastPosition)
    {
        _position = lastPosition;

        return this;
    }

    public ReadingCommandBuilder StartingFromRevision(long lastRevision)
    {
        _revision = lastRevision;

        return this;
    }

    public NpgsqlCommand Build()
    {
        var cmdText = $"""
                       SELECT position, event_type, revision, data, metadata
                       FROM events
                       WHERE 1 = 1
                       """;

        if (_streamId is not null)
        {
            cmdText += " AND stream_id = @streamId";
        }

        if (_position is not null)
        {
            cmdText += " AND position > @lastPosition";
        }

        if (_revision is not null)
        {
            cmdText += " AND revision > @lastRevision";
        }

        cmdText += " ORDER BY position ASC";

        if (_batchSize is not null)
        {
            cmdText += " LIMIT @batchSize";
        }

        cmdText += ";";

        var command = new NpgsqlCommand(cmdText, _npgsqlConnection);

        if (_position is not null)
        {
            command.Parameters.AddWithValue("@lastPosition", _position);
        }

        if (_revision is not null)
        {
            // Todo: fail if we are not fetching a stream
            command.Parameters.AddWithValue("@lastRevision", _revision);
        }

        if (_batchSize is not null)
        {
            command.Parameters.AddWithValue("@batchSize", _batchSize);
        }

        if (_streamId is not null)
        {
            command.Parameters.AddWithValue("@streamId", _streamId);
        }

        return command;
    }

    private static async Task<(bool, long, List<ResolvedEvent>)> BuildEvents(NpgsqlDataReader reader)
    {
        long lastPosition = 0;
        var events = new List<ResolvedEvent>();
        while (await reader.ReadAsync())
        {
            var (position, resolvedEvent) = BuildOneEvent(reader);

            events.Add(resolvedEvent);

            lastPosition = position;
        }

        return (true, lastPosition, events);
    }

    public static (long position, ResolvedEvent resolvedEvent) BuildOneEvent(NpgsqlDataReader reader)
    {
        var position = reader.GetInt64(0);
        var eventType = reader.GetString(1);
        var revision = reader.GetInt64(2);
        var data = reader.GetString(3);
        var metaData = reader.GetString(4);

        var resolvedEvent = new ResolvedEvent(position, revision, eventType, data, metaData);
        return (position, resolvedEvent);
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
        return await ReadStreamResult.ForStream(_npgsqlConnection, streamId);
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
            var streamExists = await StreamExist(streamId);

            switch (streamExists == StreamExistence.Exists)
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

            await using var reader = await command.ExecuteReaderAsync();

            if (!await reader.ReadAsync()) continue;

            position = reader.GetInt64(0);
            revision = reader.GetInt64(1);
        }

        return new AppendResult(position, revision);
    }

    public async Task<StreamExistence> StreamExist(string streamId)
    {
        var checkStreamCommand = new NpgsqlCommand("SELECT 1 FROM events WHERE stream_id = @stream_id LIMIT 1;",
            _npgsqlConnection);
        checkStreamCommand.Parameters.AddWithValue("stream_id", streamId);

        return await checkStreamCommand.ExecuteScalarAsync() != null ? StreamExistence.Exists : StreamExistence.NotFound;
    }

    public ReadAllStreamResult ReadAllAsync()
    {
        return new ReadAllStreamResult(_npgsqlConnection);
    }

}
