using Npgsql;

namespace MyDotNetEventStore;

public static class EventStoreSchema
{
    public static async Task BuildSchema(NpgsqlConnection npgsqlConnection)
    {
        var command = new NpgsqlCommand($"""
                                         CREATE TABLE IF NOT EXISTS events (
                                             position SERIAL PRIMARY KEY,
                                             id UUID NOT NULL,
                                             stream_id TEXT NOT NULL,
                                             revision BIGINT NOT NULL,
                                             event_type TEXT NOT NULL,
                                             data JSONB,
                                             metadata JSONB,
                                             UNIQUE (stream_id, revision)
                                         );
                                         """, npgsqlConnection);

        await command.ExecuteNonQueryAsync();
    }
}