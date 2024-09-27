using Npgsql;
using OneOf;

namespace MyDotNetEventStore.Tests;

public class ReadingCommandBuilder
{
    private int _batchSize = 100;
    private OneOf<long, StreamRevision> _position;
    private string? _streamId;
    private OneOf<long, StreamRevision> _revision;
    private Direction _direction;
    private bool _basedOnRevision;

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

    public ReadingCommandBuilder StartingFromPosition(OneOf<long, StreamRevision> lastPosition)
    {
        _basedOnRevision = false;
        _position = lastPosition;

        return this;
    }

    public ReadingCommandBuilder InDirection(Direction direction)
    {
        _direction = direction;

        return this;
    }

    public ReadingCommandBuilder StartingFromRevision(OneOf<long, StreamRevision> lastRevision)
    {
        _basedOnRevision = true;
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

        // _revision is a long
        if (_basedOnRevision && _revision.IsT0 && _direction == Direction.Forward)
        {
            cmdText += " AND revision >= @lastRevision";
        }

        // _revision is a long
        if (_basedOnRevision && _revision.IsT0 && _direction == Direction.Backward)
        {
            cmdText += " AND revision <= @lastRevision";
        }


        if (!_basedOnRevision && _position.IsT0 && _direction == Direction.Forward)
        {
            cmdText += " AND position > @lastPosition";
        }

        if (!_basedOnRevision && _position.IsT0 && _direction == Direction.Backward)
        {
            cmdText += " AND position < @lastPosition";
        }

        cmdText += " ORDER BY position";

        if (_direction == Direction.Forward)
        {
            cmdText += " ASC";
        }
        else
        {
            cmdText += " DESC";
        }


        if (_revision.IsT1 &&
            ((_revision.AsT1 == StreamRevision.End && _direction == Direction.Forward) ||
             (_revision.AsT1 == StreamRevision.Start && _direction == Direction.Backward))
           )
        {
            cmdText += " LIMIT 0";
        }
        else
        {
            cmdText += " LIMIT @batchSize";
        }

        cmdText += ";";

        var command = new NpgsqlCommand(cmdText, npgsqlConnection);

        if (_position.IsT0)
        {
            command.Parameters.AddWithValue("@lastPosition", _position.Value);
        }

        if (_revision.IsT0)
        {
            // Todo: fail if we are not fetching a stream
            command.Parameters.AddWithValue("@lastRevision", _revision.Value);
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