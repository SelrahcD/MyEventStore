using Npgsql;
using NpgsqlTypes;
using OneOf;

namespace MyDotNetEventStore;

public class EventStore
{
    private const int BatchSize = 100;

    private readonly NpgsqlConnection _npgsqlConnection;

    public EventStore(NpgsqlConnection npgsqlConnection)
    {
        _npgsqlConnection = npgsqlConnection;
    }

    public ReadStreamResult ReadStreamAsync(Direction direction, string streamId)
    {
        OneOf<long, StreamRevision> startingRevision = 0;

        if (direction == Direction.Backward)
        {
            startingRevision = StreamRevision.End;
        }

        return ReadStreamAsync(direction, streamId, startingRevision);
    }

    public ReadStreamResult ReadStreamAsync(Direction direction, string streamId, OneOf<long, StreamRevision> startingRevision)
    {
        var readingCommandBuilder = new ReadingCommandBuilder()
            .InDirection(direction)
            .FromStream(streamId)
            .StartingFromRevision(startingRevision)
            .WithBatchSize(BatchSize);

        return ReadStreamResult.PrepareForReading(_npgsqlConnection, readingCommandBuilder);
    }

    public ReadStreamResult ReadAllAsync(Direction direction)
    {
        OneOf<long, StreamRevision> startingPosition = 0;

        if (direction == Direction.Backward)
        {
            startingPosition = StreamRevision.End;
        }


        var readingCommandBuilder = new ReadingCommandBuilder()
            .StartingFromPosition(startingPosition)
            .InDirection(direction)
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
