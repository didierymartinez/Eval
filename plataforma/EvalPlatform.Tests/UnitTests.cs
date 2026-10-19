using System.Text;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using EvalPlatform.Domain;
using EvalPlatform.Services;

namespace EvalPlatform.Tests;

public class JsonPathTests
{
    // Forma real de un AI Agent de n8n con "Return Intermediate Steps": la observación es JSON en texto
    // que a su vez trae JSON en texto.
    private static readonly JsonNode s_n8n = JsonNode.Parse("""
        [{
          "output": "{\"puede_responder\": true, \"respuesta\": \"Use Olvidé mi contraseña.\"}",
          "intermediateSteps": [
            { "action": { "tool": "base_conocimiento" },
              "observation": "[{\"type\":\"text\",\"text\":\"{\\\"pageContent\\\":\\\"Bloqueo por 30 minutos\\\",\\\"metadata\\\":{}}\"}]" },
            { "action": { "tool": "crear_ticket" }, "observation": "ok" }
          ]
        }]
        """)!;

    [Fact]
    public void Navega_listas_y_json_en_texto()
    {
        Assert.Equal("Use Olvidé mi contraseña.", JsonPath.SelectText(s_n8n, "output.respuesta"));
        Assert.Equal("true", JsonPath.SelectText(s_n8n, "output.puede_responder"));
        Assert.Equal(["base_conocimiento", "crear_ticket"], JsonPath.Select(s_n8n, "intermediateSteps[*].action.tool").Select(JsonPath.ToText));
        Assert.Equal("crear_ticket", JsonPath.SelectText(s_n8n, "[0].intermediateSteps[1].action.tool"));
    }

    [Fact]
    public void Aplana_documentos_recuperados()
    {
        Assert.Equal(["Bloqueo por 30 minutos", "ok"], JsonPath.SelectTexts(s_n8n, "intermediateSteps[*].observation"));
    }

    [Fact]
    public void Ruta_vacia_devuelve_todo_y_ruta_inexistente_nada()
    {
        Assert.Single(JsonPath.Select(s_n8n, ""));
        Assert.Equal("", JsonPath.SelectText(s_n8n, "no.existe"));
        Assert.Equal("", JsonPath.SelectText(JsonNode.Parse("""{"a":[1,2]}"""), "a[5]"));
    }
}

