using Npgsql;
using NpgsqlTypes;

namespace MyDotNetEventStore.Tests;

public class ReadStreamResult : IAsyncEnumerable<ResolvedEvent>
{
    private NpgsqlConnection _npgsqlConnection;
    private ReadingCommandBuilder _commandBuilder;

    private ReadStreamResult(ReadingCommandBuilder commandBuilder, NpgsqlConnection npgsqlConnection)
    {
        _commandBuilder = commandBuilder;
        _npgsqlConnection = npgsqlConnection;
    }

    public static ReadStreamResult PrepareForReading(NpgsqlConnection npgsqlConnection, ReadingCommandBuilder readingCommandBuilder)
    {
        return new ReadStreamResult(readingCommandBuilder, npgsqlConnection);
    }

    public async IAsyncEnumerator<ResolvedEvent> GetAsyncEnumerator(CancellationToken cancellationToken = new CancellationToken())
    {
        var readingCommandBuilder = _commandBuilder;

        long lastPosition = 0;
        while (cancellationToken.IsCancellationRequested == false)
        {
            int eventCount = 0;

            await using var command = readingCommandBuilder.Build(_npgsqlConnection);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var (position, resolvedEvent) = ReadingCommandBuilder.BuildOneEvent(reader);

                eventCount++;
                lastPosition = position;

                yield return resolvedEvent;
            }

            // Todo: Add test when batch size === count of fetched events
            if (eventCount < readingCommandBuilder.BatchSize())
            {
                break;
            }

            readingCommandBuilder = readingCommandBuilder.NextReadingCommandBuilderStartingAtPosition(lastPosition);
        }
    }
}

public record EventData
{
    public string Data { get; }
    public string MetaData { get; }
    public string EventType { get; }
    public Guid Id { get; }

    public EventData(string eventType, string data, string metaData, Guid id = new Guid())
    {
        Id = id;
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
    public string StreamId { get; }

    public ResolvedEvent(long position, long revision, string eventType, string data, string metaData, string streamId)
    {
        Position = position;
        Data = data;
        MetaData = metaData;
        EventType = eventType;
        Revision = revision;
        StreamId = streamId;
    }
}

public class ReadingCommandBuilder
{
    private int _batchSize = 100;
    private long? _position;
    private string? _streamId;
    private long? _revision;

    public ReadingCommandBuilder WithBatchSize(int batchSize)
    {
        _batchSize = batchSize;

        return this;
    }

    public int BatchSize()
    {
        return _batchSize;
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

    public NpgsqlCommand Build(NpgsqlConnection npgsqlConnection)
    {
        var cmdText = """
                      SELECT position, event_type, revision, data, metadata, stream_id
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

        cmdText += " LIMIT @batchSize";

        cmdText += ";";

        var command = new NpgsqlCommand(cmdText, npgsqlConnection);

        if (_position is not null)
        {
            command.Parameters.AddWithValue("@lastPosition", _position);
        }

        if (_revision is not null)
        {
            // Todo: fail if we are not fetching a stream
            command.Parameters.AddWithValue("@lastRevision", _revision);
        }

        command.Parameters.AddWithValue("@batchSize", _batchSize);

        if (_streamId is not null)
        {
            command.Parameters.AddWithValue("@streamId", _streamId);
        }

        return command;
    }

    public static (long position, ResolvedEvent resolvedEvent) BuildOneEvent(NpgsqlDataReader reader)
    {
        var position = reader.GetInt64(0);
        var eventType = reader.GetString(1);
        var revision = reader.GetInt64(2);
        var data = reader.GetString(3);
        var metaData = reader.GetString(4);
        var streamId = reader.GetString(5);

        var resolvedEvent = new ResolvedEvent(position, revision, eventType, data, metaData, streamId);
        return (position, resolvedEvent);
    }

    public ReadingCommandBuilder NextReadingCommandBuilderStartingAtPosition(long position)
    {
        var nextReadingCommandBuilder = (ReadingCommandBuilder)MemberwiseClone();

        return nextReadingCommandBuilder.StartingFromPosition(position);
    }
}

public class EventStore
{
    private const int BatchSize = 100;

    private readonly NpgsqlConnection _npgsqlConnection;

    public EventStore(NpgsqlConnection npgsqlConnection)
    {
        _npgsqlConnection = npgsqlConnection;
    }

    public ReadStreamResult ReadStreamAsync(string streamId)
    {
        var readingCommandBuilder = new ReadingCommandBuilder()
            .FromStream(streamId)
            .StartingFromRevision(0)
            .WithBatchSize(BatchSize);

        return ReadStreamResult.PrepareForReading(_npgsqlConnection, readingCommandBuilder);
    }

    public ReadStreamResult ReadStreamAsync(string streamId, int i)
    {
        return ReadStreamAsync(streamId);
    }

    public ReadStreamResult ReadAllAsync()
    {
        var readingCommandBuilder = new ReadingCommandBuilder()
            .StartingFromPosition(0)
            .WithBatchSize(BatchSize);

        return ReadStreamResult.PrepareForReading(_npgsqlConnection, readingCommandBuilder);
    }

    public async Task<AppendResult> AppendAsync(string streamId, EventData evt)
    {
        return await AppendAsync(streamId, [evt], StreamState.Any());
    }

    public async Task<AppendResult> AppendAsync(string streamId, EventData evt, StreamState streamState)
    {
        return await AppendAsync(streamId, [evt], streamState);
    }

    public async Task<AppendResult> AppendAsync(string streamId, List<EventData> events)
    {
        return await AppendAsync(streamId, events, StreamState.Any());
    }

    public async Task<AppendResult> AppendAsync(string streamId, List<EventData> events, StreamState streamState)
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
            var command = new NpgsqlCommand("INSERT INTO events (stream_id, revision, id, event_type, data, metadata) VALUES (@stream_id, @revision, @id, @event_type, @event_data, @event_metadata) RETURNING position, revision;",
                _npgsqlConnection);
            command.Parameters.AddWithValue("stream_id", streamId);
            command.Parameters.AddWithValue("revision", ++lastRevision);
            command.Parameters.AddWithValue("event_type", evt.EventType);
            command.Parameters.AddWithValue("id", evt.Id);
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
}
