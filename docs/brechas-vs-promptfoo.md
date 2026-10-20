# Qué hace promptfoo que la plataforma no hace

Comparación entre la plataforma de este repositorio y [promptfoo](https://github.com/promptfoo/promptfoo) (MIT, ~26 mil estrellas). Según su README, promptfoo ahora es parte de OpenAI y sigue siendo open source. Fecha: octubre de 2026.

Resumen: la plataforma ya cubre bien la **evaluación funcional**: casos, comparaciones, juez IA, RAG, MCP, historial y ejecutores en redes privadas. La brecha grande está en **seguridad (red teaming)**. Después vienen las **conversaciones de varios turnos**, la **comparación de variantes** en una misma corrida y la **automatización en CI**.

---

## 1. Seguridad: red teaming (la brecha más grande)

La plataforma evalúa lo que **tú** escribes en los casos. promptfoo además **genera ataques automáticamente** y revisa si el agente resiste.

| promptfoo | Qué hace | En la plataforma |
|---|---|---|
| **~157 plugins de vulnerabilidades** | Generan casos de ataque por categoría y los califican con un juez | No existe. Solo lo que escribas a mano |
| Inyección de prompt directa e indirecta | Instrucciones maliciosas en el mensaje, o escondidas en documentos, páginas o resultados de herramientas que el agente lee | No |
| Extracción del prompt de sistema | Intentos de que el agente revele sus instrucciones internas | No |
| Fuga de datos personales (`pii:*`) | Datos de otros clientes por conversación, sesión, API o BD | No |
| Autorización vía herramientas (`bola`, `bfla`, `rbac`) | Que el agente consulte o modifique datos de otro usuario, o use funciones que el usuario no puede | No. Para un agente de cartera o soporte es el riesgo más serio |
| Inyección por herramientas (`sql-injection`, `shell-injection`, `ssrf`) | Que la entrada del usuario llegue sin filtrar a una consulta, comando o URL | No |
| Agencia excesiva, secuestro, fuera de tema | Que haga más de lo pedido, se desvíe de su propósito o hable de cualquier cosa | Parcial: con criterios (IA) escritos a mano |
| RAG: envenenamiento y exfiltración | Documentos maliciosos en la base, o sacar documentos completos | No (sí cobertura y fidelidad) |
| Fuga entre sesiones y envenenamiento de memoria | Datos de una conversación que aparecen en otra | No (no hay varios turnos) |
| Alucinación, competidores, sesgo, contenido dañino | Juez por categoría | Parcial: con criterios a mano |
| **Estrategias de ataque** | Las mismas pruebas disfrazadas: base64, leetspeak, homoglifos, plantillas de jailbreak; jailbreak iterativo con un modelo atacante; ataques de varios turnos (Crescendo, GOAT, Hydra) | No |
| **Marcos de cumplimiento** | Presets y reportes por OWASP LLM Top 10, OWASP API Top 10, MITRE ATLAS, NIST AI RMF, ISO 42001, GDPR, EU AI Act | No |
| **Seguridad de MCP** | Envenenamiento de herramientas (instrucciones maliciosas en la descripción), servidores maliciosos junto a uno legítimo, pruebas de autenticación y autorización | Solo funcional (directo y con modelo); nada de ataques |
| Reporte de riesgos | Panel de vulnerabilidades por severidad, con mitigaciones sugeridas | No |

Otros productos de promptfoo que no tienen equivalente aquí:
- **Guardrails:** protección en tiempo real.
- **Model audit:** revisa archivos de modelos en busca de código malicioso.
- **Code scanning:** revisa pull requests con problemas de seguridad de LLM.

Estos tres son de otra categoría; no los recomiendo para esta plataforma.

## 2. Ejecución de las evaluaciones

| promptfoo | En la plataforma | Brecha |
|---|---|---|
| **Matriz prompts × modelos**: varias versiones del prompt y varios modelos en la misma corrida, lado a lado | Una evaluación = un agente. Para comparar hay que correr dos veces | **Media-alta**: es la forma natural de decidir "¿prompt v2 o v3?", "¿este modelo o el otro?" |
| **Conversaciones de varios turnos**: sesiones, secuencias de mensajes, **usuario simulado** (un modelo hace de cliente con una persona) | Cada caso es una sola pregunta | **Alta** para agentes de soporte: muchos fallos aparecen en el segundo o tercer mensaje |
| **Caché** de respuestas: no se vuelve a llamar al modelo si nada cambió | No hay caché: cada corrida vuelve a llamar al agente y al juez | Media: costo y tiempo |
| **Costo en dinero** por caso y por corrida, y la comparación `cost` | Solo tokens | Baja-media |
| **Repetir solo los fallidos** (`--filter-failing`), muestras y filtros por metadatos | Se corre todo el dataset | Media cuando el dataset crece |
| Reintentos con espera ante límites de tasa (429) y pausas entre llamadas | Concurrencia configurable, sin reintentos | Media con modelos públicos |
| **Trazas OpenTelemetry** del agente y comparaciones sobre ellas: cantidad de pasos, duración, errores | No | Media: permite evaluar el *camino* del agente sin instrumentar la respuesta |
| ~100 proveedores de modelos integrados; proveedores propios en Python, JS, Ruby, Go, comando de consola, WebSocket, navegador; MCP por stdio | HTTP, API compatible con OpenAI, MCP por HTTP, y cualquier tecnología mediante ejecutores | Baja: los ejecutores cubren lo que no es HTTP. Faltan WebSocket y MCP por stdio |
| Configuración como código (YAML en el repositorio), CLI, librería Node y **GitHub Action** que bloquea el PR si baja la calidad | Todo se configura en la interfaz; la API existe pero no hay CLI ni puerta de CI | **Media-alta** si quieres que cada cambio de prompt pase por la evaluación automáticamente |
| Generación de datasets con personas y casos límite | Sí (Generar con IA), sin personas | Baja |

## 3. Comparaciones (assertions) que faltan

La plataforma tiene 15 comparaciones. promptfoo tiene más de 50. Las que más valen la pena:

| promptfoo | Para qué sirve | Prioridad |
|---|---|---|
| `is-json` / `contains-json` **con JSON Schema** | Validar respuestas estructuradas (como el `puede_responder` del helpdesk) y su forma completa | **Alta** |
| `is-valid-openai-tools-call` | Que los argumentos de cada herramienta cumplan su esquema | Alta (complementa *Llamó con estos argumentos*) |
| `is-refusal` | Detectar que el modelo se negó a responder | Alta para seguridad y para casos que *deben* responder |
| `javascript` / `python` / `webhook` | Comparaciones propias en código. En la plataforma, la forma segura es un **webhook**: tu servicio califica | Alta |
| `similar` (embeddings) | Parecido de significado, más barato y estable que un juez | Media |
| `g-eval`, `factuality`, `model-graded-closedqa` | Variantes de juez con métodos publicados | Media (ya hay corrección y criterio) |
| `context-relevance` | ¿Lo recuperado tiene que ver con la pregunta? Completa cobertura y fidelidad | Media |
| `conversation-relevance`, `trajectory:goal-success` | Calificar una conversación o una ejecución completa | Media (requiere varios turnos) |
| `select-best` / `max-score` | Elegir la mejor de varias salidas | Media (requiere la matriz) |
| `classifier`, `moderation` | Clasificadores de toxicidad, sentimiento o PII | Media |
| `tool-call-f1`, `trace-*` | Precisión de herramientas y comparaciones sobre trazas | Baja-media |
| `bleu`, `rouge`, `meteor`, `perplexity`, `cost` | Métricas clásicas de NLP y costo | Baja |
| `starts-with`, `contains-any`, `is-sql`, `is-xml`, `is-html` | Formatos específicos | Baja |
| `not-` para cualquier comparación, `assert-set`, pesos y umbral por caso | Combinar comparaciones con más control | Media |

## 4. Donde la plataforma ya va por delante

- **Personas no técnicas:** interfaz web en español, casos desde Excel o Google Sheets, sugerencias de comparaciones y explicación de cada resultado. promptfoo es una CLI con YAML y un visor local.
- **Redes privadas y cualquier tecnología:** el modelo de **ejecutores**, que solo hacen conexiones salientes y con los secretos en su red. Con promptfoo hay que correr la herramienta dentro de cada red, o escribir un proveedor propio por lenguaje.
- **Historial compartido en un servidor:** corridas, notas de "qué cambié", comparación con la corrida anterior y Excel, sin depender de un servicio externo.
- **Diagnóstico de RAG y MCP orientado a la decisión:** la base de conocimiento alcanza o no; el servidor MCP responde bien o no; el modelo elige bien las herramientas o no.

## 5. Lo que falta para usarla en producción (no es de promptfoo)

- **Inicio de sesión y roles:** quién ve qué, y quién puede ejecutar o crear agentes. Hoy es una red interna sin usuarios.
- **Auditoría** de quién cambió qué.
- **Base de datos para muchos usuarios:** SQLite sirve para un equipo; para muchos, PostgreSQL o SQL Server.
- **Tokens de ejecutor por grupo:** hoy hay uno solo compartido.

---

## Recomendación

1. **Seguridad, en dos niveles.**
   - **Dentro de la plataforma**, un "set de seguridad básico" que genere casos de ataque para tu agente y los califique con el juez "¿resistió?": inyección de prompt directa e indirecta (en documentos y resultados de herramientas), extracción del prompt de sistema, datos de otro cliente (BOLA), uso de herramientas no permitidas, fuera de tema y PII. Es lo que más riesgo cubre para agentes de soporte y cartera.
   - **Para auditorías profundas**, usar promptfoo red team contra los mismos endpoints. Es MIT y tiene 157 plugins, estrategias y reportes por OWASP, NIST y EU AI Act que no vale la pena reimplementar. Se puede importar su resultado a la plataforma.
2. **Conversaciones de varios turnos y usuario simulado.**
3. **Comparar variantes en una corrida** (prompts × modelos) y repetir solo los fallidos.
4. **Comparaciones nuevas:** JSON Schema, validez de llamadas a herramientas, negativa a responder y calificación por webhook propio.
5. **CI:** una CLI pequeña o una acción que ejecute una evaluación y falle si baja de un umbral.
6. **Caché y costo en dinero.**
7. **Inicio de sesión y roles** antes de abrirla a más equipos.

## Fuentes

- [promptfoo en GitHub](https://github.com/promptfoo/promptfoo)
- [Tipos de comparaciones (assertions)](https://www.promptfoo.dev/docs/configuration/expected-outputs/)
- [Red teaming](https://www.promptfoo.dev/docs/red-team/), [plugins](https://www.promptfoo.dev/docs/red-team/plugins/), [estrategias](https://www.promptfoo.dev/docs/red-team/strategies/)
- [Seguridad de servidores MCP](https://www.promptfoo.dev/docs/red-team/mcp-security-testing/)
- [Proveedores](https://www.promptfoo.dev/docs/providers/), [Trazas OpenTelemetry](https://promptfoo.dev/docs/tracing/), [Introducción](https://www.promptfoo.dev/docs/intro/)
