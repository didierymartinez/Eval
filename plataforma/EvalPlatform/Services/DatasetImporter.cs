using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;

namespace EvalPlatform.Services;

public sealed record ImportedTable(List<string> Columns, List<Dictionary<string, string>> Rows);

public sealed class ImportException(string message) : Exception(message);

// Carga casos desde CSV, Excel (.xlsx) o un enlace (Google Sheets o un CSV publicado).
public sealed partial class DatasetImporter(IHttpClientFactory httpFactory)
{
    public const int MaxRows = 5000;

    public static ImportedTable FromFile(string fileName, Stream stream) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".xlsx" or ".xlsm" => FromXlsx(stream),
            ".csv" or ".txt" or ".tsv" => FromCsv(stream),
            _ => throw new ImportException("Formato no soportado. Sube un archivo .csv o .xlsx."),
        };

    public async Task<ImportedTable> FromUrlAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            throw new ImportException("El enlace no es válido.");
        }

        Uri download = ToGoogleSheetsExport(uri) ?? uri;
        using HttpResponseMessage response = await httpFactory.CreateClient(AgentInvoker.HttpClientName).GetAsync(download, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ImportException($"No se pudo descargar el enlace (HTTP {(int)response.StatusCode}). Si es un Google Sheet, compártelo como 'Cualquier persona con el enlace puede ver'.");
        }

        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is "text/html")
        {
            throw new ImportException("El enlace devolvió una página web, no una tabla. Si es un Google Sheet, compártelo como 'Cualquier persona con el enlace puede ver'.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        bool isExcel = mediaType?.Contains("spreadsheetml", StringComparison.OrdinalIgnoreCase) == true
            || download.AbsolutePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);
        return isExcel ? FromXlsx(buffer) : FromCsv(buffer);
    }

    // https://docs.google.com/spreadsheets/d/<id>/edit?gid=<gid>#gid=<gid>  ->  .../export?format=csv&gid=<gid>
    public static Uri? ToGoogleSheetsExport(Uri uri)
    {
        if (!uri.Host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        Match id = SheetId().Match(uri.AbsolutePath);
        if (!id.Success || uri.AbsolutePath.Contains("/export", StringComparison.Ordinal))
        {
            return null;
        }
        Match gid = Gid().Match(uri.Query + uri.Fragment);
        string query = gid.Success ? $"format=csv&gid={gid.Groups[1].Value}" : "format=csv";
        return new Uri($"https://docs.google.com/spreadsheets/d/{id.Groups[1].Value}/export?{query}");
    }

    public static ImportedTable FromCsv(Stream stream)
    {
        string text = DecodeText(stream);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            DetectDelimiter = true,
            DetectDelimiterValues = [",", ";", "\t"],
            BadDataFound = null,
            MissingFieldFound = null,
            TrimOptions = TrimOptions.Trim,
        };

        using var csv = new CsvParser(new StringReader(text), config);
        var records = new List<string[]>();
        while (csv.Read())
        {
            records.Add(csv.Record ?? []);
        }
        return Build(records);
    }

    public static ImportedTable FromXlsx(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        IXLWorksheet sheet = workbook.Worksheets.First();
        IXLRange? used = sheet.RangeUsed();
        if (used is null)
        {
            throw new ImportException("La hoja está vacía.");
        }

        int columnCount = used.ColumnCount();
        var records = used.Rows()
            .Select(r => Enumerable.Range(1, columnCount).Select(i => r.Cell(i).GetFormattedString()).ToArray())
            .ToList();
        return Build(records);
    }

    private static ImportedTable Build(List<string[]> records)
    {
        records = records.Where(r => r.Any(v => !string.IsNullOrWhiteSpace(v))).ToList();
        if (records.Count < 2)
        {
            throw new ImportException("La tabla necesita una fila de encabezados y al menos un caso.");
        }
        if (records.Count - 1 > MaxRows)
        {
            throw new ImportException($"La tabla tiene más de {MaxRows} casos. Divídela en varias.");
        }

        var columns = new List<string>();
        foreach (var (header, i) in records[0].Select((h, i) => (h.Trim().TrimStart('﻿'), i)))
        {
            string name = header.Length > 0 ? header : $"columna_{i + 1}";
            string unique = name;
            for (int n = 2; columns.Contains(unique, StringComparer.OrdinalIgnoreCase); n++)
            {
                unique = $"{name}_{n}";
            }
            columns.Add(unique);
        }

        var rows = records.Skip(1)
            .Select(r => columns.Select((c, i) => (c, v: i < r.Length ? r[i].Trim() : "")).ToDictionary(x => x.c, x => x.v))
            .ToList();
        return new ImportedTable(columns, rows);
    }

    // Excel en español guarda CSV en Windows-1252; "CSV UTF-8" y Google Sheets, en UTF-8.
    private static string DecodeText(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    [GeneratedRegex(@"/spreadsheets/d/([a-zA-Z0-9_-]+)")]
    private static partial Regex SheetId();

    [GeneratedRegex(@"gid=(\d+)")]
    private static partial Regex Gid();
}
