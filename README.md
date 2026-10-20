# Eval

Herramientas para evaluar si un agente o un prompt LLM responde lo que se espera, comparando su respuesta con ejemplos (como las evaluaciones de n8n), conectándose al agente por URL.

## Plataforma de evaluaciones (empieza aquí)

Aplicación web en español, para técnicos y no técnicos: conectas el agente (webhook de n8n, endpoint en C# u otro, la API de un modelo o un servidor MCP por HTTP), subes los casos (Excel, CSV o Google Sheets), eliges qué comparar y ejecutas. Ves qué pasó y qué falló con su motivo, la comparación con la corrida anterior, y puedes descargar el resultado en Excel.

```bash
cd plataforma
dotnet run --project EvalPlatform      # http://localhost:5080 (trae un ejemplo listo para ejecutar)
```

Guía completa: [`plataforma/README.md`](plataforma/README.md).

## Documentación

- [`docs/investigacion-evals-llm.md`](docs/investigacion-evals-llm.md): conceptos de evaluación de LLM, qué hace n8n, herramientas del mercado, cómo conectar un agente y un plan por fases.
- [`docs/evaluar-rag-helpdesk.md`](docs/evaluar-rag-helpdesk.md): cómo saber si la base de conocimiento de un agente de helpdesk con RAG en n8n alcanza para el primer nivel.

## Alternativas por línea de comandos

Lo mismo sin la plataforma, útil para CI:

| Carpeta | Qué es |
|---|---|
| `promptfoo/` | Arnés con [promptfoo](https://www.promptfoo.dev/) contra un endpoint (`dataset/casos.csv`) |
| `promptfoo/helpdesk/` | Eval de cobertura del RAG contra un webhook de n8n, con diagnóstico por ticket (`dataset/tickets.csv`) |
| `dotnet/AgentEvals.Tests/` | Evals como tests de xUnit con Microsoft.Extensions.AI.Evaluation y reporte HTML |
| `samples/AgentEndpoint/` | Ejemplo en ASP.NET de un endpoint `POST /eval/run` para exponer un agente en C# |

### promptfoo: agente con herramientas

```bash
dotnet run --project samples/AgentEndpoint          # agente de ejemplo en http://localhost:5150
cd promptfoo
export AGENT_URL=http://localhost:5150/eval/run AGENT_EVAL_KEY=dev-eval-key
export ANTHROPIC_API_KEY=...                        # juez para llm-rubric
npx promptfoo@0.124.1 eval --no-cache
```

El caso `PED-006` falla a propósito, para que veas cómo se reporta un fallo.

### promptfoo: cobertura del RAG en n8n

```bash
cd promptfoo/helpdesk
node mock-n8n.js &                                  # opcional: webhook simulado
export N8N_WEBHOOK_URL=http://localhost:5678/webhook/helpdesk
export ANTHROPIC_API_KEY=...
npx promptfoo@0.124.1 eval --no-cache -o resultados.json
node diagnostico.js resultados.json                 # capacidad N1 + diagnostico.csv
```

### .NET (xUnit)

```bash
cd dotnet
dotnet test AgentEvals.Tests                        # necesita samples/AgentEndpoint corriendo
dotnet tool restore && dotnet aieval report --path AgentEvals.Tests/eval-results --output report.html
```
