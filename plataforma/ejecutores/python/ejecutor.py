"""Ejecutor propio para la plataforma de evaluaciones (solo biblioteca estándar de Python 3.9+).

Corre dentro de la red donde vive tu agente: pide casos a la plataforma, llama al agente
como sea que se llame (librería, gRPC, cola de mensajes, línea de comandos...) y devuelve
la respuesta. Solo hace conexiones salientes hacia la plataforma.

Uso:
    export EVAL_PLATAFORMA=https://evaluaciones.miempresa
    export EVAL_EJECUTOR_TOKEN=...          # el mismo valor de Ejecutores__Token en la plataforma
    export EVAL_GRUPO=red-finanzas          # el grupo configurado en el agente de la plataforma
    export EVAL_PUERTO_TRAZAS=4318          # opcional: recibe las trazas OpenTelemetry del agente
    python ejecutor.py

Con EVAL_PUERTO_TRAZAS, el agente manda sus trazas a este ejecutor
(OTEL_EXPORTER_OTLP_ENDPOINT=http://<este-equipo>:4318) y el ejecutor las reenvía a la
plataforma por la misma salida. Sirve cuando el agente no tiene camino hacia la plataforma.

Para usarlo con tu agente, cambia solo la función ejecutar_agente().
"""

import http.server
import json
import os
import socket
import sys
import threading
import time
import urllib.error
import urllib.request

PLATAFORMA = os.environ.get("EVAL_PLATAFORMA", "http://localhost:5080").rstrip("/")
TOKEN = os.environ.get("EVAL_EJECUTOR_TOKEN", "")
GRUPO = os.environ.get("EVAL_GRUPO", "")
NOMBRE = os.environ.get("EVAL_NOMBRE", f"python-{socket.gethostname()}")
PUERTO_TRAZAS = int(os.environ.get("EVAL_PUERTO_TRAZAS", "0") or 0)


def ejecutar_agente(entrada, datos, traceparent=None, parametros=None):
    """Llama a tu agente con la entrada del caso y devuelve lo que respondió.

    entrada:     el texto de la columna de entrada de la evaluación.
    datos:       todas las columnas del caso (dict), por si tu agente necesita más datos.
    traceparent: si el agente en la plataforma tiene "Medir tokens con trazas", el encabezado
                 W3C con que el agente debe continuar la traza (mándalo como header
                 "traceparent" o úsalo como contexto padre de OpenTelemetry). Así sus tokens
                 llegan a la plataforma aunque el agente no los devuelva.
    parametros:  los parámetros de la variante que se está comparando (dict), por ejemplo
                 {"modelo": "gpt-5-mini", "prompt": "v2", "top_k": "10"}. Vacío si la corrida
                 no es de una variante. Úsalos para configurar tu agente en este caso.

    Devuelve un dict; solo "salida" es obligatoria:
        salida:        texto de la respuesta
        herramientas:  [{"nombre": "buscar_cliente", "argumentos": {...}, "resultado": "..."}]
        contexto:      ["documento recuperado 1", ...]   (para las comparaciones de RAG)
        tokens:        int
        crudo:         dict con la respuesta completa (para "Comparar otro campo de la respuesta")
    Si algo falla, lanza una excepción: el caso queda como "Error" con el mensaje.
    """
    # Reemplaza este ejemplo por la llamada real a tu agente, por ejemplo:
    #   respuesta = mi_agente.responder(entrada)                    # librería en el mismo proceso
    #   respuesta = stub.Responder(PreguntaRequest(texto=entrada))  # gRPC
    #   respuesta = cola.solicitar_y_esperar("agente.preguntas", entrada, timeout=60)
    if "123" in entrada:
        return {
            "salida": "El cliente 123 debe $1.200.000, con 2 facturas vencidas.",
            "herramientas": [{"nombre": "consultar_cartera", "argumentos": {"cliente": "123"}, "resultado": "saldo=1200000"}],
            "tokens": 180,
        }
    return {"salida": "Puedo consultar la cartera de un cliente por su código.", "herramientas": []}


def traceparent(caso):
    """Encabezado W3C Trace Context para la traza que la plataforma asignó al caso (o None)."""
    if not caso.get("traceId"):
        return None
    return f"00-{caso['traceId']}-{os.urandom(8).hex()}-01"


def llamar(ruta, cuerpo, espera=60):
    pedido = urllib.request.Request(
        f"{PLATAFORMA}/api/ejecutores/{ruta}",
        data=json.dumps(cuerpo).encode("utf-8"),
        headers={"Content-Type": "application/json", "X-Ejecutor-Token": TOKEN, "X-Ejecutor-Nombre": NOMBRE},
        method="POST",
    )
    with urllib.request.urlopen(pedido, timeout=espera) as respuesta:
        return json.loads(respuesta.read().decode("utf-8"))


