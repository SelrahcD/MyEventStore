using Npgsql;

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