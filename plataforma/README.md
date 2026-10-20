# Plataforma de evaluaciones

Aplicación web para comprobar, con casos reales, si un agente o un prompt responde lo que se espera. Funciona como las evaluaciones de n8n, pero con cualquier agente que responda por HTTP:

1. **Agente**: dónde está (URL) y cómo se le habla. Sirve un webhook de n8n, un endpoint propio en C# u otro lenguaje, la API de un modelo (para evaluar un prompt directamente) o un **servidor MCP** por HTTP.
2. **Casos de prueba**: un Excel, un CSV o un Google Sheet con la entrada de cada caso y lo que se espera.
3. **Evaluación**: qué se compara en cada respuesta (que contenga algo, que sea correcta según un juez IA, que use los documentos del RAG, etc.).
4. **Resultados**: qué casos pasaron, cuáles fallaron y por qué, comparación con la corrida anterior y descarga en Excel.

La interfaz está en español y pensada para que soporte, negocio o QA puedan cargar casos y ejecutar evaluaciones sin tocar código.

### Del flujo de n8n Evaluations a la plataforma

El ciclo es el mismo que se ve en tutoriales como [Evaluate N8N AI Agents & RAG like a PRO](https://youtu.be/tn0C1VlkkQ4): dataset en Sheets → correr el agente por cada fila → métricas → mejorar el prompt → volver a correr y comparar.

| En n8n | En la plataforma |
|---|---|
| Evaluation Trigger leyendo un Google Sheet | **Casos de prueba** desde Google Sheets, Excel o CSV |
| Dataset generado con Claude/ChatGPT | **Casos de prueba → Generar con IA** (borrador para revisar) |
| AI Agent con *Return Intermediate Steps* | **Agente** con plantilla de n8n: respuesta, documentos y herramientas |
| Check If Evaluating | Encabezado `X-Eval: 1` que manda la plantilla, para que un nodo IF salte las acciones reales |
| Set Outputs (escribe en el Sheet) | Detalle por caso en pantalla y **Descargar Excel** |
| Métrica Correctness | Correcta frente a lo esperado (IA), con explicación |
| Métrica Helpfulness | Responde lo que se preguntó (IA) |
| Métrica String similarity | Se parece a (texto) |
| Métrica Categorization | Es igual a |
| Métrica Tools used | Usó las herramientas / Usó las herramientas en orden |
| Tokens en la pestaña Evaluations | Consume pocos tokens y tokens promedio por corrida |
| Comparar la corrida antes y después del cambio de prompt | Nota **Qué cambiaste**, historial con gráfica y "Cambios frente a la corrida anterior" |
| — | Comparaciones de RAG: *respaldada por los documentos* y *los documentos traen la respuesta* |

Diferencias: no hace falta n8n (sirve para agentes en C# u otro lenguaje, y para prompts sueltos), y no hay límite de workflows. En n8n las evaluaciones con métricas son de los planes Pro y Enterprise; en Community y Starter registrados se pueden usar en un solo workflow ([docs](https://docs.n8n.io/build/integrate-ai/test-and-improve-ai-workflows/use-metrics-to-measure-quality)).

## Arrancar

Requisito: [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
cd plataforma
dotnet run --project EvalPlatform
```

Abre http://localhost:5080. La primera vez se crea un ejemplo listo para ejecutar: un agente de helpdesk simulado (responde como un workflow de n8n con RAG), 7 tickets y una evaluación. En la pantalla de inicio, **Ejecutar el ejemplo**.

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

## Conectar un agente

En **Agentes → Nuevo agente** hay plantillas para los casos comunes:

| Plantilla | Cuerpo | Respuesta | Documentos (RAG) | Herramientas |
|---|---|---|---|---|
| Webhook de n8n (AI Agent) | `{ "mensaje": "{{input}}" }` | `output` | `intermediateSteps[*].observation` | `intermediateSteps[*].action.tool` |
| Endpoint propio (`/eval/run`) | `{ "input": "{{input}}" }` | `output` | — | `toolCalls[*].name` |
| Prompt directo (API OpenAI) | `messages` con tu prompt de sistema | `choices[0].message.content` | — | `choices[0].message.tool_calls[*].function.name` |
| Prompt directo (API Anthropic) | `system` + `messages` | `content[*].text` | — | `content[*].name` |

Ruta de argumentos de herramientas en cada plantilla: `intermediateSteps[*].action.toolInput` (n8n), `toolCalls[*].arguments` (endpoint propio), `choices[0].message.tool_calls[*].function.arguments` (OpenAI) y `content[*].input` (Anthropic).

Ruta de tokens en cada plantilla: `usage.inputTokens, usage.outputTokens` (endpoint propio), `usage.total_tokens` (OpenAI) y `usage.input_tokens, usage.output_tokens` (Anthropic); varias rutas separadas por coma se suman. El webhook de n8n no devuelve tokens a menos que el workflow los agregue a la respuesta.

- **Marcadores:** `{{input}}` es la columna de entrada del caso; `{{cualquier_columna}}` trae otra columna; `{{env:NOMBRE}}` lee una variable de entorno del servidor, para no guardar claves en la base de datos.
- **Rutas:** `a.b` entra en propiedades, `lista[0]` toma un elemento y `lista[*]` todos. Si un valor es texto con JSON adentro (n8n lo hace a menudo), se interpreta solo. Si la respuesta es una lista, las propiedades se aplican a cada elemento.
- **Probar:** antes de guardar, escribe una entrada de ejemplo y pulsa **Probar**. Se ve lo que se envió, la respuesta completa y qué se extrajo con cada ruta.

### n8n

En el workflow:

1. El **Webhook** debe esperar la respuesta (*Respond: Using 'Respond to Webhook' Node* o *When Last Node Finishes*) y terminar en **Respond to Webhook** con la salida del AI Agent.
2. En el **AI Agent**, activa *Return Intermediate Steps* para ver documentos y herramientas.
3. Si el agente debe decidir entre responder o escalar, conecta un **Structured Output Parser** con un campo `puede_responder` (booleano). En la evaluación, compáralo con **Es igual a** usando *Comparar otro campo de la respuesta* = `output.puede_responder`.
4. Las ramas que hacen cosas reales (responder al cliente, crear tickets, enviar correos) no deberían correr durante una evaluación. La plantilla manda el encabezado `X-Eval: 1` para que un nodo **IF** las salte. Otra opción es un segundo webhook solo para evaluaciones.

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

Esta comparación también sirve para agentes HTTP: n8n, endpoint propio, OpenAI o Anthropic, configurando *Dónde vienen sus argumentos*. Las plantillas ya lo traen. Formato de lo esperado, una llamada por línea:

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
| Se parece a | Similitud letra por letra de 0 a 1 (como *String similarity* de n8n) | No |
| Cumple un patrón | Expresión regular | No |
| Usó las herramientas | Llamó las herramientas esperadas, sin importar el orden; `ninguna` = no debía llamar herramientas | No |
| Usó las herramientas en orden | Las herramientas esperadas aparecen en ese orden (puede haber otras en medio) | No |
| Llamó con estos argumentos | La herramienta se llamó con esos argumentos (ver [Servidores MCP](#llamó-con-estos-argumentos)) | No |
| Consume pocos tokens | Tokens máximos por caso (requiere la ruta de tokens en el agente) | No |
| Responde a tiempo | Latencia máxima en milisegundos | No |
| Correcta frente a lo esperado | Mismo significado que la respuesta de referencia (como *Correctness* de n8n), de 1 a 5, con explicación | Sí |
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

## Configuración

| Clave | Por defecto | Para qué |
|---|---|---|
| `ConnectionStrings:Eval` | `Data Source=datos/eval.db` | Ruta de la base SQLite |
| `Juez:Endpoint`, `Juez:ApiKey`, `Juez:Model` | vacío | Modelo juez (ver arriba) |
| `Corridas:MaxConcurrency` | `3` | Casos en paralelo por corrida. Bájalo si el agente o n8n se saturan |
| `Demo:Habilitado` | `true` | Expone el agente simulado en `/demo/helpdesk` y el servidor MCP de demostración en `/demo/mcp` |
| `Demo:CrearEjemplo` | `true` | Crea el ejemplo si la base está vacía |

En variables de entorno, el `:` se escribe `__` (por ejemplo, `Corridas__MaxConcurrency=1`).

## Seguridad

- **No tiene inicio de sesión.** Úsala en una red interna, o detrás de un proxy con autenticación (por ejemplo Azure App Service con Entra ID o un reverse proxy con SSO).
- La plataforma hace llamadas HTTP a las URLs que se configuren en los agentes. Quien pueda crear agentes puede hacer que el servidor llame a cualquier dirección a la que tenga acceso.
- Las claves van en variables de entorno (`{{env:NOMBRE}}`), no en los encabezados guardados.
- Para producción: `Demo__Habilitado=false`.

## Publicar en un servidor

```bash
dotnet publish EvalPlatform -c Release -o publicado
cd publicado && ASPNETCORE_URLS=http://0.0.0.0:5080 ./EvalPlatform
```

La carpeta `datos/` con la base SQLite queda junto al ejecutable. Si la publicas en un servicio en la nube, apunta `ConnectionStrings__Eval` a un disco persistente.

## Desarrollo

```bash
dotnet test        # 44 pruebas: rutas JSON, plantillas, importación, comparaciones, juez, generación de casos,
                   # migración de la base, MCP (directo y con modelo) y flujo completo por API
```

Estructura:

| Ruta | Qué hay |
|---|---|
| `EvalPlatform/Domain/` | Modelo (agente, dataset, evaluación, corrida, resultados) y catálogo de comparaciones |
| `EvalPlatform/Services/` | Llamada al agente, rutas JSON, importación de casos, comparaciones, juez, cola de corridas, Excel, demo |
| `EvalPlatform/Api/` | API REST que usa la interfaz (`/api/...`) |
| `EvalPlatform/wwwroot/` | Interfaz web (HTML + JavaScript sin dependencias) |
| `EvalPlatform.Tests/` | Pruebas unitarias y de integración |

Para agregar una comparación nueva: súmala al catálogo en `Domain/CheckTypes.cs` e impleméntala en `Services/CheckRunner.cs`. La interfaz la muestra sola.
