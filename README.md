# Eval

Herramientas para evaluar si un agente o un prompt LLM responde lo que se espera: le pasas casos de prueba (entrada y lo esperado), se ejecuta el agente con cada uno y se compara su respuesta con lo esperado.

## Plataforma de evaluaciones (empieza aquí)

Aplicación web en español, para técnicos y no técnicos:

- **Conectas el agente:** un endpoint o webhook (C#, Java, Python…), la API de un modelo, un servidor MCP por HTTP, o tu propio código.
- **Subes los casos:** Excel, CSV o Google Sheets.
- **Eliges qué comparar y ejecutas.** Ves qué pasó y qué falló con su motivo, la comparación con la corrida anterior, y puedes descargar el resultado en Excel.
- **Comparas variantes:** los mismos casos con otro modelo, otro prompt, más o menos contexto, o con y sin RAG, lado a lado y con la mejor marcada.

Sirve para dos preguntas:
- **"¿Alcanza?"** Por ejemplo, ¿este agente responde los tickets de nivel 1 con la documentación que tiene?
- **"¿Cuál es mejor?"** Por ejemplo, ¿modelo grande o pequeño, prompt v1 o v2, con o sin RAG?

Es una **plataforma** (servidor web con su base de datos) con **ejecutores** que se despliegan junto a los agentes que no puede alcanzar. Todavía falta una **CLI** para CI, que será un cliente de la misma API. No se instala nada de evaluación dentro de los proyectos.

Para un servicio compartido:
- **Jueces por equipo:** cada equipo usa su propio juez IA (endpoint, modelo y clave cifrada) con un límite de tokens por mes, y el servidor puede prohibir el suyo. Así los tokens no corren por cuenta de quien administra el servidor.
- **Redes sin salida:**
  - los ejecutores solo necesitan un camino saliente hacia la plataforma, y reenvían también las trazas de los agentes de su red;
  - en una red totalmente aislada, la plataforma va adentro y los resultados se llevan con **Exportar / Importar**.
- **Producción con Docker Compose:** [`plataforma/docker-compose.yml`](plataforma/docker-compose.yml). Actualizar es cambiar la imagen; los datos quedan en un volumen.

Si el agente vive en una red privada o no se expone por HTTP, un **ejecutor** dentro de esa red lleva los casos al agente. El ejecutor puede ser el de la plataforma o unas líneas de código en la tecnología del equipo. Solo hace conexiones salientes.

```bash
cd plataforma
dotnet run --project EvalPlatform      # http://localhost:5080 (trae ejemplos listos para ejecutar)
```

Guía completa: [`plataforma/README.md`](plataforma/README.md).

## Documentación

- [`docs/investigacion-evals-llm.md`](docs/investigacion-evals-llm.md): conceptos de evaluación de LLM, herramientas del mercado, cómo conectar un agente y un plan por fases.
- [`docs/evaluar-rag-helpdesk.md`](docs/evaluar-rag-helpdesk.md): cómo saber si la base de conocimiento de un agente de helpdesk con RAG alcanza para el primer nivel.
- [`docs/brechas-vs-promptfoo.md`](docs/brechas-vs-promptfoo.md): qué hace promptfoo que la plataforma no hace (seguridad, ejecución, comparaciones) y qué conviene construir después.

## Alternativas por línea de comandos

Lo mismo sin la plataforma, útil para CI:

| Carpeta | Qué es |
|---|---|
| `promptfoo/` | Arnés con [promptfoo](https://www.promptfoo.dev/) contra un endpoint (`dataset/casos.csv`) |
| `promptfoo/helpdesk/` | Eval de cobertura del RAG contra el webhook de un agente, con diagnóstico por ticket (`dataset/tickets.csv`) |
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

### promptfoo: cobertura del RAG

```bash
cd promptfoo/helpdesk
node mock-agente.js &                               # opcional: agente simulado
export AGENT_WEBHOOK_URL=http://localhost:5678/webhook/helpdesk
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