public class TemplatesTests
{
    [Fact]
    public void Cuerpo_json_escapa_valores_del_caso()
    {
        var vars = new Dictionary<string, string> { ["input"] = "Dijo \"hola\"\ny se fue", ["id"] = "7" };
        string body = Templates.RenderBody("""{ "mensaje": "{{input}}", "meta": { "caso": "C-{{id}}", "n": 1 }, "lista": ["{{ id }}"] }""", vars);
        JsonNode json = JsonNode.Parse(body)!;
        Assert.Equal("Dijo \"hola\"\ny se fue", json["mensaje"]!.GetValue<string>());
        Assert.Equal("C-7", json["meta"]!["caso"]!.GetValue<string>());
        Assert.Equal(1, json["meta"]!["n"]!.GetValue<int>());
        Assert.Equal("7", json["lista"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Lee_variables_de_entorno_y_deja_vacias_las_desconocidas()
    {
        Environment.SetEnvironmentVariable("EVAL_TEST_TOKEN", "secreto");
        Assert.Equal("Bearer secreto|", Templates.RenderText("Bearer {{env:EVAL_TEST_TOKEN}}|{{nada}}", new Dictionary<string, string>()));
    }
}

public class DatasetImporterTests
{
    [Fact]
    public void Csv_con_coma_y_comillas()
    {
        ImportedTable t = DatasetImporter.FromCsv(new MemoryStream(Encoding.UTF8.GetBytes("﻿id,input,esperado\n1,\"Hola, ¿qué tal?\",\"Dijo \"\"sí\"\"\"\n\n2,Adiós,\n")));
        Assert.Equal(["id", "input", "esperado"], t.Columns);
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("Hola, ¿qué tal?", t.Rows[0]["input"]);
        Assert.Equal("Dijo \"sí\"", t.Rows[0]["esperado"]);
        Assert.Equal("", t.Rows[1]["esperado"]);
    }

    [Fact]
    public void Csv_de_excel_en_espanol_con_punto_y_coma_y_windows_1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] bytes = Encoding.GetEncoding(1252).GetBytes("pregunta;respuesta\n¿Cuál es el horario?;Lunes a viernes\n");
        ImportedTable t = DatasetImporter.FromCsv(new MemoryStream(bytes));
        Assert.Equal(["pregunta", "respuesta"], t.Columns);
        Assert.Equal("¿Cuál es el horario?", t.Rows[0]["pregunta"]);
    }

    [Fact]
    public void Excel_primera_hoja_con_celdas_vacias_y_encabezados_repetidos()
    {
        using var workbook = new XLWorkbook();
        IXLWorksheet sheet = workbook.AddWorksheet("Casos");
        sheet.Cell(1, 1).Value = "input";
        sheet.Cell(1, 2).Value = "esperado";
        sheet.Cell(1, 3).Value = "esperado";
        sheet.Cell(2, 1).Value = "Hola";
        sheet.Cell(2, 3).Value = 42;
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        ImportedTable t = DatasetImporter.FromXlsx(stream);
        Assert.Equal(["input", "esperado", "esperado_2"], t.Columns);
        Assert.Equal("", t.Rows[0]["esperado"]);
        Assert.Equal("42", t.Rows[0]["esperado_2"]);
    }

    [Theory]
    [InlineData("https://docs.google.com/spreadsheets/d/abc_123/edit?gid=987#gid=987", "https://docs.google.com/spreadsheets/d/abc_123/export?format=csv&gid=987")]
    [InlineData("https://docs.google.com/spreadsheets/d/abc_123/edit?usp=sharing", "https://docs.google.com/spreadsheets/d/abc_123/export?format=csv")]
    public void Convierte_enlace_de_google_sheets(string link, string expected)
    {
        Assert.Equal(expected, DatasetImporter.ToGoogleSheetsExport(new Uri(link))!.ToString());
        Assert.Null(DatasetImporter.ToGoogleSheetsExport(new Uri("https://ejemplo.com/casos.csv")));
    }

    [Fact]
    public void Rechaza_tabla_sin_casos()
    {
        Assert.Throws<ImportException>(() => DatasetImporter.FromCsv(new MemoryStream(Encoding.UTF8.GetBytes("a,b\n"))));
    }
}

public class CheckRunnerTests
{
    private static readonly Agent s_agent = new() { Name = "a", Url = "http://x", ToolsPath = "tools", ContextPath = "ctx" };

    private static CaseData Case(string output, Dictionary<string, string>? row = null, List<string>? tools = null, List<string>? context = null, string? json = null, long latency = 100) =>
        new(s_agent, "¿Pregunta?", row ?? [], new AgentInvocation
        {
            Output = output,
            Tools = tools ?? [],
            Context = context ?? [],
            Json = json is null ? null : JsonNode.Parse(json),
            LatencyMs = latency,
        });

    private static async Task<CheckResult> Run(CheckDefinition check, CaseData data, IJudgeProvider? judge = null) =>
        await new CheckRunner(judge ?? new FakeJudgeProvider(null)).RunAsync(check, data, CancellationToken.None);

    [Theory]
    [InlineData("Sí", "si", true)]
    [InlineData("  TRUE ", "sí", true)]
    [InlineData("Facturación", "facturacion", true)]
    [InlineData("no", "si", false)]
    public async Task Igual_normaliza(string actual, string expected, bool pass)
    {
        CheckResult r = await Run(new() { Type = CheckTypes.Exact, ExpectedValue = expected }, Case(actual));
        Assert.Equal(pass ? CheckStatus.Passed : CheckStatus.Failed, r.Status);
    }

