# Evals y testing para prompts y agentes LLM

Investigación y plan para evaluar si un prompt o un agente (en C# u otro lenguaje) hace lo que debe. Fecha: octubre de 2026.

> Para el caso concreto de un agente de helpdesk con RAG detrás de un webhook, ver [evaluar-rag-helpdesk.md](evaluar-rag-helpdesk.md).

---

## 1. Respuesta corta

- **Lo que buscas tiene nombre: evaluación offline sobre un dataset.** Tienes una tabla de casos (entrada + lo esperado), un arnés que corre el agente por cada fila y unos *graders* (evaluadores) que comparan el resultado con lo esperado y le ponen un puntaje. La tabla puede ser un Google Sheet, un Excel o un CSV.
- **Para tu caso (agente en C#), lo más directo es exponer un endpoint HTTP de evaluación** en tu agente (`POST /eval/run`) que devuelva la respuesta final **y las herramientas que llamó**. Con eso cualquier arnés lo puede probar por URL, sin importar el lenguaje.
- **Dos arneses recomendados**, y los dos están armados y probados en este repo:
  1. **promptfoo** (CLI open source): lee el dataset desde CSV **o directamente desde Google Sheets**, llama tu URL, aplica chequeos y un juez LLM, y puede **escribir los resultados de vuelta en Sheets**.
  2. **Microsoft.Extensions.AI.Evaluation** (librerías oficiales de .NET): los evals quedan como tests de xUnit/MSTest en tu solución, con caché de respuestas del juez y un reporte HTML (`dotnet aieval report`). Sirve para tenerlo dentro del CI de .NET.
- **Empieza chico:** de 20 a 50 casos sacados de fallos reales, con chequeos deterministas donde se pueda y juez LLM solo donde haga falta. Lee las salidas, no solo los puntajes.

---

## 2. Conceptos que conviene tener claros

| Término | Qué es | En este repo |
|---|---|---|
| **Caso / tarea** | Una entrada con criterios de éxito definidos | Una fila de `dataset/casos.csv` |
| **Trial** | Un intento de un caso. Los LLM no son deterministas, así que conviene correr varios | `promptfoo eval --repeat 3` |
| **Grader / evaluador** | Lógica que califica un aspecto del resultado. Un caso puede tener varios | `assertions.js`, `DeterministicEvaluators.cs`, `RubricEvaluator.cs` |
| **Transcript / traza** | Registro completo del intento: mensajes, llamadas a herramientas, resultados intermedios | `toolCalls` en la respuesta del endpoint |
| **Outcome / resultado** | El estado final del mundo (¿quedó creado el ticket en la base de datos?) | Se revisa con chequeos sobre tu sistema, no solo sobre el texto |
| **Harness / arnés** | Lo que corre todo: casos → agente → graders → resultados | promptfoo o el proyecto `AgentEvals.Tests` |

Distinciones que cambian cómo usas los resultados:

- **Offline vs. online.** *Offline*: corres un dataset fijo antes de publicar un cambio (lo que pides). *Online*: calificas tráfico real en producción usando trazas. Primero offline; online después.
- **Evals de capacidad vs. de regresión.** Los de *capacidad* miden algo que el agente todavía no hace bien (esperas un % bajo que suba). Los de *regresión* deben estar cerca del 100% y sirven de alarma si un cambio de prompt rompe algo que ya funcionaba. Cuando un eval de capacidad llega alto, pasa a ser de regresión.
- **pass@k vs. pass^k.** *pass@k* = probabilidad de que **al menos uno** de k intentos salga bien. *pass^k* = probabilidad de que **los k** salgan bien. Para un agente que atiende clientes importa pass^k: con 75% de éxito por intento, pass^3 es solo ~42%. Por eso conviene repetir cada caso.

---

## 3. Métricas típicas de una evaluación sobre un dataset

Casi todas las herramientas de evaluación traen un conjunto parecido de métricas básicas:

| Métrica | Tipo | En este repo |
|---|---|---|
| Corrección frente a una respuesta de referencia (1–5) | Juez LLM | `llm-rubric` / `RubricEvaluator`; en la plataforma, *Correcta frente a lo esperado (IA)* |
| Utilidad o relevancia: ¿responde la pregunta? (1–5) | Juez LLM | `RelevanceEvaluator`; *Responde lo que se preguntó (IA)* |
| Similitud de texto (0–1) | Distancia de edición | `similar` / `levenshtein` en promptfoo; *Se parece a (texto)* |
| Categorización (0/1) | Coincidencia exacta | `equals` en promptfoo; *Es igual a* |
| Herramientas usadas | ¿Llamó las herramientas esperadas? | columna `expected_tools`; *Usó las herramientas* |
| Personalizada | Lo que calcules | Assertions JS/Python o `IEvaluator` propio |

Lo importante es que la evaluación vaya contra tu código directamente, sin envolver el agente en otra herramienta solo para poder evaluarlo.

---

## 4. Qué medir en un prompt o agente

Para un agente no basta con mirar el texto final. Hay cinco capas:

1. **Respuesta final**: ¿es correcta, completa, en el tono pedido? (contiene X, no contiene Y, coincide con la referencia, cumple la rúbrica).
2. **Uso de herramientas**: ¿llamó las herramientas necesarias, con argumentos correctos? ¿Evitó llamar las que no debía?
3. **Resultado en el sistema (outcome)**: ¿quedó creado el ticket, actualizada la dirección? Esto se revisa contra tu base de datos o un mock, no contra lo que *dice* el agente.
4. **Seguridad y límites**: inyección de prompt, fuga del prompt de sistema o datos sensibles, temas fuera de alcance.
5. **Costo y latencia**: tokens y milisegundos por caso, para que una mejora de calidad no triplique el costo sin que lo notes.

**No califiques un camino rígido.** Exige las herramientas necesarias, pero permite que el agente llegue por otro orden o haga una consulta extra. Si exiges la secuencia exacta, vas a castigar soluciones válidas. (En este repo `expected_tools` revisa que estén las requeridas, sin importar el orden y permitiendo extras.)

### Tipos de evaluadores

| Tipo | Ejemplos | A favor | En contra |
|---|---|---|---|
| **Deterministas (código)** | contiene, regex, JSON válido, herramienta llamada, valor en BD | Rápidos, gratis, reproducibles | Frágiles con texto libre |
| **Juez LLM** | rúbrica, equivalencia con la referencia, relevancia | Entienden matices y texto abierto | Cuestan, varían, hay que calibrarlos |
| **Humanos** | revisión de una muestra, etiquetado | La referencia de verdad | Lentos y caros |

Regla práctica: **determinista donde se pueda, juez LLM donde haga falta, humanos para calibrar.** Para que el juez sea confiable:

- Rúbricas concretas y verificables ("menciona el número de pedido y la fecha de entrega"), no "es una buena respuesta".
- Escalas cortas (pasa/no pasa o 1–5 con significado de cada nivel), temperatura 0.
- Usa como juez un modelo igual o más capaz que el del agente y, si puedes, de otro proveedor, para reducir sesgos.
- Calibra: etiqueta a mano 20–30 casos y compara con el juez. Si no coinciden, ajusta la rúbrica antes de confiar en el número.

---

## 5. Panorama de herramientas

| Herramienta | Qué es | Lenguaje | Llama tu agente por URL | Dataset desde Sheets | Cuándo usarla |
|---|---|---|---|---|---|
| **[promptfoo](https://www.promptfoo.dev/)** | CLI open source, config en YAML | Cualquiera (vía HTTP) | Sí, provider `https` | **Sí, lee y escribe Google Sheets** | Arnés principal, agnóstico al lenguaje, apto para CI. También hace red teaming |
| **[Microsoft.Extensions.AI.Evaluation](https://learn.microsoft.com/dotnet/ai/evaluation/libraries)** | Librerías NuGet oficiales | C# / .NET | Lo que programes (aquí: HttpClient) | Lo que programes (aquí: CSV) | Evals como tests .NET, reporte HTML, caché del juez. Trae evaluadores de agente: `ToolCallAccuracy`, `TaskAdherence`, `IntentResolution` |
| **[DeepEval](https://deepeval.com/)** | Librería Python estilo pytest, 50+ métricas | Python | Lo que programes | Lo que programes | Si tu equipo prefiere Python o quieres métricas de RAG listas |
| **[Langfuse](https://langfuse.com/)** | Plataforma open source de trazas, datasets y experimentos | SDKs + OpenTelemetry | Vía SDK | Importación | Observabilidad en producción y evals online. Self-host (útil por datos sensibles) |
| **Braintrust, LangSmith, Arize Phoenix** | Plataformas de evals y trazas (SaaS / open core) | SDKs | Vía SDK | Importación | Cuando un equipo grande necesita UI compartida, curación de datasets y comparación de experimentos |

Un patrón común en 2026: una herramienta liviana (promptfoo o DeepEval) como *gate* de CI en cada PR, y una plataforma de trazas (Langfuse, Braintrust) para producción y para alimentar el dataset con casos reales. Ojo: casi todas las comparativas que circulan las publica algún proveedor; tómalas como punto de partida.

---

## 6. Cómo conectar tu agente C# al arnés

### Opción A — Endpoint HTTP de evaluación (recomendada para empezar)

Agregas a tu servicio un endpoint que recibe la entrada, corre el agente y devuelve un JSON con la respuesta y las herramientas usadas. Contrato usado en este repo (`samples/AgentEndpoint/Contracts.cs`):

```http
POST /eval/run
X-Eval-Key: <secreto>
Content-Type: application/json

{ "input": "¿Dónde está mi pedido 12345?", "sessionId": null, "context": { "caso": "PED-001" } }
```

```json
{
  "output": "Tu pedido 12345 está en camino y llega el 22 de octubre.",
  "toolCalls": [
    { "name": "buscar_pedido", "arguments": { "pedido": "12345" }, "result": "{...}" }
  ],
  "usage": { "inputTokens": 210, "outputTokens": 35 },
  "latencyMs": 840,
  "traceId": "966fb8ec..."
}
```

- **A favor:** sirve para cualquier arnés (promptfoo, el runner .NET, la plataforma, Postman) y prueba el agente tal como corre de verdad: prompt, configuración, herramientas.
- **En contra:** hay que proteger el endpoint (solo en ambientes no productivos, con API key) y las herramientas con efectos reales necesitan un ambiente de prueba.

**Cómo capturar las herramientas que llamó el agente** (lo que más se suele olvidar):

- **Microsoft.Extensions.AI** (`FunctionInvokingChatClient`): el `ChatResponse` final trae en `response.Messages` los `FunctionCallContent` (nombre + argumentos) y los `FunctionResultContent`. Recórrelos y mapéalos a `toolCalls`.
- **Semantic Kernel**: registra un `IAutoFunctionInvocationFilter` que anote cada función invocada (nombre, argumentos, resultado) en una lista asociada a la petición.
- **Microsoft Agent Framework / otros SDKs**: casi todos exponen el historial de mensajes de la corrida o un middleware de herramientas; la idea es la misma.
- **Llamadas directas a la API del proveedor**: ya tienes los bloques `tool_use` / `tool_calls` en cada respuesta; acumúlalos en tu bucle.

### Opción B — Dentro del proceso, como tests .NET

El proyecto de tests referencia tu librería del agente y lo invoca directo (sin HTTP). Es más rápido y fácil de depurar, y puedes inyectar mocks de herramientas por DI. El runner de este repo usa HTTP para que funcione igual con agentes que no son .NET, pero cambiar `AgentClient` por una llamada directa a tu clase es un cambio de pocas líneas.

### Opción C — Por trazas (OpenTelemetry), para producción

Instrumentas el agente con OpenTelemetry (MEAI y Semantic Kernel ya emiten spans con las convenciones GenAI) y mandas las trazas a Langfuse, Phoenix u otra plataforma. Ahí calificas conversaciones reales y conviertes las malas en casos nuevos del dataset. No reemplaza a A o B; las complementa.

### Opción D — Un ejecutor dentro de la red del agente

Si el agente solo es alcanzable dentro de una red privada, o no se expone por HTTP (gRPC, colas, una librería, un proceso por consola), un **ejecutor** dentro de esa red pide los casos a la plataforma, llama al agente localmente y devuelve las respuestas. Solo hace conexiones salientes. Puede ser el ejecutor genérico de la plataforma (para agentes HTTP o MCP) o unas líneas en la tecnología del equipo. Detalle en [`plataforma/README.md`](../plataforma/README.md#agentes-en-redes-privadas-u-otras-tecnologías).

### Cuidados al conectar

- **Efectos secundarios:** si una herramienta envía correos, cobra o escribe en producción, en modo evaluación debe apuntar a un sandbox o a un mock. Puedes pasar un flag en `context` y que tu DI cambie las implementaciones.
- **Datos fijos:** los casos que consultan "el pedido 12345" necesitan que ese pedido exista en el ambiente de prueba con datos estables, o las respuestas cambian entre corridas por razones que no son del agente.
- **Versión bajo prueba:** devuelve en la respuesta (o en un header) la versión del prompt y del modelo, para saber exactamente qué se evaluó.
- **Multi-turno:** si el agente mantiene conversación, usa `sessionId`. promptfoo soporta sesiones en el provider HTTP (`sessionParser` o un `sessionId` generado por caso).

---

## 7. Diseño del dataset

Columnas usadas aquí (sirven igual en CSV o en Google Sheets):

| Columna | Para qué | Ejemplo |
|---|---|---|
| `id` | Identificador estable del caso | `PED-002` |
| `categoria` | Agrupar resultados en el reporte | `devolucion` |
| `input` | Lo que escribe el usuario | `Quiero devolver el producto del pedido 777…` |
| `expected_output` | Respuesta de referencia (orientativa) | `Se creó un ticket de devolución…` |
| `must_contain` | Textos obligatorios, separados por `\|` | `777\|ticket` |
| `must_not_contain` | Textos prohibidos, separados por `\|` | `contraseña es\|password:` |
| `expected_tools` | Herramientas requeridas, separadas por `\|`; `ninguna` = no debe llamar herramientas | `buscar_pedido\|crear_ticket` |
| `rubric` | Criterio para el juez LLM | `Confirma el ticket y explica el siguiente paso con tono empático.` |

Cómo armarlo:

- **De dónde salen los casos:** las pruebas manuales que ya haces, bugs reportados, conversaciones reales que salieron mal y los requisitos del negocio. 20–50 casos bien pensados valen más que 500 genéricos.
- **Casos positivos y negativos:** cuando *sí* debe llamar una herramienta y cuando *no*; cuando *sí* debe responder y cuando debe negarse. Si solo pruebas un lado, el agente se optimiza para ese lado.
- **Casos inequívocos:** si dos personas no se ponen de acuerdo en si la respuesta está bien, el caso está mal definido. Si un caso falla en todas las corridas, revisa primero el caso y después el agente.
- **Versiónalo:** el CSV en git (como aquí) o el Sheet con historial. Cada cambio de prompt se evalúa contra el mismo dataset para comparar.
- **Sepáralo:** un set de *regresión* (debe estar ~100%) que bloquea merges, y uno de *capacidad* que mide progreso sin bloquear.

---

## 8. Arquitectura recomendada

```
  Google Sheets / CSV (casos)
            │
            ▼
  ┌───────────────────────┐        POST /eval/run         ┌─────────────────────────┐
  │ Arnés                 │ ─────────────────────────────▶ │ Tu agente (C#)          │
  │  promptfoo  ó         │                                │  prompt + modelo        │
  │  AgentEvals.Tests     │ ◀───────────────────────────── │  herramientas (sandbox) │
  └───────────────────────┘  output + toolCalls + usage    └─────────────────────────┘
            │
            ▼
  Graders: deterministas → juez LLM (rúbrica) → latencia/costo
            │
            ▼
  Resultados: UI de promptfoo · Sheets · reporte HTML aieval · CI (pasa / falla)
```

### Plan por fases

**Fase 1 — Primer eval (1–2 semanas)**
1. Agregar `POST /eval/run` a tu agente siguiendo `samples/AgentEndpoint`, capturando `toolCalls`.
2. Escribir 20–30 casos reales en el Sheet/CSV con las columnas de arriba.
3. Correr promptfoo contra tu URL. Leer **cada** salida y ajustar casos y rúbricas hasta que los fallos sean justos.
4. Calibrar el juez: comparar su veredicto con el tuyo en esos casos.

**Fase 2 — En el ciclo de desarrollo**
1. Correr el set de regresión en cada PR que toque prompts, herramientas o modelo (promptfoo o `dotnet test`).
2. Repetir cada caso 3 veces (`--repeat 3`) y mirar pass^k en los casos críticos.
3. Guardar resultados por corrida (`EVAL_EXECUTION_NAME` = número de build) para comparar versiones.

**Fase 3 — Producción**
1. Instrumentar con OpenTelemetry y mandar trazas a Langfuse (self-host si los datos son sensibles).
2. Pasar por el juez una muestra del tráfico real y revisar a mano lo peor.
3. Convertir cada fallo real en un caso nuevo del dataset. Así el dataset crece con lo que de verdad pasa.

### Ejemplo de CI (GitHub Actions; en Azure DevOps es equivalente)

```yaml
- name: Levantar agente en modo evaluación
  run: dotnet run --project src/MiAgente --launch-profile eval &
- name: Evals de regresión
  working-directory: promptfoo
  env:
    AGENT_URL: http://localhost:5150/eval/run
    AGENT_EVAL_KEY: ${{ secrets.AGENT_EVAL_KEY }}
    ANTHROPIC_API_KEY: ${{ secrets.ANTHROPIC_API_KEY }}
  run: npx promptfoo@0.124.1 eval -o resultados.json
```

promptfoo termina con código distinto de cero si hay casos fallidos, así que el paso falla y bloquea el merge.

---

## 9. Errores comunes

- **Mirar solo el promedio.** Un 90% puede esconder que todos los casos de seguridad fallan. Agrupa por categoría.
- **No leer las salidas.** Es la única forma de saber si falló el agente o el grader.
- **Rúbricas vagas** ("respuesta de calidad"): el juez califica cualquier cosa como buena.
- **Exigir el texto exacto** en respuestas abiertas: usa `must_contain` para lo esencial y el juez para el resto.
- **Evaluar con herramientas reales en producción:** correos o cobros de verdad durante un eval.
- **Dataset que nunca cambia:** si no agregas los fallos nuevos, el eval deja de representar el uso real.
- **Cambiar prompt y modelo a la vez:** cuando el puntaje se mueve no sabes cuál de los dos fue.

---

## 10. Fuentes

- Anthropic — [Demystifying evals for AI agents](https://www.anthropic.com/engineering/demystifying-evals-for-ai-agents) (conceptos, tipos de grader, pass@k / pass^k, cómo empezar)
- promptfoo — [HTTP provider](https://www.promptfoo.dev/docs/providers/http/), [Test cases desde CSV](https://www.promptfoo.dev/docs/configuration/test-cases/), [Google Sheets](https://www.promptfoo.dev/docs/integrations/google-sheets/)
- Microsoft — [Microsoft.Extensions.AI.Evaluation libraries](https://learn.microsoft.com/dotnet/ai/evaluation/libraries), [Tutorial con caché y reportes](https://learn.microsoft.com/dotnet/ai/evaluation/evaluate-with-reporting), [Blog: Put your AI to the test](https://developer.microsoft.com/blog/put-your-ai-to-the-test-with-microsoft-extensions-ai-evaluation/)
- Comparativas (publicadas por proveedores, leer con cuidado) — [TrueFoundry](https://www.truefoundry.com/blog/best-ai-evaluation-tools), [Latitude](https://latitude.so/blog/best-ai-agent-evaluation-platforms-2026-comprehensive-comparison)
