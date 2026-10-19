# ¿Mi base de conocimiento alcanza para el helpdesk de primer nivel?

Guía para evaluar un agente de n8n (webhook → AI Agent → vector store) que decide si puede resolver un ticket con lo que hay en el RAG o si lo escala. Complementa [la investigación general](investigacion-evals-llm.md).

---

## 1. La pregunta, separada en partes

"¿El agente es capaz de contestar estos tickets?" mezcla tres causas distintas, y cada una se arregla en un lugar diferente:

| Si falla porque... | El problema está en... | Se arregla... |
|---|---|---|
| La información **no está** en la base | El contenido | Agregando o completando documentos |
| La información está pero **la búsqueda no la trae** | La recuperación (chunking, embeddings, top-k, la consulta que arma el agente) | Ajustando la ingesta o la búsqueda |
| La información llegó pero el agente **la usa mal** o inventa | El prompt o el modelo | Ajustando el prompt |

Un eval que solo dice "pasó / no pasó" no te dice cuál de las tres es. Por eso aquí cada ticket se mide en cuatro dimensiones:

| Métrica | Pregunta | Cómo se mide | Aplica a |
|---|---|---|---|
| **decision** | ¿Respondió cuando debía y escaló cuando debía? | Compara `puede_responder` del agente con `debe_responder` del dataset | Todos |
| **cobertura** | ¿Lo que recuperó el RAG contiene la respuesta correcta? | `context-recall`: el juez revisa si cada frase de la respuesta de referencia está en los fragmentos recuperados | Tickets de N1 |
| **fiel** | ¿Lo que respondió está respaldado por lo recuperado, o lo inventó? | `context-faithfulness`: el juez revisa cada afirmación de la respuesta contra los fragmentos | Todos (pesa solo si respondió) |
| **correcta** | ¿La respuesta resuelve el ticket como lo haría un agente humano? | `llm-rubric` contra la respuesta de referencia | Tickets de N1 |

Con esas cuatro, `diagnostico.js` le pone a cada ticket una etiqueta:

| Diagnóstico | Qué pasó | Qué hacer |
|---|---|---|
| **Cubierto** | Recuperó la información, respondió y respondió bien | Nada |
| **Brecha de conocimiento** | No recuperó la información y escaló | Busca la respuesta en los documentos fuente: si no está, agrégala; si está, el problema es la recuperación |
| **Brecha parcial** | Respondió con lo que encontró, pero faltaba parte | Completa el documento |
| **Alucinación** | Respondió con datos que no estaban en lo recuperado | Refuerza en el prompt "responde solo con el contexto; si no alcanza, escala" |
| **Falla del agente** | Tenía la información y respondió mal | Ajusta el prompt o el modelo |
| **Escaló sin necesidad** | Tenía la información y aun así escaló | Revisa el criterio de decisión del prompt |
| **Escalado correcto** | Caso de N2 y lo escaló | Nada |
| **Respondió un caso de N2** | Debía escalar y respondió | Agrega la regla de escalamiento al prompt |

El número que responde tu pregunta es la **capacidad N1**: tickets de primer nivel *cubiertos* sobre el total de tickets de primer nivel, en general y por categoría. Las *brechas* son la lista de contenido que le falta a la base.

---

## 2. El dataset de tickets

Archivo: `dataset/tickets.csv`, o una hoja de Google Sheets con las mismas columnas. Para usar la hoja, compártela con enlace y pasa su URL de exportación CSV:

```bash
export HELPDESK_DATASET="https://docs.google.com/spreadsheets/d/<SHEET_ID>/export?format=csv&gid=<GID>"
```

| Columna | Qué poner |
|---|---|
| `id` | Identificador del ticket |
| `categoria` | Tema (acceso, facturación, nómina...). Sirve para ver la cobertura por tema |
| `ticket` | El texto **tal como lo escribe el cliente**, sin limpiarlo de más |
| `debe_responder` | `si` si un agente de primer nivel lo resuelve; `no` si va a segundo nivel |
| `respuesta_referencia` | Cómo lo resolvió un agente humano de N1. Vacía si `debe_responder = no` |
| `notas` | Libre; no se usa en el eval |

De dónde sacarlos:

- **Tickets históricos ya resueltos** de tu herramienta de helpdesk: el texto del cliente + la respuesta con la que el humano lo cerró. Es la fuente más valiosa, porque la `respuesta_referencia` sale de cómo se resolvió de verdad.
- Toma las **categorías con más volumen** y unos 5–10 tickets por categoría. Entre 50 y 100 tickets te dan una buena primera foto.
- Incluye **tickets que N1 escaló** (`debe_responder = no`): sin ellos no sabes si el agente responde cosas que no le tocan.
- Quita datos personales (nombres, documentos, correos) antes de ponerlos en el dataset.
- La `respuesta_referencia` debe tener los **datos concretos** (la ruta del menú, el plazo, el porcentaje). La cobertura se mide buscando esas frases en lo recuperado; si la referencia es vaga ("se le explicó al cliente"), el juez no tiene qué buscar.

