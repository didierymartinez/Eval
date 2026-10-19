using System.Text.Json;
using EvalPlatform.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EvalPlatform.Data;

public sealed class EvalDbContext(DbContextOptions<EvalDbContext> options) : DbContext(options)
{
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<Dataset> Datasets => Set<Dataset>();
    public DbSet<Evaluation> Evaluations => Set<Evaluation>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<RunResult> RunResults => Set<RunResult>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        // Las listas y diccionarios se guardan como JSON en una columna de texto.
        model.Entity<Agent>(e => e.Json(a => a.Headers));
        model.Entity<Dataset>(e =>
        {
            e.Json(d => d.Columns);
            e.Json(d => d.Rows);
        });
        model.Entity<Evaluation>(e => e.Json(v => v.Checks));
        model.Entity<Run>(e =>
        {
            e.Json(r => r.Checks);
            e.Property(r => r.Status).HasConversion<string>();
        });
        model.Entity<RunResult>(e =>
        {
            e.HasIndex(r => r.RunId);
            e.Json(r => r.Row);
            e.Json(r => r.Context);
            e.Json(r => r.Tools);
            e.Json(r => r.Checks);
            e.Property(r => r.Outcome).HasConversion<string>();
        });

        // SQLite no ordena DateTimeOffset; se guarda como ticks UTC.
        foreach (var entity in model.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties().Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?)))
            {
                property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
            }
        }
    }
}

internal static class JsonColumnExtensions
{
    private static readonly JsonSerializerOptions s_options = new(JsonSerializerDefaults.Web);

    public static void Json<TEntity, TProperty>(this EntityTypeBuilder<TEntity> builder, System.Linq.Expressions.Expression<Func<TEntity, TProperty>> property)
        where TEntity : class
        where TProperty : class, new()
    {
        builder.Property(property)
            .HasConversion(
                v => JsonSerializer.Serialize(v, s_options),
                v => JsonSerializer.Deserialize<TProperty>(v, s_options) ?? new TProperty(),
                new ValueComparer<TProperty>(
                    (a, b) => JsonSerializer.Serialize(a, s_options) == JsonSerializer.Serialize(b, s_options),
                    v => JsonSerializer.Serialize(v, s_options).GetHashCode(),
                    v => JsonSerializer.Deserialize<TProperty>(JsonSerializer.Serialize(v, s_options), s_options)!))
            .HasColumnType("TEXT");
    }
}