def procesar(caso):
    inicio = time.monotonic()
    resultado = {"taskId": caso["taskId"]}
    try:
        r = ejecutar_agente(caso["input"], caso["row"], traceparent(caso), caso.get("parameters") or {})
        resultado.update(
            output=str(r.get("salida", "")),
            toolCalls=[
                {"name": h["nombre"], "arguments": h.get("argumentos"), "result": h.get("resultado")}
                for h in r.get("herramientas", [])
            ],
            context=list(r.get("contexto", [])),
            tokens=r.get("tokens"),
            raw=json.dumps(r["crudo"], ensure_ascii=False) if "crudo" in r else None,
        )
    except Exception as error:  # el caso queda como "Error" en la plataforma, con este mensaje
        resultado["error"] = f"{type(error).__name__}: {error}"
    resultado["latencyMs"] = int((time.monotonic() - inicio) * 1000)
    return llamar("resultado", resultado)


class ReenvioDeTrazas(http.server.BaseHTTPRequestHandler):
    """Recibe POST /v1/traces (OTLP/HTTP) y lo reenvía a la plataforma con el token del ejecutor."""

    def do_POST(self):
        if self.path.split("?")[0] != "/v1/traces":
            self.send_error(404)
            return
        cuerpo = self.rfile.read(int(self.headers.get("Content-Length") or 0))
        encabezados = {"Content-Type": self.headers.get("Content-Type", "application/x-protobuf"), "X-Ejecutor-Token": TOKEN}
        if self.headers.get("Content-Encoding"):
            encabezados["Content-Encoding"] = self.headers["Content-Encoding"]
        pedido = urllib.request.Request(f"{PLATAFORMA}/v1/traces", data=cuerpo, headers=encabezados, method="POST")
        try:
            with urllib.request.urlopen(pedido, timeout=30) as respuesta:
                estado, datos, tipo = respuesta.status, respuesta.read(), respuesta.headers.get("Content-Type", "application/x-protobuf")
        except urllib.error.HTTPError as error:
            estado, datos, tipo = error.code, b"", "text/plain"
        except (urllib.error.URLError, TimeoutError, ConnectionError):
            estado, datos, tipo = 503, b"", "text/plain"  # el exportador de OpenTelemetry reintenta solo
        self.send_response(estado)
        self.send_header("Content-Type", tipo)
        self.send_header("Content-Length", str(len(datos)))
        self.end_headers()
        self.wfile.write(datos)

    def log_message(self, formato, *args):
        pass


def iniciar_reenvio_de_trazas():
    servidor = http.server.ThreadingHTTPServer(("0.0.0.0", PUERTO_TRAZAS), ReenvioDeTrazas)
    threading.Thread(target=servidor.serve_forever, daemon=True).start()
    print(f"Recibiendo trazas OpenTelemetry en el puerto {PUERTO_TRAZAS} y reenviándolas a la plataforma.", flush=True)


def main():
    if not TOKEN or not GRUPO:
        sys.exit("Configura EVAL_EJECUTOR_TOKEN y EVAL_GRUPO (ver el comienzo de este archivo).")
    if PUERTO_TRAZAS:
        iniciar_reenvio_de_trazas()
    print(f"Ejecutor {NOMBRE} del grupo '{GRUPO}' esperando casos de {PLATAFORMA}…", flush=True)
    pausa = 2
    while True:
        try:
            # Espera hasta 20 s a que haya casos (long polling), así no pregunta todo el tiempo.
            casos = llamar("tomar", {"group": GRUPO, "worker": NOMBRE, "mode": "codigo", "max": 1, "waitSeconds": 20})
            pausa = 2
            for caso in casos:
                r = procesar(caso)
                print(f"Corrida {caso['runId']} · caso {caso['caseNumber']}: {r['resultado']}", flush=True)
        except urllib.error.HTTPError as error:
            detalle = error.read().decode("utf-8", "replace")
            print(f"La plataforma respondió {error.code}: {detalle}. Reintento en {pausa} s.", file=sys.stderr, flush=True)
            if error.code in (401, 503):
                sys.exit(1)
            time.sleep(pausa)
            pausa = min(pausa * 2, 60)
        except (urllib.error.URLError, TimeoutError, ConnectionError) as error:
            print(f"No se pudo conectar con la plataforma: {error}. Reintento en {pausa} s.", file=sys.stderr, flush=True)
            time.sleep(pausa)
            pausa = min(pausa * 2, 60)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
