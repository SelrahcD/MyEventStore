using BenchmarkDotNet.Attributes;
using MyDotNetEventStore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Benchmarks;

[MemoryDiagnoser]
public class ReadingLongStream
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

        _list = new List<EventData>();

        for (var i = 0; i < 10000; i++)
        {
            _list.Add(new EventData("event-type", "{}", "{}"));
        }

        await _eventStore.AppendAsync("a-stream", _list);
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        await _postgresContainer.DisposeAsync();
    }

    [Params(Direction.Forward, Direction.Backward)]
    public Direction Direction { get; set; }

    [Benchmark(Baseline = true)]
    public async Task ReadStream()
    {
        var readStreamResult = _eventStore.ReadStreamAsync(Direction, "a-stream");

        await foreach (var _ in readStreamResult)
        {
        }
    }
}