using Npgsql;
using Testcontainers.PostgreSql;

namespace MyDotNetEventStore.Tests;

[TestFixture]
public abstract class EventStoreTests
{
    private readonly PostgreSqlContainer _postgresContainer = new PostgreSqlBuilder()
        .Build();

    [OneTimeSetUp]
    public async Task OneTimeSetup()
    {
        await _postgresContainer.StartAsync();

        await using var connection = new NpgsqlConnection(_postgresContainer.GetConnectionString());

        await connection.OpenAsync();

        var command = new NpgsqlCommand($"""
                                         CREATE TABLE IF NOT EXISTS streams (
                                             id SERIAL PRIMARY KEY,
                                             stream_id TEXT NOT NULL
                                         );
                                         """, connection);

        await command.ExecuteNonQueryAsync();
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

    public class ReadingAStream : EventStoreTests
    {

        public class WhenTheStreamDoesntExists : EventStoreTests
        {
            [Test]
            public async Task returns_a_ReadStreamResult_with_State_equals_to_StreamNotFound()
            {
                await using var connection = new NpgsqlConnection(_postgresContainer.GetConnectionString());

                await connection.OpenAsync();

                var eventStore = new EventStore(connection);

                var readStreamResult = await eventStore.ReadStreamAsync("a-stream-that-doesnt-exists");

                Assert.That(readStreamResult.State, Is.EqualTo(ReadState.StreamNotFound));
            }
        }

        public class WhenTheStreamExists: EventStoreTests
        {
            [Test]
            public async Task returns_a_ReadStreamResult_with_a_State_equals_to_Ok()
            {
                await using var connection = new NpgsqlConnection(_postgresContainer.GetConnectionString());

                await connection.OpenAsync();

                var eventStore = new EventStore(connection);

                await eventStore.AppendAsync("stream-id");

                var readStreamResult = await eventStore.ReadStreamAsync("stream-id");

                Assert.That(readStreamResult.State, Is.EqualTo(ReadState.Ok));
            }
        }

    }
    
}

public enum ReadState
{
    StreamNotFound,
    Ok
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
        var command = new NpgsqlCommand("SELECT 1 FROM streams WHERE stream_id = @stream_id;", _npgsqlConnection);
        command.Parameters.AddWithValue("stream_id", streamId);

        var result = await command.ExecuteScalarAsync();

        // If result is not null, the stream exists
        return result != null ? ReadStreamResult.StreamFound(streamId) : ReadStreamResult.StreamNotFound(streamId);
    }

    public async Task AppendAsync(string streamId)
    {
        var command = new NpgsqlCommand("INSERT INTO streams (stream_id) VALUES (@stream_id);", _npgsqlConnection);
        command.Parameters.AddWithValue("stream_id", streamId);

        await command.ExecuteNonQueryAsync();
    }
}

public class ReadStreamResult
{
    private readonly ReadState _state;

    private ReadStreamResult(ReadState state)
    {
        _state = state;
    }

    public ReadState State()
    {
        return _state;
    }

    public static ReadStreamResult StreamNotFound(string streamId)
    {
        return new(ReadState.StreamNotFound);
    }

    public static ReadStreamResult StreamFound(string streamId)
    {
        return new(ReadState.Ok);
    }
}