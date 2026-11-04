using System.Text.Json.Nodes;
using Npgsql;

namespace BugHunter.Api.Storage;

// PostgreSQL backend: one JSONB table, so several API instances can share hunts and app maps. Selected with
// Storage:Provider=Postgres and a connection string in ConnectionStrings:BugHunter.
public sealed class PostgresDocStore(string connectionString) : IDocStore, IAsyncDisposable
{
    readonly NpgsqlDataSource _db = NpgsqlDataSource.Create(connectionString);

    public string Describe => $"PostgreSQL at {new NpgsqlConnectionStringBuilder(connectionString) { Password = null }.Host}";

    public async Task InitAsync(CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("""
            CREATE TABLE IF NOT EXISTS documents (
                area text NOT NULL, kind text NOT NULL, key text NOT NULL,
                data jsonb NOT NULL, updated_at timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (area, kind, key));
            CREATE INDEX IF NOT EXISTS documents_updated ON documents (area, kind, updated_at DESC);
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<JsonObject?> GetAsync(string area, string kind, string key, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("SELECT data::text FROM documents WHERE area = $1 AND kind = $2 AND key = $3");
        cmd.Parameters.AddWithValue(area); cmd.Parameters.AddWithValue(kind); cmd.Parameters.AddWithValue(key);
        if (await cmd.ExecuteScalarAsync(ct) is not string json) return null;
        var o = JsonNode.Parse(json)!.AsObject(); o["_key"] = key; return o;
    }

    public async Task<List<JsonObject>> ListAsync(string area, string kind, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("SELECT key, data::text FROM documents WHERE area = $1 AND kind = $2");
        cmd.Parameters.AddWithValue(area); cmd.Parameters.AddWithValue(kind);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<JsonObject>();
        while (await r.ReadAsync(ct)) { var o = JsonNode.Parse(r.GetString(1))!.AsObject(); o["_key"] = r.GetString(0); list.Add(o); }
        return list;
    }

    public async Task PutAsync(string area, string kind, string key, JsonObject data, CancellationToken ct)
    {
        data.Remove("_key");
        await using var cmd = _db.CreateCommand("""
            INSERT INTO documents (area, kind, key, data, updated_at) VALUES ($1, $2, $3, $4::jsonb, now())
            ON CONFLICT (area, kind, key) DO UPDATE SET data = EXCLUDED.data, updated_at = now()
            """);
        cmd.Parameters.AddWithValue(area); cmd.Parameters.AddWithValue(kind); cmd.Parameters.AddWithValue(key); cmd.Parameters.AddWithValue(data.ToJsonString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string area, string kind, string key, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM documents WHERE area = $1 AND kind = $2 AND key = $3");
        cmd.Parameters.AddWithValue(area); cmd.Parameters.AddWithValue(kind); cmd.Parameters.AddWithValue(key);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
