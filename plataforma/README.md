# Plataforma de evaluaciones

Aplicación web para comprobar, con casos reales, si un agente o un prompt responde lo que se espera. Le pasas casos de prueba (entrada y lo esperado), se ejecuta el agente con cada uno y se compara su respuesta con lo esperado:

1. **Agente**: dónde está y cómo se le habla. Puede ser:
   - un endpoint o webhook propio, en C# u otro lenguaje;
   - la API de un modelo, para evaluar un prompt directamente;
   - un **servidor MCP** por HTTP;
   - **tu propio código** en la red del agente, para agentes en redes privadas o que no se exponen por HTTP.
2. **Casos de prueba**: un Excel, un CSV o un Google Sheet con la entrada de cada caso y lo que se espera.
3. **Evaluación**: qué se compara en cada respuesta (que contenga algo, que sea correcta según un juez IA, que use los documentos del RAG, etc.).
4. **Resultados**: qué casos pasaron, cuáles fallaron y por qué, comparación con la corrida anterior y descarga en Excel.

La interfaz está en español y pensada para que soporte, negocio o QA puedan cargar casos y ejecutar evaluaciones sin tocar código.

### Qué es: una plataforma con ejecutores

Funciona como un servidor de CI con sus runners:

| Pieza | Para qué | Dónde corre |
|---|---|---|
| **Plataforma** (este proyecto) | Guarda casos, evaluaciones, corridas e historial; califica las respuestas (comparaciones y juez IA); muestra y exporta los resultados | Un servidor de tu red |
| **Ejecutores** | Llevan los casos a agentes que la plataforma no alcanza (otra red, otra tecnología). Solo hacen conexiones salientes | Junto al agente, en su red |
| **Trazas OpenTelemetry** (opcional) | Que el agente informe tokens y pasos sin cambiar su respuesta | Dentro del agente: es instrumentación estándar |
| **CLI para CI** (pendiente) | Lanzar una evaluación desde un pipeline y fallar si baja la calidad. Será un cliente de la misma API | En el pipeline o en el equipo del desarrollador |

No se instala nada de evaluación dentro de los proyectos: la lógica, los resultados y el historial viven en la plataforma, y todos los equipos usan la misma.

### Dos formas de usarla

