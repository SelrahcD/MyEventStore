using System.Diagnostics;
using System.Text;
using MyDotNetEventStore.Tests;
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
        using var activity = Tracing.ActivitySource.StartActivity("ReadStreamAsync");

        OneOf<long, StreamRevision> startingRevision = 0;

        if (direction == Direction.Backward)
        {
            startingRevision = StreamRevision.End;
        }

        return ReadStreamAsync(direction, streamId, startingRevision);
    }

    public ReadStreamResult ReadStreamAsync(Direction direction, string streamId, OneOf<long, StreamRevision> startingRevision)
    {
        using var activity = Tracing.ActivitySource.StartActivity("ReadStreamAsyncWithRevision");
        activity?.SetTag("direction", direction.ToString());
        activity?.SetTag("streamId", streamId);

        var readingCommandBuilder = new ReadingCommandBuilder()
            .InDirection(direction)
            .FromStream(streamId)
            .StartingFromRevision(startingRevision)
            .WithBatchSize(BatchSize);

        return ReadStreamResult.PrepareForReading(_npgsqlConnection, readingCommandBuilder);
    }

    public ReadStreamResult ReadAllAsync(Direction direction)
    {
        using var activity = Tracing.ActivitySource.StartActivity("ReadAllAsync");
        activity?.SetTag("direction", direction.ToString());

        OneOf<long, StreamRevision> startingPosition = 0;

        if (direction == Direction.Backward)
        {
            startingPosition = StreamRevision.End;
        }

        return ReadAllAsync(direction, startingPosition);
    }

    public ReadStreamResult ReadAllAsync(Direction direction, OneOf<long, StreamRevision> startingPosition)
    {
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
        if (events.Count == 0)
            throw CannotAppendAnEmptyListException.ToStream(streamId);

        using var activity = Tracing.ActivitySource.StartActivity("AppendAsync");
        activity?.SetTag("streamId", streamId);
        activity?.SetTag("eventCount", events.Count);

        // Check on revision not being greater than the current stored revision forces us to always make the query
        var currentlyKnownRevision = (long) (await currentRevisionForStream(streamId) ?? 0L);
        long expectedRevision = streamState.Type switch
        {
            StreamStateType.NoStream => 0L,
            StreamStateType.Any => currentlyKnownRevision,
            StreamStateType.StreamExists => currentlyKnownRevision,
            StreamStateType.AtRevision => streamState.ExpectedRevision,
        };

        if (streamState.Type == StreamStateType.StreamExists && expectedRevision == 0)
        {
            throw ConcurrencyException.StreamDoesntExist(streamId);
        }

        if (streamState.Type == StreamStateType.NoStream && currentlyKnownRevision > 0)
        {
            throw ConcurrencyException.StreamAlreadyExists(streamId);
        }

        if (streamState.Type == StreamStateType.AtRevision && currentlyKnownRevision == 0)
        {
            throw ConcurrencyException.StreamDoesntExist(streamId);
        }

        if (streamState.Type == StreamStateType.AtRevision && streamState.ExpectedRevision > currentlyKnownRevision)
        {
            throw ConcurrencyException.StreamIsNotAtExpectedRevision(streamState.ExpectedRevision, currentlyKnownRevision);
        }

        using var cmdActivity = Tracing.ActivitySource.StartActivity("InsertEvents", ActivityKind.Client);

        var commandText =
            new StringBuilder("INSERT INTO events (stream_id, revision, id, event_type, data, metadata) VALUES ");

        var parameters = new List<NpgsqlParameter>();

        for (int i = 0; i < events.Count; i++)
        {
            var evt = events[i];
            var paramStreamId = $"@stream_id{i}";
            var paramRevision = $"@revision{i}";
            var paramId = $"@id{i}";
            var paramEventType = $"@event_type{i}";
            var paramData = $"@event_data{i}";
            var paramMetadata = $"@event_metadata{i}";

            commandText.Append(
                $"({paramStreamId}, {paramRevision}, {paramId}, {paramEventType}, {paramData}, {paramMetadata})");

            // Add commas between values, except for the last one
            if (i < events.Count - 1)
            {
                commandText.Append(", ");
            }

            // Adding parameters for this event
            parameters.Add(new NpgsqlParameter(paramStreamId, NpgsqlDbType.Varchar) { Value = streamId });
            parameters.Add(new NpgsqlParameter(paramRevision, NpgsqlDbType.Bigint) { Value = ++expectedRevision });
            parameters.Add(new NpgsqlParameter(paramId, NpgsqlDbType.Uuid) { Value = evt.Id });
            parameters.Add(new NpgsqlParameter(paramEventType, NpgsqlDbType.Varchar) { Value = evt.EventType });
            parameters.Add(new NpgsqlParameter(paramData, NpgsqlDbType.Jsonb) { Value = evt.Data });
            parameters.Add(new NpgsqlParameter(paramMetadata, NpgsqlDbType.Jsonb) { Value = evt.MetaData });
        }

        commandText.Append(" RETURNING position, revision;");

        var command = new NpgsqlCommand(commandText.ToString(), _npgsqlConnection);
        command.Parameters.AddRange(parameters.ToArray());

        try
        {
            await using var reader = await command.ExecuteReaderAsync();

            long resultPosition = 0;
            long resultRevision = 0;
            while (await reader.ReadAsync())
            {
                resultPosition = reader.GetInt64(0);
                resultRevision = reader.GetInt64(1);
            }

            Metrics.AppendedEventCounter.Add(events.Count, new TagList
            {
                { "StreamId", streamId },
                { "StreamState", streamState.ToString() }
            });

            return new AppendResult(resultPosition, resultRevision);
        }
        catch (PostgresException e) when (e.SqlState == "23505")
        {
            throw ConcurrencyException.StreamIsNotAtExpectedRevision(streamState.ExpectedRevision, currentlyKnownRevision);
        }
    }

    private async Task<object?> currentRevisionForStream(string streamId)
    {
        var lastRevisionCommand =
            new NpgsqlCommand(
                "SELECT revision FROM events WHERE stream_id = @stream_id ORDER BY revision DESC LIMIT 1;",
                _npgsqlConnection);
        lastRevisionCommand.Parameters.AddWithValue("stream_id", streamId);

        var storedRevision = await lastRevisionCommand.ExecuteScalarAsync();
        return storedRevision;
    }


    public async Task<StreamExistence> StreamExist(string streamId)
    {
        using var activity = Tracing.ActivitySource.StartActivity("StreamExist", ActivityKind.Client);

        var checkStreamCommand = new NpgsqlCommand("SELECT 1 FROM events WHERE stream_id = @stream_id LIMIT 1;",
            _npgsqlConnection);
        checkStreamCommand.Parameters.AddWithValue("stream_id", streamId);

        return await checkStreamCommand.ExecuteScalarAsync() != null ? StreamExistence.Exists : StreamExistence.NotFound;
    }

    public async Task<long> HeadPosition()
    {
        using var activity = Tracing.ActivitySource.StartActivity("HeadPosition", ActivityKind.Client);

        var positionCommand = new NpgsqlCommand("SELECT position FROM events ORDER BY position DESC LIMIT 1;",
            _npgsqlConnection);

        await using var reader = await positionCommand.ExecuteReaderAsync();

        if (await reader.ReadAsync())
        {
            return reader.GetInt64(0);
        }

        return 0;
    }

    public async Task<StreamHead> StreamHead(string streamId)
    {
        using var activity = Tracing.ActivitySource.StartActivity("StreamHead", ActivityKind.Client);

        long revision = 0;
        long position = 0;
        await using var revisionCommand = new NpgsqlCommand(
            "SELECT revision, position FROM events WHERE stream_id = @stream_id ORDER BY position DESC LIMIT 1;",
            _npgsqlConnection)
        {
            Parameters =
            {
                new("@stream_id", streamId),
            }
        };

        await using var revisionReader = await revisionCommand.ExecuteReaderAsync();

        if (await revisionReader.ReadAsync())
        {
            revision = revisionReader.GetInt64(0);
            position = revisionReader.GetInt64(1);
        }


        return new StreamHead(revision, position);
    }
}
