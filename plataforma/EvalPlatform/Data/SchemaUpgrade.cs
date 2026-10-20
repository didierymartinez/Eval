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
        ("Agents", "TracesEnabled", "INTEGER NOT NULL DEFAULT 0"),
        ("Agents", "TraceWaitSeconds", "INTEGER NOT NULL DEFAULT 15"),
        ("RunResults", "TraceId", "TEXT NULL"),
        ("RunResults", "Trace", "TEXT NOT NULL DEFAULT '{}'"),
        ("RunTasks", "TraceId", "TEXT NULL"),
        ("Evaluations", "Variants", "TEXT NOT NULL DEFAULT '[]'"),
        ("Runs", "AgentId", "INTEGER NULL"),
        ("Runs", "Variant", "TEXT NULL"),
        ("Runs", "Parameters", "TEXT NOT NULL DEFAULT '{}'"),
        ("Runs", "ComparisonId", "INTEGER NULL"),
    ];

    // Tablas nuevas; mismo esquema que crea EnsureCreated (ver la prueba de migración).
    private const string CreateTables = """
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
        CREATE TABLE IF NOT EXISTS "Comparisons" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Comparisons" PRIMARY KEY AUTOINCREMENT,
            "EvaluationId" INTEGER NOT NULL,
            "EvaluationName" TEXT NOT NULL,
            "DatasetName" TEXT NOT NULL,
            "Note" TEXT NULL,
            "CreatedAt" INTEGER NOT NULL
        );
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
                create.CommandText = CreateTables;
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
            await using (SqliteCommand index = connection.CreateCommand())
            {
                index.CommandText = """CREATE INDEX IF NOT EXISTS "IX_Runs_ComparisonId" ON "Runs" ("ComparisonId");""";
                await index.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