    [Fact]
    public async Task Igual_sobre_otro_campo_de_la_respuesta()
    {
        CheckResult r = await Run(
            new() { Type = CheckTypes.Exact, ExpectedColumn = "debe", ResponsePath = "output.puede_responder" },
            Case("texto", new() { ["debe"] = "no" }, json: """{"output":{"puede_responder":true}}"""));
        Assert.Equal(CheckStatus.Failed, r.Status);
        Assert.Contains("Se esperaba \"no\" y llegó \"sí\"", r.Reason);
    }

    [Fact]
    public async Task Contiene_y_no_contiene_ignoran_tildes_y_mayusculas()
    {
        CheckResult contains = await Run(new() { Type = CheckTypes.Contains, ExpectedColumn = "c" }, Case("El PEDIDO 123 está en camino", new() { ["c"] = "pedido|123|cancelado" }));
        Assert.Equal(CheckStatus.Failed, contains.Status);
        Assert.Equal(2.0 / 3, contains.Score!.Value, 3);
        Assert.Contains("cancelado", contains.Reason);

        CheckResult notContains = await Run(new() { Type = CheckTypes.NotContains, ExpectedValue = "contraseña" }, Case("Tu contrasena es 1234"));
        Assert.Equal(CheckStatus.Failed, notContains.Status);
    }

    [Fact]
    public async Task Similitud_regex_latencia()
    {
        Assert.Equal(CheckStatus.Passed, (await Run(new() { Type = CheckTypes.Similarity, ExpectedValue = "Atendemos de lunes a viernes" }, Case("Atendemos de lunes a viernes."))).Status);
        Assert.Equal(CheckStatus.Failed, (await Run(new() { Type = CheckTypes.Similarity, ExpectedValue = "abc" }, Case("xyz"))).Status);
        Assert.Equal(CheckStatus.Passed, (await Run(new() { Type = CheckTypes.Regex, ExpectedValue = @"T-\d{4}" }, Case("Ticket T-9001 creado"))).Status);
        Assert.Equal(CheckStatus.Error, (await Run(new() { Type = CheckTypes.Regex, ExpectedValue = "(" }, Case("x"))).Status);
        Assert.Equal(CheckStatus.Failed, (await Run(new() { Type = CheckTypes.Latency, Threshold = 50 }, Case("x", latency: 80))).Status);
    }

    [Fact]
    public async Task Herramientas_sin_orden_con_extras_y_ninguna()
    {
        var tools = new List<string> { "crear_ticket", "buscar_pedido", "log" };
        Assert.Equal(CheckStatus.Passed, (await Run(new() { Type = CheckTypes.Tools, ExpectedValue = "buscar_pedido|crear_ticket" }, Case("x", tools: tools))).Status);
        Assert.Equal(CheckStatus.Failed, (await Run(new() { Type = CheckTypes.Tools, ExpectedValue = "actualizar_direccion" }, Case("x", tools: tools))).Status);
        Assert.Equal(CheckStatus.Passed, (await Run(new() { Type = CheckTypes.Tools, ExpectedValue = "Ninguna" }, Case("x"))).Status);
    }

    [Fact]
    public async Task Herramientas_en_orden_permite_otras_en_medio()
    {
        var tools = new List<string> { "buscar_base", "buscar_pedido", "log", "crear_ticket" };
        Assert.Equal(CheckStatus.Passed, (await Run(new() { Type = CheckTypes.ToolsInOrder, ExpectedValue = "buscar_pedido|crear_ticket" }, Case("x", tools: tools))).Status);
        CheckResult wrongOrder = await Run(new() { Type = CheckTypes.ToolsInOrder, ExpectedValue = "crear_ticket|buscar_pedido" }, Case("x", tools: tools));
        Assert.Equal(CheckStatus.Failed, wrongOrder.Status);
        Assert.Contains("crear_ticket → buscar_pedido", wrongOrder.Reason);
    }

