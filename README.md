# Eval

Investigación y kit de inicio para evaluar prompts y agentes LLM contra un dataset de casos, al estilo de las evaluaciones de n8n pero directo contra tu código (C# u otro lenguaje) por URL.

- **Investigación completa:** [`docs/investigacion-evals-llm.md`](docs/investigacion-evals-llm.md)
- **Caso helpdesk con RAG en n8n** (¿la base de conocimiento alcanza para el primer nivel?): [`docs/evaluar-rag-helpdesk.md`](docs/evaluar-rag-helpdesk.md)

## Caso helpdesk: cobertura de la base de conocimiento

Manda cada ticket de `dataset/tickets.csv` al webhook de n8n y clasifica cada uno: *cubierto*, *brecha de conocimiento*, *alucinación*, *escaló sin necesidad*, etc.

```bash
cd promptfoo/helpdesk
node mock-n8n.js &                                   # opcional: webhook simulado
export N8N_WEBHOOK_URL=http://localhost:5678/webhook/helpdesk
export ANTHROPIC_API_KEY=...                         # modelo juez
npx promptfoo@0.124.1 eval --no-cache -o resultados.json
node diagnostico.js resultados.json                  # capacidad N1 + diagnostico.csv
```

## Qué hay aquí

| Carpeta | Qué es |
|---|---|
| `dataset/tickets.csv` + `promptfoo/helpdesk/` | Eval de cobertura del RAG para el helpdesk (webhook de n8n) |
| `dataset/casos.csv` | Los casos de prueba (lo que en n8n sería el Google Sheet). Lo usan los dos arneses |
| `samples/AgentEndpoint/` | API ASP.NET mínima que expone `POST /eval/run` con un agente de mentira. Es la plantilla de lo que hay que agregarle a tu agente real |
| `promptfoo/` | Arnés con [promptfoo](https://www.promptfoo.dev/): lee el CSV (o un Google Sheet), llama la URL y califica |
| `dotnet/AgentEvals.Tests/` | Arnés en .NET con xUnit y Microsoft.Extensions.AI.Evaluation: un test por caso y reporte HTML |

El caso `PED-006` falla a propósito (el agente de mentira no llama `actualizar_direccion`), para que veas cómo se reporta un fallo.

## Probarlo (ejemplo general de agente con herramientas)

Requisitos: .NET 10 SDK y Node.js 20+.

```bash
# 1. Levantar el agente de ejemplo (http://localhost:5150)
dotnet run --project samples/AgentEndpoint
```

**Con promptfoo** (en otra terminal):

```bash
cd promptfoo
export AGENT_URL=http://localhost:5150/eval/run AGENT_EVAL_KEY=dev-eval-key
export ANTHROPIC_API_KEY=...        # para el juez llm-rubric (se puede cambiar de proveedor en la config)
npx promptfoo@0.124.1 eval --no-cache
npx promptfoo@0.124.1 view          # UI con el detalle por caso
```

**Con .NET:**

```bash
cd dotnet
dotnet test AgentEvals.Tests        # chequeos deterministas
# Opcional: juez LLM (cualquier endpoint compatible con OpenAI)
JUDGE_API_KEY=... JUDGE_MODEL=... JUDGE_ENDPOINT=... dotnet test AgentEvals.Tests
# Reporte HTML
dotnet tool restore
dotnet aieval report --path AgentEvals.Tests/eval-results --output report.html
```

## Conectar tu agente real

1. Copia el contrato de `samples/AgentEndpoint/Contracts.cs` y agrega `POST /eval/run` a tu servicio, devolviendo la respuesta y las herramientas que llamó (ver sección 6 de la investigación).
2. Reemplaza los casos de `dataset/casos.csv` por los tuyos, o apunta `tests:` de `promptfoo/promptfooconfig.yaml` a tu Google Sheet.
3. Cambia `AGENT_URL` a la URL de tu agente.
