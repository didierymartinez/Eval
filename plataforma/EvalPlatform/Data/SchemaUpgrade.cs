using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EvalPlatform.Data;

// EnsureCreated no modifica una base ya creada. Las columnas nuevas (opcionales o con
// valor por defecto) se agregan aquí para que una base de una versión anterior siga funcionando.
public static class SchemaUpgrade
{
    private static readonly (string Table, string Column, string Type)[] s_columns =
    [
        ("Agents", "TokensPath", "TEXT NULL"),
        ("Agents", "ToolArgumentsPath", "TEXT NULL"),
        ("Agents", "Kind", "TEXT NOT NULL DEFAULT 'http'"),
        ("Agents", "Mcp", "TEXT NOT NULL DEFAULT '{}'"),
        ("Runs", "Note", "TEXT NULL"),
        ("RunResults", "Tokens", "INTEGER NULL"),
        ("RunResults", "ToolCalls", "TEXT NOT NULL DEFAULT '[]'"),
    ];

    public static async Task ApplyAsync(EvalDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            foreach (var (table, column, type) in s_columns)
            {
                await using SqliteCommand info = connection.CreateCommand();
                info.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
                if ((long)(await info.ExecuteScalarAsync())! == 0)
                {
                    await using SqliteCommand alter = connection.CreateCommand();
                    alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {type}";
                    await alter.ExecuteNonQueryAsync();
                }
            }
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
