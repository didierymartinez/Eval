using System.Text.Json;
using System.Text.Json.Serialization;
using EvalPlatform.Api;
using EvalPlatform.Data;
using EvalPlatform.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

// Modo ejecutor: corre dentro de otra red y le lleva los casos al agente (ver ExecutorWorker).
if (args.Length > 0 && args[0] == "ejecutor")
{
    Environment.ExitCode = await ExecutorWorker.RunAsync(args[1..]);
    return;
}

var builder = WebApplication.CreateBuilder(args);

// La ruta relativa de la base se toma desde la carpeta de la aplicación, no desde el directorio de trabajo.
(string connectionString, string dataFolder) = ResolveSqlite(builder.Configuration.GetConnectionString("Eval") ?? "Data Source=datos/eval.db", builder.Environment.ContentRootPath);

builder.Services.AddDbContextFactory<EvalDbContext>(o => o.UseSqlite(connectionString));

// Llaves para cifrar las claves de los jueces de equipo: junto a la base, en el mismo volumen.
builder.Services.AddDataProtection()
    .SetApplicationName("EvalPlatform")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataFolder, "claves")));
builder.Services.Configure<LimitOptions>(builder.Configuration.GetSection("Limites"));
builder.Services.Configure<PlatformOptions>(builder.Configuration.GetSection("Plataforma"));
builder.Services.Configure<JudgeOptions>(builder.Configuration.GetSection("Juez"));
builder.Services.Configure<RunnerOptions>(builder.Configuration.GetSection("Corridas"));
builder.Services.Configure<ExecutorOptions>(builder.Configuration.GetSection("Ejecutores"));
builder.Services.Configure<TraceOptions>(builder.Configuration.GetSection("Trazas"));

// Sin límite global: cada agente define su propio tiempo máximo.
builder.Services.AddHttpClient(AgentInvoker.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);

builder.Services.AddSingleton<IJudgeProvider, OpenAiCompatibleJudgeProvider>();
builder.Services.AddSingleton<IJudgeClientFactory, OpenAiJudgeClientFactory>();
builder.Services.AddSingleton<JudgeSecrets>();
builder.Services.AddSingleton<JudgeResolver>();
builder.Services.AddSingleton<JudgeBudget>();
builder.Services.AddSingleton<AgentInvoker>();
builder.Services.AddSingleton<IAgentModelFactory, OpenAiCompatibleModelFactory>();
builder.Services.AddSingleton<McpInvoker>();
builder.Services.AddSingleton<AgentDispatcher>();
builder.Services.AddSingleton<DatasetImporter>();
builder.Services.AddSingleton<CheckRunner>();
builder.Services.AddSingleton<RunQueue>();
builder.Services.AddSingleton<RunRecorder>();
builder.Services.AddSingleton<RemoteTasks>();
builder.Services.AddSingleton<TraceStore>();
builder.Services.AddScoped<RunExecutor>();
builder.Services.AddHostedService<RunWorker>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

bool demo = builder.Configuration.GetValue("Demo:Habilitado", true);
if (demo)
{
    // Servidor MCP de demostración en /demo/mcp.
    builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "demo-cartera", Version = "1.0" })
        .WithHttpTransport(o => o.Stateless = true)
        .WithTools<DemoMcpTools>();
}

var app = builder.Build();

await using (EvalDbContext db = await app.Services.GetRequiredService<IDbContextFactory<EvalDbContext>>().CreateDbContextAsync())
{
    await SchemaUpgrade.ApplyAsync(db);
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapEvalApi();
app.MapExecutorApi();
app.MapTraceReceiver();

// Agente de demostración para probar la plataforma sin conectar nada.
if (demo)
{
    app.MapMcp("/demo/mcp");

    app.MapPost("/demo/helpdesk", (JsonElement body) =>
        DemoHelpdeskAgent.Respond(
            body.TryGetProperty("mensaje", out JsonElement m) ? m.GetString() ?? "" : "",
            body.TryGetProperty("base", out JsonElement b) ? b.GetString() : null));

    if (app.Configuration.GetValue("Demo:CrearEjemplo", true))
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            string baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault() ?? "http://localhost:5080";
            baseUrl = baseUrl.Replace("://+", "://localhost").Replace("://*", "://localhost").Replace("://0.0.0.0", "://localhost").Replace("://[::]", "://localhost");
            using EvalDbContext db = app.Services.GetRequiredService<IDbContextFactory<EvalDbContext>>().CreateDbContext();
            DemoSeeder.SeedAsync(db, baseUrl).GetAwaiter().GetResult();
        });
    }
}

app.Run();

// Devuelve la cadena con la ruta absoluta de la base (y crea su carpeta) y la carpeta de datos.
static (string ConnectionString, string DataFolder) ResolveSqlite(string connectionString, string contentRoot)
{
    var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
    if (string.IsNullOrEmpty(builder.DataSource) || builder.DataSource == ":memory:")
    {
        return (connectionString, Path.Combine(contentRoot, "datos"));
    }
    string path = Path.IsPathRooted(builder.DataSource) ? builder.DataSource : Path.GetFullPath(Path.Combine(contentRoot, builder.DataSource));
    string folder = Path.GetDirectoryName(path)!;
    Directory.CreateDirectory(folder);
    builder.DataSource = path;
    return (builder.ToString(), folder);
}

public partial class Program;
