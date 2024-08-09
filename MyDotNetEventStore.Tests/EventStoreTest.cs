using Npgsql;
using Testcontainers.PostgreSql;

namespace MyDotNetEventStore.Tests;

public class EventStoreTests
{
    private readonly PostgreSqlContainer _postgresContainer = new PostgreSqlBuilder()
        .Build();

    [OneTimeSetUp]
    public async Task OneTimeSetup()
    {
        await _postgresContainer.StartAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        // Stop and dispose of the container
        await _postgresContainer.DisposeAsync();
    }

    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public async Task TestDatabaseConnection()
    {
        var connectionString = _postgresContainer.GetConnectionString();

        using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();

            using (var command = new NpgsqlCommand("SELECT 1", connection))
            {
                var result = await command.ExecuteScalarAsync();
                Assert.AreEqual(1, result);
            }
        }
    }


    public class Reading_a_stream
    {

        [Test]
        public void returns_a_ReadStreamResult_with_State_equals_to_StreamNotFound_when_stream_doesnt_exist()
        {
            var eventStore = new EventStore();

            var readStreamResult = eventStore.ReadStreamAsync("a-stream-that-doesnt-exists");

            Assert.That(readStreamResult.State, Is.EqualTo(ReadState.StreamNotFound));
        }
    }
    
}

public enum ReadState
{
    StreamNotFound
}

public class EventStore
{
    public ReadStreamResult ReadStreamAsync(string streamId)
    {
        return ReadStreamResult.StreamNotFound(streamId);
    }
}

public class ReadStreamResult
{
    public ReadState State()
    {
        return ReadState.StreamNotFound;
    }

    public static ReadStreamResult StreamNotFound(string streamId)
    {
        return new();
    }
}