- **"¿Alcanza?"** (consultoría, soporte, negocio): un agente que ya existe, en una URL, y una pregunta de sí o no. Por ejemplo, ¿puede responder los tickets de nivel 1 con la documentación de la empresa? Se usa una evaluación con un solo agente y las comparaciones de RAG (*Los documentos traen la respuesta*, *Respaldada por los documentos*).
- **"¿Cuál es mejor?"** (desarrollo): las mismas pruebas contra varias configuraciones del mismo agente: modelo grande o pequeño, prompt v1 o v2, más o menos documentos, con o sin RAG. Se usan las [variantes](#comparar-variantes), que corren todo de una vez y lo muestran lado a lado.

### El ciclo de trabajo

1. Arma los casos: desde Google Sheets, Excel o CSV, o un borrador con **Generar con IA**.
2. Ejecuta el agente con cada caso y mira las comparaciones de cada uno.
3. Mejora el prompt, el modelo, las herramientas o los documentos.
4. Vuelve a ejecutar con una nota de **qué cambiaste** y compara con la corrida anterior. Para probar varias opciones a la vez, usa [variantes](#comparar-variantes).

## Arrancar

Requisito: [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
cd plataforma
dotnet run --project EvalPlatform
```

Abre http://localhost:5080. La primera vez se crea un ejemplo listo para ejecutar: un agente de helpdesk simulado (con RAG, devuelve sus pasos intermedios), 7 tickets y una evaluación, además de los ejemplos de MCP. En la pantalla de inicio, **Ejecutar el ejemplo**.

Los datos se guardan en `EvalPlatform/datos/eval.db` (SQLite). Para empezar de cero, borra esa carpeta.

## Juez IA

Las comparaciones marcadas como **(IA)** y **(RAG)** usan un modelo que califica de 1 a 5 y explica por qué. Sin juez configurado esas comparaciones se **omiten** (la interfaz lo avisa) y el resto funciona igual.

Se configura con cualquier API compatible con OpenAI, en `EvalPlatform/appsettings.json` o con variables de entorno (recomendado para la clave):

```bash
# OpenAI
export Juez__ApiKey=sk-...
export Juez__Model=gpt-5

# Azure OpenAI
export Juez__Endpoint=https://<recurso>.openai.azure.com/openai/v1/
export Juez__ApiKey=<clave>
export Juez__Model=<nombre-del-deployment>

# Anthropic (endpoint compatible con OpenAI)
export Juez__Endpoint=https://api.anthropic.com/v1/
export Juez__ApiKey=sk-ant-...
export Juez__Model=claude-sonnet-5-5

# Modelo local (Ollama, LM Studio…): la clave puede ir vacía
export Juez__Endpoint=http://localhost:11434/v1/
export Juez__Model=llama3.1
```

Usa como juez un modelo igual o más capaz que el del agente. Antes de confiar en los puntajes, revisa a mano 20–30 casos y compara con lo que dijo el juez.

### Jueces por equipo: que cada uno pague sus tokens

En un servidor compartido, el juez del servidor lo paga quien lo administra. Para que cada equipo use su cuenta:

1. En **Jueces IA** (menú), cada equipo registra su juez: endpoint, modelo, clave y, si quiere, un **límite de tokens por mes**. Puede ser cualquier API compatible con OpenAI, incluso un modelo local sin costo por token.
2. En cada evaluación elige ese juez en **Juez IA**. *Generar con IA* también deja elegirlo.

Cómo se manejan las claves y el consumo:
- **La clave se guarda cifrada** (ASP.NET Data Protection). No se vuelve a mostrar: solo sus últimos 4 caracteres. También puede ser `{{env:VARIABLE}}` para leerla de una variable del servidor.
- **La clave guardada no viaja a otro endpoint.** Si cambias el endpoint de un juez, hay que volver a escribir la clave. *Probar* también usa la clave guardada solo con el endpoint guardado. Así nadie puede apuntar la clave de otro equipo a su propio servidor.
- **Llaves de cifrado:** están en la carpeta `claves/` junto a la base, en el mismo volumen. **Respáldalas junto con `eval.db`.** Sin ellas las claves guardadas no se pueden leer y hay que volver a escribirlas.
- **Conteo de tokens:** cada corrida guarda los tokens que gastó el juez, por caso y en total, y la página *Jueces IA* muestra el uso del mes de cada juez. Si el modelo no informa el uso, se estima (unos 4 caracteres por token).
- **Al llegar al límite**, las comparaciones con juez quedan como **Error**, con el motivo, hasta el mes siguiente o hasta que se suba el límite. Las comparaciones sin juez siguen funcionando. Las llamadas que ya estaban en curso pueden pasar un poco el límite.

Para que el servidor no gaste nada:
- `Juez__PermitirEnEvaluaciones=false` obliga a cada evaluación a usar el juez de un equipo. Tampoco se usa el juez del servidor como modelo de los agentes *MCP con modelo* que no tienen modelo propio.
- `Juez__LimiteTokensMes` pone un tope mensual al juez del servidor.
- `Limites__CasosPorEjecucion` limita cuántos casos (casos × repeticiones × variantes) puede tener una ejecución.

No todas las comparaciones gastan tokens: *Contiene*, *Es igual a*, *Herramientas*, *Argumentos*, *Tokens* y *Latencia* son gratis. Una comparación con juez es una llamada al modelo por caso (de unos cientos a ~1.500 tokens).

## Conectar un agente

En **Agentes → Nuevo agente** hay plantillas para los casos comunes:

| Plantilla | Cuerpo | Respuesta | Documentos (RAG) | Herramientas |
|---|---|---|---|---|
| Agente con pasos intermedios (webhook) | `{ "mensaje": "{{input}}" }` | `output` | `intermediateSteps[*].observation` | `intermediateSteps[*].action.tool` |
| Endpoint propio (`/eval/run`) | `{ "input": "{{input}}" }` | `output` | — | `toolCalls[*].name` |
| Prompt directo (API OpenAI) | `messages` con tu prompt de sistema | `choices[0].message.content` | — | `choices[0].message.tool_calls[*].function.name` |
| Prompt directo (API Anthropic) | `system` + `messages` | `content[*].text` | — | `content[*].name` |

Ruta de argumentos de herramientas en cada plantilla: `intermediateSteps[*].action.toolInput` (pasos intermedios), `toolCalls[*].arguments` (endpoint propio), `choices[0].message.tool_calls[*].function.arguments` (OpenAI) y `content[*].input` (Anthropic).

Ruta de tokens en cada plantilla: `usage.inputTokens, usage.outputTokens` (endpoint propio), `usage.total_tokens` (OpenAI) y `usage.input_tokens, usage.output_tokens` (Anthropic); varias rutas separadas por coma se suman. Un agente con pasos intermedios no suele devolver tokens; agrégalos a su respuesta si quieres medirlos.

- **Marcadores:** `{{input}}` es la columna de entrada del caso; `{{cualquier_columna}}` trae otra columna; `{{env:NOMBRE}}` lee una variable de entorno del servidor, para no guardar claves en la base de datos.
- **Rutas:** `a.b` entra en propiedades, `lista[0]` toma un elemento y `lista[*]` todos. Si un valor es texto con JSON adentro (muchos orquestadores de agentes lo hacen), se interpreta solo. Si la respuesta es una lista, las propiedades se aplican a cada elemento.
- **Probar:** antes de guardar, escribe una entrada de ejemplo y pulsa **Probar**. Se ve lo que se envió, la respuesta completa y qué se extrajo con cada ruta.

### Agente con pasos intermedios

Es el formato de muchos frameworks de agentes con herramientas, por ejemplo `intermediateSteps` en LangChain: `{ "output": ..., "intermediateSteps": [{ "action": { "tool", "toolInput" }, "observation" }] }`. Para evaluarlo bien:

1. La ruta debe **esperar la respuesta del agente** y devolverla en la misma llamada, con los pasos intermedios.
2. Si el agente debe decidir entre responder o escalar, pídele una salida estructurada con un campo `puede_responder` (booleano). En la evaluación, compáralo con **Es igual a** usando *Comparar otro campo de la respuesta* = `output.puede_responder`.
3. Las acciones reales (responder al cliente, crear tickets, enviar correos) no deberían ejecutarse durante una evaluación. La plantilla manda el encabezado `X-Eval: 1` para que el agente las salte. Otra opción es una ruta aparte solo para evaluaciones.

Más detalle del caso helpdesk con RAG en [`../docs/evaluar-rag-helpdesk.md`](../docs/evaluar-rag-helpdesk.md).

## Servidores MCP

Hay dos tipos de agente para evaluar un servidor MCP por HTTP (Streamable HTTP, o el transporte SSE anterior). Los servidores stdio no están soportados. En ambos, **Ver herramientas del servidor** muestra lo que expone: nombres, descripciones y parámetros.

### 1. Llamar herramientas: ¿el servidor responde bien?

La plataforma se conecta al MCP y, por cada caso, llama una herramienta (`tools/call`) con sus argumentos. Lo que devuelve la herramienta es la respuesta que se evalúa. Es lo mismo que hace el [proveedor MCP de promptfoo](https://www.promptfoo.dev/docs/providers/mcp/).

- **Herramienta:** un nombre fijo, o `{{herramienta}}` para tomarlo de esa columna.
- **Argumentos (JSON):** `{{argumentos}}` para tomar el JSON de esa columna, o armarlo con marcadores: `{ "cliente": "{{cliente}}" }`. Para números sin comillas: `{ "limite": {{limite}} }`.

| caso | herramienta | argumentos | debe_contener | es_error |
|---|---|---|---|---|
| Saldo de un cliente existente | consultar_cartera | `{"cliente": "123"}` | 1.200.000 | no |
| Cliente que no existe | consultar_cartera | `{"cliente": "999"}` | no existe | si |
| Ticket sin asunto debe fallar | crear_ticket | `{"asunto": ""}` | obligatorio | si |

Comparaciones útiles:
- **Contiene** sobre el texto del resultado.
- **Es igual a** con *Comparar otro campo de la respuesta* = `isError`, para revisar que falle cuando debe y no falle cuando no debe. La sugerencia automática lo arma si la columna se llama `es_error`.
- **Es igual a** o **Contiene** sobre `structuredContent.campo`, si la herramienta devuelve resultado estructurado.
- **Responde a tiempo.**

Una herramienta que no existe o un error de protocolo cuentan como respuesta con `isError = true`, no como fallo de conexión. Así se pueden probar los casos de error.

### 2. Con un modelo: ¿el modelo elige bien las herramientas?

Un modelo recibe la entrada del caso y las herramientas del MCP, y decide cuáles llamar. Así se ve si los nombres, descripciones y parámetros de tus herramientas le alcanzan para elegir bien. La plataforma guarda cada llamada con sus argumentos y su resultado.

- **Modelo:** cualquier API compatible con OpenAI (modelo, URL y clave con `{{env:...}}`). Si lo dejas vacío, usa el mismo modelo del juez IA.
- **Prompt de sistema** y **herramientas que se ofrecen** (opcional, para probar con un subconjunto).

| input | herramientas_esperadas | argumentos_esperados | respuesta_esperada |
|---|---|---|---|
| ¿Cuánto debe el cliente 123? | consultar_cartera | `consultar_cartera {"cliente": "123"}` | Debe $1.200.000… |
| ¿Cuánto debe Constructora Andina? | buscar_cliente\|consultar_cartera | `consultar_cartera {"cliente": "123"}` | … |
| Hola, ¿qué puedes hacer? | ninguna | | Explica qué puede hacer sin usar herramientas |

Comparaciones útiles:
- **Usó las herramientas** (`ninguna` = no debía usar ninguna) y **Usó las herramientas en orden**.
- **Llamó con estos argumentos.**
- **Correcta frente a lo esperado (IA)** sobre la respuesta final.
- **Respaldada por los documentos (RAG):** los resultados de las herramientas cuentan como "documentos", así que detecta cuando el modelo inventa datos que las herramientas no le dieron.

El ciclo: corre la evaluación, mira qué casos eligen mal, mejora la descripción de esa herramienta en tu MCP, vuelve a correr con la nota "descripción de buscar_cliente v2" y compara.

### Llamó con estos argumentos

Esta comparación también sirve para agentes HTTP (pasos intermedios, endpoint propio, OpenAI o Anthropic) configurando *Dónde vienen sus argumentos* (las plantillas ya lo traen), y para tu propio código, que manda `arguments` en cada llamada de `toolCalls`. Formato de lo esperado, una llamada por línea:

```
consultar_cartera {"cliente": "123"}       ← esa herramienta, con al menos esos argumentos
{"cliente": "123"}                         ← cualquier herramienta con ese argumento
buscar_cliente                             ← solo que la haya llamado
```

Solo se revisan los argumentos que escribas; los demás pueden tener cualquier valor. Los valores se comparan sin mayúsculas ni tildes, `"123"` vale lo mismo que `123`, y en objetos anidados basta con un subconjunto.

### Generar casos desde el MCP

En **Generar con IA**, la lista *Traer las herramientas de un servidor MCP* llena el campo de herramientas con las de tu servidor. El borrador incluye las columnas `herramientas_esperadas` y `argumentos_esperados`.

### Ejemplos incluidos

La plataforma trae un servidor MCP de demostración en `/demo/mcp`, con las herramientas `consultar_cartera`, `buscar_cliente` y `crear_ticket`, y dos evaluaciones:
- **Demo · MCP: ¿las herramientas responden bien?** Llamada directa. Encuentra un error real que dejé a propósito: `buscar_cliente` distingue mayúsculas.
- **Demo · MCP: ¿el modelo elige bien las herramientas?** Modelo + MCP. Necesita el juez IA configurado, porque usa ese mismo modelo.

## Tokens cuando el servicio no los devuelve

La mayoría de servicios devuelven solo el resultado. Para comparar eficiencia (tokens, llamadas al modelo, tiempo por paso) sin cambiar su respuesta, la plataforma lee las **trazas OpenTelemetry** del agente:

1. En cada caso, la plataforma (o el ejecutor remoto) manda el encabezado estándar `traceparent` con una traza nueva.
2. El agente, instrumentado con OpenTelemetry, continúa esa traza. ASP.NET Core, Spring, FastAPI, Express y casi todos los frameworks lo hacen solos al recibir el encabezado.
3. Sus librerías de IA registran cada llamada al modelo con sus tokens, con los atributos GenAI estándar `gen_ai.usage.input_tokens` y `gen_ai.usage.output_tokens`. Lo hacen Microsoft.Extensions.AI, Semantic Kernel, LangChain/OpenLLMetry, OpenAI Agents SDK y otras.
4. El agente exporta sus trazas a la plataforma (`POST /v1/traces`, OTLP en protobuf o JSON). La plataforma suma los tokens de esa traza y los asocia al caso.

En el agente de la plataforma, activa **Medir tokens y pasos con las trazas del agente**. En el servicio del agente:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=https://evaluaciones.miempresa     # la plataforma
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_BSP_SCHEDULE_DELAY=500                                    # enviar rápido en ambientes de evaluación
OTEL_EXPORTER_OTLP_HEADERS=X-Trazas-Token=...                  # si configuraste Trazas__Token
```

C# con Microsoft.Extensions.AI (paquetes `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol` y `OpenTelemetry.Instrumentation.AspNetCore`):

```csharp
builder.Services.AddOpenTelemetry().WithTracing(t => t
    .AddSource("mi-agente")
    .AddAspNetCoreInstrumentation()            // continúa el traceparent que manda la plataforma
    .AddOtlpExporter());

IChatClient chat = cliente.AsIChatClient()
    .AsBuilder()
    .UseOpenTelemetry(sourceName: "mi-agente") // registra cada llamada al modelo con sus tokens
    .Build();
```

Qué obtienes por caso:
- tokens de entrada y salida;
- número de llamadas al modelo y qué modelos;
- pasos con su duración, incluidas las herramientas si el framework las registra.

Por corrida: tokens promedio y llamadas al modelo promedio. **Consume pocos tokens** funciona igual que con tokens en la respuesta.

Detalles:
- **No se cuenta dos veces:** si una cadena reporta tokens que también reportan sus llamadas internas, solo se cuentan las internas.
- **Solo se guardan los spans de las trazas que inició la plataforma.** Si el agente exporta todo su tráfico, lo demás se descarta.
- **La plataforma espera las trazas** hasta el tiempo configurado (15 s por defecto), porque los exportadores envían por lotes. Termina antes si ya llegaron los tokens.
- **Con ejecutores remotos:** cada caso trae su `traceId` y el ejecutor manda el `traceparent`. El agente exporta a la plataforma (una conexión saliente) o, si no la alcanza, a su ejecutor (ver *Trazas desde una red sin salida*).

Si el servicio no se puede instrumentar (un producto de terceros, por ejemplo):
- **Pasarela de modelos:** apunta el agente a un proxy compatible con OpenAI que registre el consumo, como LiteLLM o Azure API Management, con una clave usada solo por el ambiente de evaluación. Con concurrencia 1 se puede atribuir el consumo a cada caso por tiempo.
- **Métricas del proveedor** por clave o proyecto dedicado (OpenAI, Azure OpenAI, Anthropic): da el total por corrida, no por caso.
- **Estimar** tokens a partir del texto de entrada y salida no sirve para agentes: no ve el prompt de sistema, los documentos del RAG, las llamadas a herramientas ni las varias vueltas al modelo.

## Agentes en redes privadas u otras tecnologías

Muchas veces el agente no es alcanzable desde la plataforma. Puede vivir en un microservicio dentro de una red privada, o no hablar HTTP: gRPC, una cola de mensajes, una librería dentro de otro servicio, un proceso por consola. Para eso se separa quién **ejecuta** al agente de quién **califica**:

```
  Red de la plataforma                         Red privada del agente
 ┌────────────────────────┐                 ┌───────────────────────────────────┐
 │ Plataforma             │  1. pide casos  │ Ejecutor                          │
 │  casos, comparaciones, │ ◀────────────── │  (genérico o tu propio código)    │
 │  juez IA, resultados   │  3. respuestas  │          │ 2. llama al agente     │
 │                        │ ◀────────────── │          ▼    (HTTP, MCP, gRPC,   │
 └────────────────────────┘                 │   Agente / microservicio  colas…) │
                                            └───────────────────────────────────┘
```

- El ejecutor **solo hace conexiones salientes** (HTTPS) hacia la plataforma: no hay que abrir puertos en la red del agente.
- Los **secretos se quedan en esa red**: los `{{env:NOMBRE}}` de los agentes se leen en el ejecutor, no en la plataforma.
- La plataforma sigue haciendo todo lo demás: guarda los casos, califica (incluido el juez IA) y muestra los resultados.
- Los casos esperan en la base hasta que un ejecutor los tome. Si un ejecutor se cae a mitad de un caso, el caso vuelve a repartirse cuando vence su tiempo máximo más 60 segundos. Puede haber varios ejecutores del mismo grupo trabajando en paralelo.

### 1. Activar la API de ejecutores

En la plataforma, define un token (los ejecutores lo envían en el encabezado `X-Ejecutor-Token`):

```bash
export Ejecutores__Token="un-valor-largo-y-secreto"
```

Sin token configurado, la API de ejecutores está apagada.

### 2a. Agentes HTTP o MCP en otra red: el ejecutor genérico

En el agente, en **¿Desde dónde se llama al agente?**, elige *Desde un ejecutor dentro de la red del agente* y escribe un **grupo** (por ejemplo `red-finanzas`). En una máquina o contenedor de esa red, corre el mismo programa de la plataforma en modo ejecutor:

```bash
export EVAL_EJECUTOR_TOKEN="un-valor-largo-y-secreto"
dotnet EvalPlatform.dll ejecutor --plataforma https://evaluaciones.miempresa --grupo red-finanzas [--nombre srv-01] [--concurrencia 2]

# o con Docker (la misma imagen de la plataforma):
docker run -d -e EVAL_EJECUTOR_TOKEN=... evaluaciones ejecutor --plataforma https://evaluaciones.miempresa --grupo red-finanzas
```

Funciona con los tipos HTTP, *Servidor MCP: llamar herramientas* y *Servidor MCP con un modelo*.

### 2b. Cualquier tecnología: tu propio código

Crea el agente con el tipo **Tu propio código** y un grupo. Tu código, en la tecnología y la red del agente, hace un ciclo con dos llamadas:

```http
POST /api/ejecutores/tomar
X-Ejecutor-Token: ...
Content-Type: application/json

{ "group": "red-finanzas", "worker": "servicio-cobranza-01", "max": 1, "waitSeconds": 20 }
```

Devuelve una lista de casos (vacía si no hay; espera hasta `waitSeconds` antes de responder):

```json
[{ "taskId": 15, "runId": 3, "caseNumber": 1, "attempt": 1,
   "input": "¿Cuánto debe el cliente 123?", "row": { "input": "...", "respuesta_esperada": "..." }, "agent": { ... },
   "traceId": null, "variant": "Modelo grande", "parameters": { "modelo": "gpt-5" } }]
```

`variant` y `parameters` vienen cuando el caso es de una [variante](#comparar-variantes): usa los parámetros para configurar tu agente en ese caso. Si no, `variant` es `null`.

Llama a tu agente con `input` (o con cualquier columna de `row`) y devuelve lo que respondió:

```http
POST /api/ejecutores/resultado
X-Ejecutor-Token: ...
Content-Type: application/json

{ "taskId": 15,
  "output": "El cliente 123 debe $1.200.000.",
  "toolCalls": [{ "name": "consultar_cartera", "arguments": { "cliente": "123" }, "result": "..." }],
  "context": ["documento recuperado 1"],
  "tokens": 180,
  "latencyMs": 950,
  "raw": "{ ...respuesta completa en JSON, para 'Comparar otro campo de la respuesta'... }" }
```

Solo `taskId` y `output` son obligatorios. Si el agente falló, manda `"error": "mensaje"` y el caso queda como *Error*. La respuesta trae cómo quedó el caso (`Passed`, `Failed` o `Error`) y las comparaciones.

Hay un ejecutor de ejemplo en **Python sin dependencias** en [`ejecutores/python/ejecutor.py`](ejecutores/python/ejecutor.py): solo cambias la función `ejecutar_agente()` por la llamada a tu agente. En cualquier otro lenguaje (Java, Go, Node, C#…) son las mismas dos llamadas HTTP.

### Cuándo usar cada opción

| Situación | Opción |
|---|---|
| El agente expone HTTP o MCP y la plataforma lo alcanza | Agente normal: lo llama la plataforma |
| El agente expone HTTP o MCP pero solo dentro de su red | Ejecutor genérico en esa red |
| El agente no expone HTTP (gRPC, colas, librería, consola) o necesitas preparar datos antes de cada caso | Tu propio código |
| Una sola red para todo | También puedes desplegar la plataforma dentro de esa red (ver *Producción*) |
| La red no tiene ningún camino hacia la plataforma | Plataforma dentro de esa red y llevar los resultados con *Exportar / Importar* |

### Qué necesita la red

No hace falta internet: hace falta un camino desde la red del agente hacia la URL de la plataforma. Las conexiones siempre salen del ejecutor; no se abre nada entrante en la red del agente.
- **Plataforma en un servidor interno:** los ejecutores llegan por la red corporativa.
- **Salida restringida:** basta una regla que permita HTTPS saliente hacia el dominio de la plataforma, o el proxy corporativo. Los ejecutores .NET y Python respetan `HTTPS_PROXY`.
- **Red aislada:** la plataforma va adentro. La imagen se lleva con `docker save evaluaciones | gzip > evaluaciones.tar.gz` y `docker load < evaluaciones.tar.gz`, el juez es un modelo de esa red, y los resultados salen con *Exportar*.

### Trazas desde una red sin salida

Si el agente no alcanza la plataforma pero sí su ejecutor, el ejecutor recibe sus trazas y las reenvía por la conexión que ya tiene:

```bash
EvalPlatform ejecutor --plataforma https://... --grupo red-finanzas --trazas 4318   # .NET
EVAL_PUERTO_TRAZAS=4318 python ejecutor.py                                       # Python
```

En el agente: `OTEL_EXPORTER_OTLP_ENDPOINT=http://<máquina-del-ejecutor>:4318`. El ejecutor reenvía a `/v1/traces` con su token de ejecutor, que la plataforma acepta aunque `Trazas__Token` esté definido. Si la plataforma no responde, el ejecutor devuelve 503 y el exportador de OpenTelemetry reintenta.

Si el agente ni siquiera alcanza el ejecutor, que devuelva los tokens en su respuesta (*Tokens consumidos*), o pon un OpenTelemetry Collector en su red que reenvíe al ejecutor.

## Llevar resultados entre plataformas

Para redes aisladas o para juntar resultados en una plataforma central:
1. En la plataforma de origen, **Exportar** en una corrida o en una comparativa. Descarga un `.json` con la evaluación, los casos, las respuestas, las comparaciones, los tokens y las trazas resumidas.
2. En la plataforma de destino, ve a **Resultados → Importar resultados de otra plataforma**. Puedes agregar las corridas a una evaluación que ya existe o crear una nueva con sus casos.

Lo importado queda marcado con su origen (`Plataforma__Nombre` de la plataforma que exportó). Se ve y se compara igual que lo propio; para volver a ejecutarlo, elige un agente al editar la evaluación.

El archivo trae las respuestas del agente y los datos de los casos: trátalo con el mismo cuidado que la base.

## Casos de prueba

Una fila por caso. La primera fila tiene los nombres de las columnas, que son libres. Hay una plantilla en **Casos de prueba → Descargar plantilla**.

- **Excel (.xlsx):** se lee la primera hoja.
- **CSV:** acepta coma, punto y coma o tabulación, y UTF-8 o la codificación de Excel en español.
- **Google Sheets:** compártelo como *Cualquier persona con el enlace (lector)* y pega el enlace de la pestaña. Con **Volver a cargar desde el enlace** se traen los cambios de la hoja.

### Generar casos con IA

En **Casos de prueba → Agregar casos → Generar con IA** describes qué hace el agente, qué herramientas tiene y, si quieres, pegas parte de la base de conocimiento. El modelo juez arma un borrador con preguntas frecuentes, casos límite y casos que el agente debe escalar o rechazar. Las columnas son `input`, `respuesta_esperada`, `herramientas_esperadas`, `debe_contener`, `categoria` y `tipo`.

Es un punto de partida. Descárgalo con **Descargar Excel**, corrige lo que no esté bien (sobre todo las respuestas esperadas) y vuelve a subirlo o pásalo a un Google Sheet. Lo ideal es mezclar estos casos con casos reales.

### Sugerencias de comparación

Al crear una evaluación, la plataforma sugiere comparaciones según los nombres de las columnas: `respuesta_esperada`/`referencia` → correcta (IA), `debe_contener` → contiene, `no_debe_contener` → no contiene, `criterio` → cumple un criterio (IA), `herramientas` → usó las herramientas.

Si la celda de lo esperado está vacía en un caso, esa comparación se **omite** en ese caso. Así, en una misma tabla, unos casos pueden tener respuesta de referencia y otros no.

## Comparaciones disponibles

| Comparación | Qué revisa | Juez IA |
|---|---|---|
| Es igual a | Coincide con lo esperado, sin mayúsculas, tildes ni espacios; `sí`/`true`/`1` son lo mismo | No |
| Contiene / No contiene | Todos los textos (separados por `\|`) aparecen, o ninguno | No |
| Se parece a | Similitud letra por letra de 0 a 1 (distancia de edición) | No |
| Cumple un patrón | Expresión regular | No |
| Usó las herramientas | Llamó las herramientas esperadas, sin importar el orden; `ninguna` = no debía llamar herramientas | No |
| Usó las herramientas en orden | Las herramientas esperadas aparecen en ese orden (puede haber otras en medio) | No |
| Llamó con estos argumentos | La herramienta se llamó con esos argumentos (ver [Servidores MCP](#llamó-con-estos-argumentos)) | No |
| Consume pocos tokens | Tokens máximos por caso (requiere la ruta de tokens en el agente) | No |
| Responde a tiempo | Latencia máxima en milisegundos | No |
| Correcta frente a lo esperado | Mismo significado que la respuesta de referencia, de 1 a 5, con explicación | Sí |
| Responde lo que se preguntó | Relevancia frente a la entrada, de 1 a 5 | Sí |
| Cumple un criterio | Un criterio escrito en palabras, por caso o igual para todos | Sí |
| Respaldada por los documentos (RAG) | Lo que respondió está en los documentos recuperados (detecta respuestas inventadas) | Sí |
| Los documentos traen la respuesta (RAG) | Los documentos recuperados contienen la respuesta esperada (detecta faltantes en la base de conocimiento) | Sí |

Cada comparación puede ser **informativa** (desmarcando *Cuenta para aprobar el caso*): se mide y se muestra, pero no hace fallar el caso.

Un caso queda:
- **Aprobado** si todas las comparaciones que cuentan pasaron.
- **Fallido** si alguna falló.
- **Error** si no se pudo evaluar: el agente no respondió, devolvió un error HTTP o el juez falló. Un error es un problema de conexión o configuración, no del agente.

### Mejorar y comparar

Al ejecutar puedes escribir **qué cambiaste desde la última corrida** (prompt, modelo, temperatura, documentos nuevos en el RAG). Queda en el historial junto al porcentaje de aprobados. Cada corrida se compara con la anterior: puntos de diferencia, tokens promedio y la lista de casos que pasaron de fallido a aprobado o al revés. Cambia una cosa a la vez; si cambias prompt y modelo juntos, no sabrás cuál de los dos movió el resultado.

Con **Repeticiones por caso** mayor que 1, cada caso se corre varias veces y el resumen muestra cuántos pasaron en todos los intentos. Los LLM no responden siempre igual, y un caso que pasa 2 de 3 veces es un riesgo en producción.

## Comparar variantes

Una **variante** es una configuración del agente que quieres comparar con otras sobre los mismos casos. Se definen en la evaluación (*Editar → Variantes a comparar*), y *⚖ Comparar las variantes* corre una corrida por variante, una tras otra. Después muestra:

- un resumen por variante: % de aprobados, fallidos, errores, tiempo, tokens y llamadas al modelo, con la **mejor marcada** y una conclusión en palabras ("aprueba 86% frente a 71%; además gasta 30% menos tokens");
- el % que pasó cada comparación en cada variante;
- los casos uno al lado del otro, con un filtro **"solo donde cambia el resultado"** y las respuestas de cada variante juntas;
- descarga en Excel con una columna por variante.

Cada variante es el agente de la evaluación (u otro agente) con **parámetros**. Los parámetros llegan a la configuración del agente como `{{nombre}}`, igual que las columnas del caso:

```json
{
  "model": "{{modelo}}",
  "temperature": "{{json:temperatura}}",
  "messages": [
    { "role": "system", "content": "{{prompt_sistema}}" },
    { "role": "user", "content": "{{input}}" }
  ]
}
```

| Variante | modelo | temperatura | prompt_sistema |
|---|---|---|---|
| A. Actual | gpt-5-mini | 0.7 | Eres un asistente de soporte… |
| B. Modelo grande | gpt-5 | 0.7 | Eres un asistente de soporte… |
| C. Prompt v2 | gpt-5-mini | 0.2 | Eres un agente de soporte N1. Si la respuesta no está en los documentos, escala… |

- `{{json:nombre}}` mete el valor como JSON (número, booleano u objeto) en vez de texto. Así `"0.2"` llega como `0.2`; si el parámetro está vacío, llega `null`.
- **Tu propio endpoint** decide qué hace con cada parámetro: por ejemplo `{ "input": "{{input}}", "modelo": "{{modelo}}", "top_k": "{{json:top_k}}", "usar_rag": "{{json:rag}}" }`. Así se comparan cosas que la plataforma no conoce, como más contexto, otro índice vectorial o con y sin RAG.
- **MCP con un modelo:** los parámetros se pueden usar en el modelo, el endpoint del modelo, el prompt de sistema y las herramientas permitidas. Un parámetro `temperatura` reemplaza la temperatura del agente.
- **Ejecutores:** cada caso trae `variant` y `parameters`. El ejecutor genérico los aplica a la configuración del agente. En tu propio código llegan como argumento (en `ejecutor.py`, `ejecutar_agente(..., parametros)`).
- **Otro agente** en una variante sirve para comparar proveedores distintos, por ejemplo la plantilla de OpenAI contra la de Anthropic.
- Si un parámetro se llama igual que una columna del caso, gana el parámetro.

La plataforma rechaza las variantes que no cambiarían nada: un parámetro que el agente no usa (salvo en agentes de código propio), dos variantes iguales o dos con el mismo nombre. Así una comparativa nunca sale "igual" por un error de configuración.

Para un **antes y después** sin variantes, marca dos o más corridas en el historial de la evaluación y usa *Comparar seleccionadas*.

La demo incluye dos variantes de la base de conocimiento, «Base actual» y «Base + manual de usuarios». Compararlas muestra que el ticket de crear usuarios solo se responde con el manual nuevo.

## Configuración

| Clave | Por defecto | Para qué |
|---|---|---|
| `ConnectionStrings:Eval` | `Data Source=datos/eval.db` | Ruta de la base SQLite |
| `Juez:Endpoint`, `Juez:ApiKey`, `Juez:Model` | vacío | Juez del servidor (ver arriba) |
| `Juez:LimiteTokensMes` | vacío | Tokens por mes del juez del servidor. Vacío = sin límite |
| `Juez:PermitirEnEvaluaciones` | `true` | `false` = cada evaluación usa el juez de un equipo (*Jueces IA*) |
| `Limites:CasosPorEjecucion` | `0` | Máximo de casos × repeticiones × variantes por ejecución. 0 = sin límite |
| `Plataforma:Nombre` | nombre de la máquina | Cómo se identifica esta plataforma en los archivos exportados |
| `Corridas:MaxConcurrency` | `3` | Casos en paralelo por corrida (cuando llama la plataforma). Bájalo si el agente se satura |
| `Ejecutores:Token` | vacío | Token que deben enviar los ejecutores remotos. Vacío = API de ejecutores apagada |
| `Trazas:Habilitado` | `true` | Receptor OTLP de trazas en `/v1/traces` |
| `Trazas:Token` | vacío | Si se define, los agentes deben mandarlo en `X-Trazas-Token` (o `Authorization: Bearer`) |
| `Demo:Habilitado` | `true` | Expone el agente simulado en `/demo/helpdesk` y el servidor MCP de demostración en `/demo/mcp` |
| `Demo:CrearEjemplo` | `true` | Crea el ejemplo si la base está vacía |

En variables de entorno, el `:` se escribe `__` (por ejemplo, `Corridas__MaxConcurrency=1`).

## Seguridad

- **No tiene inicio de sesión.** Úsala en una red interna, o detrás de un proxy con autenticación (por ejemplo Azure App Service con Entra ID o un reverse proxy con SSO).
- La plataforma hace llamadas HTTP a las URLs que se configuren en los agentes. Quien pueda crear agentes puede hacer que el servidor llame a cualquier dirección a la que tenga acceso.
- Las claves van en variables de entorno (`{{env:NOMBRE}}`), no en los encabezados guardados. Con ejecutores remotos se leen en el ejecutor.
- La API de ejecutores usa un token compartido (`Ejecutores__Token`). Los ejecutores reciben la configuración del agente (URL, encabezados con sus `{{env:…}}`, cuerpo), no los valores de los secretos.
- Las claves de los jueces de equipo se guardan cifradas. Las llaves de cifrado (`claves/`) quedan junto a la base, sin cifrar en disco: protege ese volumen como protegerías la base.
- Sin inicio de sesión, cualquiera que entre a la plataforma puede usar los jueces de cualquier equipo. Los límites mensuales acotan el daño; el control de acceso lo da el proxy de delante.
- Para producción: `Demo__Habilitado=false`.

## Producción

### Requisitos

| | |
|---|---|
| **Máquina** | Linux, Windows o macOS (x64 o ARM64). 1 vCPU y 1 GB de RAM alcanzan para un equipo: el trabajo pesado lo hacen el agente y el juez |
| **Runtime** | Ninguno con Docker. Sin Docker: ASP.NET Core 10, o publicar *self-contained* |
| **Disco** | Persistente y **local** (no carpetas compartidas SMB/NFS: SQLite se corrompe en ellas). Cada caso guarda la respuesta del agente (hasta 200 KB); las corridas viejas se borran desde la interfaz |
| **Una sola instancia** | La cola de corridas, el reparto a ejecutores y la espera de trazas están en memoria. No pongas dos réplicas sobre la misma base |
| **Entrada** | Usuarios (interfaz y API); ejecutores a `/api/ejecutores/*` (long polling de 20 s: el proxy necesita un timeout de lectura mayor); agentes a `/v1/traces` |
| **Salida** | URLs de los agentes y servidores MCP, API del juez, Google Sheets. Respeta `HTTPS_PROXY` |
| **Acceso** | No tiene inicio de sesión: ponle delante un proxy con HTTPS y SSO, o úsala en una red interna. `/api/ejecutores/*` y `/v1/traces` se protegen con sus propios tokens |

### Con Docker Compose

[`docker-compose.yml`](docker-compose.yml) y [`.env.example`](.env.example) tienen todo:

```bash
cd plataforma
cp .env.example .env          # completa los valores; no lo subas al repositorio
docker compose up -d --build  # o, con la imagen en un registro: EVAL_IMAGEN=miregistro/evaluaciones:1.4.0
```

Los datos quedan en el volumen `eval-datos` (`/datos`): la base `eval.db` y las llaves de cifrado `claves/`. Al arrancar, la aplicación agrega sola las tablas y columnas nuevas.

**Actualizar** es cambiar la imagen:

```bash
docker compose pull && docker compose up -d
```

- **Actualiza sin corridas en curso.** Las que ejecuta la plataforma quedan como falladas al reiniciar; las de ejecutores remotos siguen solas, porque los ejecutores reintentan.
- **Nunca** uses `docker compose down -v` ni borres el volumen: ahí está todo.
- **Usa etiquetas de versión**, no solo `latest`, para poder volver atrás. Los cambios de esquema solo agregan columnas con valor por defecto, así que una versión anterior normalmente sigue funcionando con la base nueva; para estar seguro, respalda antes.

**Respaldar** (la imagen no trae `sqlite3`; con el contenedor detenido la copia es consistente):

```bash
docker compose stop
docker run --rm -v plataforma_eval-datos:/datos -v "$PWD":/respaldo alpine tar czf /respaldo/eval-$(date +%F).tgz -C /datos .
docker compose start
```

El nombre del volumen lleva el prefijo del proyecto (`docker volume ls`). Si montas una carpeta del servidor en vez de un volumen, dale permisos al usuario del contenedor: `sudo chown -R 1654 /srv/evaluaciones`.

El mismo `docker-compose.yml` trae un servicio `ejecutor` (perfil `ejecutor`) para levantar un ejecutor con relay de trazas en otra red: `docker compose --profile ejecutor up -d ejecutor`.

### Sin Docker

```bash
dotnet publish EvalPlatform -c Release -r linux-x64 --self-contained -o /opt/evaluaciones
```

Como servicio de systemd:

```ini
[Service]
WorkingDirectory=/opt/evaluaciones
ExecStart=/opt/evaluaciones/EvalPlatform
User=evaluaciones
Restart=always
Environment=ASPNETCORE_URLS=http://127.0.0.1:5080
Environment=ConnectionStrings__Eval=Data Source=/var/lib/evaluaciones/eval.db
EnvironmentFile=/etc/evaluaciones.env
```

`WorkingDirectory` debe ser la carpeta publicada: ahí está la interfaz (`wwwroot`). La ruta relativa de la base se toma desde la carpeta de la aplicación, pero en producción conviene una ruta absoluta en un disco de datos. En Windows funciona detrás de IIS con el *ASP.NET Core Hosting Bundle*.

## Desarrollo

```bash
dotnet test        # 57 pruebas: rutas JSON, plantillas, importación, comparaciones, juez, generación de casos,
                   # migración de la base, MCP (directo y con modelo), ejecutores remotos, trazas OTLP
                   # y flujo completo por API
```

Estructura:

| Ruta | Qué hay |
|---|---|
| `EvalPlatform/Domain/` | Modelo (agente, dataset, evaluación, corrida, resultados) y catálogo de comparaciones |
| `EvalPlatform/Services/` | Llamada al agente, rutas JSON, importación de casos, comparaciones, juez, cola de corridas, Excel, demo |
| `EvalPlatform/Api/` | API REST que usa la interfaz (`/api/...`) y API de ejecutores (`/api/ejecutores/...`) |
| `EvalPlatform/wwwroot/` | Interfaz web (HTML + JavaScript sin dependencias) |
| `EvalPlatform.Tests/` | Pruebas unitarias y de integración |
| `ejecutores/python/` | Ejecutor de ejemplo para tu propio código |
| `Dockerfile` | Imagen de la plataforma (y del ejecutor) |

Para agregar una comparación nueva: súmala al catálogo en `Domain/CheckTypes.cs` e impleméntala en `Services/CheckRunner.cs`. La interfaz la muestra sola.
