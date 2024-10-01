using System.Diagnostics;
using Npgsql;
using OpenTelemetry.Trace;

namespace MyDotNetEventStore;

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
        using var enumerationActivity = Tracing.ActivitySource.StartActivity("GetAsyncEnumerator");

        var readingCommandBuilder = _commandBuilder;

        long lastPosition = 0;
        while (cancellationToken.IsCancellationRequested == false)
        {
            int eventCount = 0;

            using var commandActivity = Tracing.ActivitySource.StartActivity("BuildAndExecuteCommand");
            commandActivity?.SetTag("last_position", lastPosition);

            await using var command = readingCommandBuilder.Build(_npgsqlConnection);

            using var readEventsActivity = Tracing.ActivitySource.StartActivity("ReadEvents", ActivityKind.Client);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            readEventsActivity?.Stop();

            while (await reader.ReadAsync(cancellationToken))
            {
                var (position, resolvedEvent) = ReadingCommandBuilder.BuildOneEvent(reader);

                eventCount++;
                lastPosition = position;

                yield return resolvedEvent;
            }

            commandActivity?.Stop();

            // Todo: Add test when batch size === count of fetched events
            if (eventCount < readingCommandBuilder.BatchSize())
            {
                break;
            }

            readingCommandBuilder = readingCommandBuilder.NextReadingCommandBuilderStartingAtPosition(lastPosition);
        }

        enumerationActivity?.Stop();
    }
}