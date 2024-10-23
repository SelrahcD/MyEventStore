using BenchmarkDotNet.Attributes;
using MyDotNetEventStore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Benchmarks;

public class ReadingAllStream
{
    private readonly PostgreSqlContainer _postgresContainer = new PostgreSqlBuilder()
        .Build();

    private EventStore _eventStore;
    private List<EventData> _list;

    private static NpgsqlConnection Connection;


    [GlobalSetup]
    public async Task GlobalSetup()
    {
        await _postgresContainer.StartAsync();

        Connection = new NpgsqlConnection(_postgresContainer.GetConnectionString());

        await Connection.OpenAsync();

        await EventStoreSchema.BuildSchema(Connection);

        _eventStore = new EventStore(Connection);

        var random = new Random();

        for (var i = 0; i < 10000; i++)
        {
            var streamId = random.NextInt64(1, 300);

            await _eventStore.AppendAsync($"stream-{streamId}", new EventData("event-type", "{}", "{}"));
        }
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        await _postgresContainer.DisposeAsync();
    }

    [Params(Direction.Forward, Direction.Backward)]
    public Direction Direction { get; set; }

    [Benchmark]
    public async Task ReadAllStream()
    {
        var readStreamResult = _eventStore.ReadAllAsync(Direction);

        await foreach (var _ in readStreamResult)
        {
        }
    }
}