    [Fact]
    public async Task Tokens_maximos()
    {
        var agent = new Agent { Name = "a", Url = "http://x", TokensPath = "usage.total_tokens" };
        CaseData With(int? tokens) => new(agent, "q", new Dictionary<string, string>(), new AgentInvocation { Output = "x", Tokens = tokens });
        var runner = new CheckRunner(new FakeJudgeProvider(null));
        Assert.Equal(CheckStatus.Passed, (await runner.RunAsync(new() { Type = CheckTypes.Tokens, Threshold = 500 }, With(480), CancellationToken.None)).Status);
        Assert.Equal(CheckStatus.Failed, (await runner.RunAsync(new() { Type = CheckTypes.Tokens, Threshold = 500 }, With(900), CancellationToken.None)).Status);
        Assert.Equal(CheckStatus.Error, (await runner.RunAsync(new() { Type = CheckTypes.Tokens }, With(null), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Se_omite_si_la_columna_esperada_esta_vacia()
    {
        CheckResult r = await Run(new() { Type = CheckTypes.Contains, ExpectedColumn = "c" }, Case("x", new() { ["c"] = " " }));
        Assert.Equal(CheckStatus.Skipped, r.Status);
    }

    [Fact]
    public async Task Juez_no_configurado_omite()
    {
        CheckResult r = await Run(new() { Type = CheckTypes.Relevance }, Case("x"));
        Assert.Equal(CheckStatus.Skipped, r.Status);
    }

    [Fact]
    public async Task Juez_califica_correccion_rubrica_cobertura_y_fidelidad()
    {
        // 5 si el prompt menciona "30 minutos", 2 si no.
        var judge = new FakeJudgeProvider(new FakeJudgeClient(p => p.Contains("30 minutos") ? 5 : 2));
        var row = new Dictionary<string, string> { ["ref"] = "Se bloquea por 30 minutos." };

        CheckResult correct = await Run(new() { Type = CheckTypes.Correctness, ExpectedColumn = "ref" }, Case("Espere 30 minutos", row), judge);
        Assert.True(correct.Status == CheckStatus.Passed, correct.Reason);
        Assert.Equal("5/5", correct.Display);
        Assert.Equal(1, correct.Score);

        CheckResult rubric = await Run(new() { Type = CheckTypes.Rubric, ExpectedValue = "Es amable" }, Case("Hola"), judge);
        Assert.Equal(CheckStatus.Failed, rubric.Status);
        Assert.Equal("Puntaje 2 del juez de prueba.", rubric.Reason);

        CheckResult coverage = await Run(new() { Type = CheckTypes.Coverage, ExpectedColumn = "ref" }, Case("x", row, context: ["Manual: bloqueo de 30 minutos"]), judge);
        Assert.Equal(CheckStatus.Passed, coverage.Status);

        CheckResult grounded = await Run(new() { Type = CheckTypes.Groundedness, Threshold = 3 }, Case("Inventado", context: ["Otro tema"]), judge);
        Assert.Equal(CheckStatus.Failed, grounded.Status);

        CheckResult noDocs = await Run(new() { Type = CheckTypes.Coverage, ExpectedColumn = "ref" }, Case("x", row), judge);
        Assert.Equal(CheckStatus.Failed, noDocs.Status);
        Assert.Contains("no recuperó", noDocs.Reason);
    }

    [Fact]
    public async Task Rag_sin_ruta_de_contexto_es_error()
    {
        var judge = new FakeJudgeProvider(new FakeJudgeClient(_ => 5));
        var data = new CaseData(new Agent { Name = "a", Url = "http://x" }, "q", new Dictionary<string, string>(), new AgentInvocation { Output = "x" });
        CheckResult r = await new CheckRunner(judge).RunAsync(new() { Type = CheckTypes.Groundedness }, data, CancellationToken.None);
        Assert.Equal(CheckStatus.Error, r.Status);
    }
}