---

## 3. Cambios en el workflow de n8n

El eval llama a tu webhook por HTTP y necesita que la respuesta traiga tres cosas: la respuesta, la decisión y lo que recuperó el RAG.

### 3.1 Un webhook aparte para evaluación (recomendado)

Un workflow de n8n puede tener varios triggers. Agrega un segundo **Webhook** (por ejemplo, ruta `helpdesk-eval`) conectado al mismo AI Agent:

- En **Respond**, elige *Using 'Respond to Webhook' Node* (o *When Last Node Finishes*), para que la llamada espere la respuesta del agente. Si tu webhook actual responde de inmediato (*Immediately*), no devuelve nada que evaluar.
- Las ramas que tienen efectos (responder al cliente en el helpdesk, crear tickets, mandar correos) **no** se conectan a este trigger, o pasan por un nodo **IF** que revisa el header `X-Eval` que manda el eval.
- Protege la ruta con *Header Auth*.

Así no tocas el webhook de producción.

### 3.2 Que el agente devuelva lo que recuperó

En el nodo **AI Agent** → *Options* → activa **Return Intermediate Steps**. La salida del nodo pasa a incluir `intermediateSteps`, con cada llamada a la herramienta de vector store y los fragmentos que devolvió (`observation`). `transform.js` los extrae aunque vengan como texto, como JSON en texto o como lista de `{ pageContent, metadata }`.

Revisa una ejecución en n8n para confirmar la forma: depende de la versión de n8n y del tipo de nodo de vector store que uses.

### 3.3 Que el agente diga explícitamente si puede responder

En el AI Agent, activa **Require Specific Output Format** y conecta un **Structured Output Parser** con este esquema:

```json
{
  "type": "object",
  "properties": {
    "puede_responder": { "type": "boolean", "description": "true si la base de conocimiento alcanza para resolver el ticket" },
    "respuesta": { "type": "string", "description": "Respuesta al cliente, o mensaje de escalamiento" },
    "fuentes": { "type": "array", "items": { "type": "string" } }
  },
  "required": ["puede_responder", "respuesta"]
}
```

Y en el prompt de sistema, una regla de este estilo:

> Responde solo con información de los documentos que recuperes de la base de conocimiento. Si esos documentos no contienen lo necesario para resolver el ticket, o si el caso requiere acceso a la cuenta del cliente o revisar un error técnico, devuelve `puede_responder: false` y un mensaje de escalamiento. No completes con conocimiento general.

Si no puedes cambiar el formato de salida, `transform.js` infiere la decisión del texto buscando frases de escalamiento ("escalo", "segundo nivel", "no tengo información..."). Puedes ajustar esas frases con la variable `ESCALATION_REGEX`. Es menos confiable que el campo explícito.

### 3.4 Qué debe responder el webhook

En el nodo **Respond to Webhook**, *Respond With: First Incoming Item*, conectado a la salida del AI Agent. Queda algo así:

```json
{
  "output": { "puede_responder": true, "respuesta": "Después de 5 intentos fallidos..." },
  "intermediateSteps": [
    {
      "action": { "tool": "base_conocimiento", "toolInput": { "input": "usuario bloqueado" } },
      "observation": "[{\"type\":\"text\",\"text\":\"{\\\"pageContent\\\":\\\"Después de 5 intentos...\\\"}\"}]"
    }
  ]
}
```

Si prefieres armar tu propio JSON, `transform.js` también acepta `{ "respuesta", "puede_responder", "contextos": ["fragmento 1", "..."] }` en la raíz.

---

## 4. Correrlo

```bash
cd promptfoo/helpdesk
export N8N_WEBHOOK_URL=https://tu-n8n/webhook/helpdesk-eval
export ANTHROPIC_API_KEY=...            # modelo juez (se puede cambiar en promptfooconfig.yaml)
npx promptfoo@0.124.1 eval --no-cache -o resultados.json
node diagnostico.js resultados.json      # resumen en consola + diagnostico.csv
npx promptfoo@0.124.1 view               # detalle por ticket: respuesta, fragmentos, motivos del juez
```

Ajusta en `promptfooconfig.yaml` el `body` a los campos que espera tu webhook (en el ejemplo, `mensaje`).

Ejemplo de salida (con el webhook simulado `mock-n8n.js`):

```
Tickets evaluados: 7 (5 de primer nivel)
Capacidad N1: 4/5 resueltos correctamente con la base actual (80%)

Por diagnóstico:
  Cubierto                     4  HD-001, HD-002, HD-003, HD-007
  Brecha de conocimiento       1  HD-005
  Escalado correcto            1  HD-004
  Respondió un caso de N2      1  HD-006

Por categoría (cubiertos / tickets N1):
  acceso                     1/1
  ...
  usuarios                   0/1
```

