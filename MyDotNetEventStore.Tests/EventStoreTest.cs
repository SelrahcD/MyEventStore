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
}