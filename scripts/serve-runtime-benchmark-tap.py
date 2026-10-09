"""Temporary transparent loopback transport tap for opt-in real benchmarks.

Forwards provider bytes without rewriting prompts, models, sampling or tools.
Persists usage, timing, hashes and bounded request settings, never payloads/keys.
"""
import argparse
import hashlib
import http.client
import json
import select
import socket
import threading
import time
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit

HOP_HEADERS = {"connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
               "te", "trailers", "transfer-encoding", "upgrade", "content-length"}


class Observation:
    def __init__(self, path, body, label, preloaded=False):
        self.start = time.perf_counter()
        self.first = self.last = None
        self.pending = b""
        self.input_tokens = self.output_tokens = None
        self.cached_input_tokens = None
        self.generation_s = self.prefill_s = self.load_s = None
        self.frames = 0
        self.usage_complete = False
        self.stream = True
        self.preloaded = preloaded
        self.record = dict(label=label, path=path, utc=datetime.now(timezone.utc).isoformat(),
                           request_sha256=hashlib.sha256(body).hexdigest())
        try:
            request = json.loads(body)
            self.stream = request.get("stream", False)
            self.record["model"] = request.get("model")
            self.record["settings"] = {key: request[key] for key in
                ["options", "temperature", "seed", "top_p", "top_k", "max_tokens",
                 "max_completion_tokens", "max_output_tokens", "stream"] if key in request}
        except ValueError:
            pass

    def receive(self, data):
        now = time.perf_counter()
        self.pending += data
        while b"\n" in self.pending:
            line, self.pending = self.pending.split(b"\n", 1)
            line = line.strip()
            if line.startswith(b"data:"):
                line = line[5:].strip()
            if not line or line == b"[DONE]":
                continue
            try:
                event = json.loads(line)
            except ValueError:
                continue
            self.observe(event, now)

    def observe(self, event, now):
        if not isinstance(event, dict):
            return
        message = event.get("message") if isinstance(event.get("message"), dict) else {}
        choices = event.get("choices") if isinstance(event.get("choices"), list) else []
        choices = [choice for choice in choices if isinstance(choice, dict)]
        delta = event.get("delta")
        kind = event.get("type", "")
        response = event.get("response")
        generated = bool((response if isinstance(response, str) else None) or event.get("thinking")
                         or message.get("content") or message.get("thinking") or message.get("tool_calls"))
        generated |= any(bool((choice.get("delta") or {}).get(key)) for choice in choices
                         for key in ["content", "reasoning_content", "reasoning"])
        generated |= any(bool((tool.get("function") or {}).get(key)) for choice in choices
                         for tool in (choice.get("delta") or {}).get("tool_calls", []) for key in ["name", "arguments"])
        if kind.startswith("response.") and kind.endswith(".delta") and isinstance(delta, str):
            generated |= bool(delta)
        if kind == "content_block_delta" and isinstance(delta, dict):
            generated |= any(bool(delta.get(key)) for key in ["text", "thinking", "partial_json"])
        if generated:
            self.frames += 1
            self.first = now if self.first is None else self.first
            self.last = now
        usage = event.get("usage") or message.get("usage") or (response.get("usage") if isinstance(response, dict) else None) or {}
        if not isinstance(usage, dict):
            usage = {}
        input_count = usage.get("prompt_tokens", usage.get("input_tokens"))
        output_count = usage.get("completion_tokens", usage.get("output_tokens"))
        details = usage.get("prompt_tokens_details") or usage.get("input_tokens_details") or {}
        if isinstance(details, dict) and details.get("cached_tokens") is not None:
            self.cached_input_tokens = details["cached_tokens"]
        if usage.get("cache_read_input_tokens") is not None:
            self.cached_input_tokens = usage["cache_read_input_tokens"]
        if input_count is not None:
            self.input_tokens = input_count + usage.get("cache_read_input_tokens", 0) + usage.get("cache_creation_input_tokens", 0)
        if output_count is not None:
            self.output_tokens = output_count
            if choices and any(choice.get("finish_reason") is not None for choice in choices):
                self.usage_complete = True
            elif "completion_tokens" in usage and (not choices or not self.stream):
                self.usage_complete = True
            elif kind in {"response.completed", "response.failed", "response.incomplete"}:
                self.usage_complete = True
            elif kind == "message_delta" and isinstance(delta, dict) and delta.get("stop_reason"):
                self.usage_complete = True
        if kind == "message_stop":
            self.usage_complete = True
        if event.get("done"):
            self.usage_complete = True
            self.input_tokens = event.get("prompt_eval_count", self.input_tokens)
            self.output_tokens = event.get("eval_count", self.output_tokens)
            self.cached_input_tokens = event.get("prompt_eval_cached_count", self.cached_input_tokens)
            self.generation_s = event.get("eval_duration", 0) / 1e9 or None
            self.prefill_s = event.get("prompt_eval_duration", 0) / 1e9 or None
            self.load_s = event["load_duration"] / 1e9 if event.get("load_duration") is not None else None
        timings = event.get("timings") or {}
        if timings.get("predicted_ms"):
            self.generation_s = timings["predicted_ms"] / 1000
        if timings.get("prompt_ms"):
            self.prefill_s = timings["prompt_ms"] / 1000

    def finish(self, status, error=None):
        if self.pending.strip():
            try:
                self.observe(json.loads(self.pending), time.perf_counter())
            except ValueError:
                pass
        source = "provider" if self.generation_s else "stream"
        generation = self.generation_s
        if generation is None and self.first is not None and self.last > self.first and self.frames > 1:
            generation = self.last - self.first
        ttft = self.first - self.start if self.first else None
        if not self.usage_complete:
            self.output_tokens = None
        return dict(self.record, http_status=status, error=error, input_tokens=self.input_tokens,
                    cached_input_tokens=self.cached_input_tokens,
                    output_tokens=self.output_tokens, generation_s=generation, timing_source=source if generation else "unavailable",
                    tok_s=self.output_tokens/generation if generation and self.output_tokens is not None else None,
                    prefill_s=self.prefill_s, load_s=self.load_s, ttft_raw_s=ttft,
                    ttft_excluding_load_s=max(0, ttft-(self.load_s or 0)) if ttft is not None and (self.load_s is not None or self.preloaded) else None,
                    loading_excluded_by_preload=self.preloaded and self.load_s is None,
                    model_preloaded_at_dispatch=self.preloaded, frames=self.frames,
                    token_usage_complete=self.usage_complete,
                    wall_s=time.perf_counter()-self.start)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--upstream", required=True)
    parser.add_argument("--port", type=int, default=13450)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--runtime", choices=["ollama", "lmstudio"], default="ollama")
    args = parser.parse_args()
    upstream = urlsplit(args.upstream)
    if upstream.scheme != "http" or upstream.hostname not in {"localhost", "127.0.0.1"}:
        raise ValueError("Benchmark upstream must be local HTTP.")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    lock = threading.Lock()
    current = {"label": "setup", "active_inferences": 0}
    active = []

    def model_preloaded(body):
        check = http.client.HTTPConnection(upstream.hostname, upstream.port, timeout=5)
        try:
            payload = json.loads(body)
            check.request("GET", "/api/ps" if args.runtime == "ollama" else "/api/v0/models")
            response = check.getresponse()
            state = json.loads(response.read()) if response.status == 200 else {}
            if args.runtime == "ollama":
                context = (payload.get("options") or {}).get("num_ctx")
                return any(model.get("model", model.get("name")) == payload.get("model")
                           and (context is None or model.get("context_length") == context) for model in state.get("models", []))
            return any(model.get("id") == payload.get("model") and model.get("state") == "loaded" for model in state.get("data", []))
        except (OSError, http.client.HTTPException, ValueError):
            return False
        finally:
            check.close()

    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *values):
            pass

        def do_GET(self):
            self.forward()

        def do_POST(self):
            self.forward()

        def forward(self):
            if "chunked" in self.headers.get("Transfer-Encoding", "").lower():
                pieces = []
                while True:
                    size = int(self.rfile.readline().split(b";", 1)[0].strip(), 16)
                    if not size:
                        while self.rfile.readline().strip():
                            pass
                        break
                    pieces.append(self.rfile.read(size))
                    if self.rfile.read(2) != b"\r\n":
                        raise ValueError("Invalid chunk delimiter")
                body = b"".join(pieces)
            else:
                body = self.rfile.read(int(self.headers.get("Content-Length", 0)))
            if self.path == "/__benchmark__/label":
                if self.command == "POST":
                    label = json.loads(body)["label"]
                    with lock:
                        current["label"] = label
                with lock:
                    state = dict(current, active_progress=[dict(label=observation.record["label"],
                        elapsed_s=time.perf_counter()-observation.start, generated_frames=observation.frames,
                        first_frame_received=observation.first is not None) for observation in active])
                result = json.dumps(state).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(result)))
                self.end_headers()
                self.wfile.write(result)
                return
            with lock:
                label = current["label"]
            inference = self.command == "POST" and self.path.split("?")[0] in {
                "/api/chat", "/api/generate", "/v1/chat/completions", "/v1/responses", "/v1/messages"}
            observation = Observation(self.path, body, label, model_preloaded(body)) if inference else None
            if observation:
                with lock:
                    current["active_inferences"] += 1
                    active.append(observation)
            connection = http.client.HTTPConnection(upstream.hostname, upstream.port, timeout=1800)
            finished = threading.Event()
            disconnected = threading.Event()

            def watch_client():
                # Preserve cancellation while the provider is processing a long
                # prompt and has not sent response headers yet. Never consume
                # client bytes or rewrite the provider request.
                while not finished.wait(0.1):
                    try:
                        readable, _, _ = select.select([self.connection], [], [], 0)
                        if readable and not self.connection.recv(1, socket.MSG_PEEK):
                            disconnected.set()
                            if connection.sock:
                                connection.sock.shutdown(socket.SHUT_RDWR)
                            return
                    except OSError:
                        return
            status = None
            error = None
            try:
                headers = {key: value for key, value in self.headers.items() if key.lower() not in HOP_HEADERS | {"host"}}
                headers["Accept-Encoding"] = "identity"
                connection.request(self.command, self.path, body=body if body else None, headers=headers)
                if observation:
                    threading.Thread(target=watch_client, daemon=True).start()
                response = connection.getresponse()
                status = response.status
                self.send_response(status)
                for key, value in response.getheaders():
                    if key.lower() not in HOP_HEADERS:
                        self.send_header(key, value)
                self.send_header("Transfer-Encoding", "chunked")
                self.end_headers()
                while True:
                    chunk = response.read1(65536)
                    if not chunk:
                        break
                    if observation:
                        observation.receive(chunk)
                    self.wfile.write(f"{len(chunk):X}\r\n".encode()+chunk+b"\r\n")
                    self.wfile.flush()
                self.wfile.write(b"0\r\n\r\n")
                self.wfile.flush()
            except (OSError, http.client.HTTPException) as exception:
                error = "ClientDisconnected" if disconnected.is_set() else type(exception).__name__
                self.close_connection = True
            finally:
                finished.set()
                connection.close()
                if observation:
                    record = observation.finish(status, error)
                    with lock:
                        with args.output.open("a", encoding="utf-8") as file:
                            file.write(json.dumps(record)+"\n")
                        current["active_inferences"] -= 1
                        active.remove(observation)

    print(f"Benchmark transport tap: 127.0.0.1:{args.port} -> {args.upstream}", flush=True)
    ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()


if __name__ == "__main__":
    main()