`diagnostico.csv` trae una fila por ticket con el diagnóstico, la acción sugerida, la respuesta del agente, la de referencia y los motivos del juez. Puedes importarlo a Sheets y repartir las brechas entre quienes mantienen los documentos.

Para probarlo sin tu n8n: `node mock-n8n.js` en otra terminal y `N8N_WEBHOOK_URL=http://localhost:5678/webhook/helpdesk`.

### Umbrales y repeticiones

- `UMBRAL_COBERTURA` (0.6 por defecto): fracción de la respuesta de referencia que debe estar en lo recuperado para considerar que la información llegó.
- `UMBRAL_FIDELIDAD` (0.7 por defecto): fracción de lo que dijo el agente que debe estar respaldada.
- `--repeat 3` corre cada ticket tres veces. Si un ticket sale *cubierto* una vez y *brecha* otra, la recuperación está en el límite: ese contenido existe pero quedó mal indexado o mal redactado.

### Un ticket pasa o falla así

En promptfoo, un ticket **pasa** si tomó la decisión correcta y, si era de N1, respondió bien. Cobertura y fidelidad se miden pero no hacen fallar el ticket (van con `weight: 0`): sirven para explicar el fallo. Si el juez no logra calificar alguna de las dos, el diagnóstico sale como *Error* y no como *Brecha*, para no contar una falla del juez como falta de contenido.

---

## 5. Brecha de contenido vs. falla de recuperación

Un ticket marcado *Brecha de conocimiento* quiere decir que **lo que el agente recuperó** no tenía la respuesta. Para saber si el contenido existe:

1. **Búsqueda manual:** busca los datos clave de `respuesta_referencia` en los documentos fuente. Es lo más rápido con pocas brechas.
2. **Webhook de sonda:** un segundo workflow que, sin agente, consulta el vector store con el texto del ticket y devuelve los 20 primeros fragmentos. Si en esos 20 está la respuesta y en los del agente no, el problema es la recuperación: top-k muy bajo, chunks muy grandes o muy chicos, o el agente reformula mal la consulta. Si no está ni en los 20, falta el contenido.
3. Mira en `promptfoo view` la consulta que armó el agente (`toolInput` en los intermediate steps). Muchas veces el agente busca con palabras distintas a las de los documentos.

---

## 6. Alternativa: evaluaciones nativas de n8n

Como el agente ya está en n8n, también puedes usar sus [evaluaciones](https://docs.n8n.io/integrations/builtin/core-nodes/n8n-nodes-base.evaluation/): un **Evaluation Trigger** que lee el Sheet de tickets, conectado al mismo AI Agent, y nodos **Evaluation** para *Set Outputs* y *Set Metrics*.

- **A favor:** todo queda en n8n, y *Check If Evaluating* evita los efectos secundarios sin tocar el webhook.
- **En contra:** n8n no trae métricas de RAG (cobertura, fidelidad). Las tendrías que armar con nodos de LLM y prompts propios, y el diagnóstico por ticket también. *Correctness* (1–5 contra la referencia) sí viene incluida.

Recomendación: usa el eval de este repo para la foto de cobertura de la base, que es tu pregunta. Si después quieres un chequeo rápido dentro de n8n cada vez que cambias el prompt, agrega la evaluación nativa con *Correctness* sobre el mismo Sheet.

---

## 7. Límites a tener en cuenta

- **El juez se equivoca.** Antes de confiar en los números, revisa a mano 20–30 tickets y compara con su veredicto. Si no coinciden, ajusta la referencia o los umbrales.
- **La cobertura depende de la referencia.** Si la `respuesta_referencia` trae datos que no hacían falta para resolver el ticket, va a parecer que la base no los tiene. Pon solo lo esencial.
- **Solo se ve lo que el agente buscó.** Si el agente no llama la herramienta del vector store, no hay fragmentos y el ticket sale como *brecha* aunque el contenido exista. `herramientas` en la metadata muestra si la llamó.
- **El resultado vale para esta versión de la base.** Cada vez que cargues documentos nuevos, vuelve a correr el eval y compara la capacidad N1 con la corrida anterior.

---

## Fuentes

- promptfoo — [Métricas con juez LLM (context-recall, context-faithfulness, contextTransform)](https://www.promptfoo.dev/docs/configuration/expected-outputs/model-graded/), [HTTP provider](https://www.promptfoo.dev/docs/providers/http/)
- n8n — [Evaluation node](https://docs.n8n.io/integrations/builtin/core-nodes/n8n-nodes-base.evaluation/), [Métricas de evaluación](https://docs.n8n.io/build/integrate-ai/test-and-improve-ai-workflows/use-metrics-to-measure-quality)
- Anthropic — [Demystifying evals for AI agents](https://www.anthropic.com/engineering/demystifying-evals-for-ai-agents)
