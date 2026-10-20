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
        ("Agents", "Executor", "TEXT NULL"),
        ("Runs", "Executor", "TEXT NULL"),
    ];

    // Tablas nuevas; mismo esquema que crea EnsureCreated (ver la prueba de migración).
    private const string CreateRunTasks = """
        CREATE TABLE IF NOT EXISTS "RunTasks" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_RunTasks" PRIMARY KEY AUTOINCREMENT,
            "RunId" INTEGER NOT NULL,
            "Group" TEXT NOT NULL,
            "RowIndex" INTEGER NOT NULL,
            "Attempt" INTEGER NOT NULL,
            "Input" TEXT NOT NULL,
            "Row" TEXT NOT NULL,
            "State" TEXT NOT NULL,
            "Worker" TEXT NULL,
            "Deliveries" INTEGER NOT NULL,
            "LeaseUntil" INTEGER NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_RunTasks_Group_State" ON "RunTasks" ("Group", "State");
        CREATE INDEX IF NOT EXISTS "IX_RunTasks_RunId" ON "RunTasks" ("RunId");
        """;

    public static async Task ApplyAsync(EvalDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using (SqliteCommand create = connection.CreateCommand())
            {
                create.CommandText = CreateRunTasks;
                await create.ExecuteNonQueryAsync();
            }
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
