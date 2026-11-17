from __future__ import annotations

import json
import os
from pathlib import Path
import socket
import socketserver
import threading
from typing import Any

from victus_control.controller import VictusController
from victus_control.models import VictusError

DEFAULT_SOCKET_PATH = Path("/run/victus-control/victusd.sock")


class _ThreadedUnixStreamServer(socketserver.ThreadingMixIn, socketserver.UnixStreamServer):
    daemon_threads = True


class VictusIpcServer:
    """JSON-RPC over Unix domain socket for unprivileged GUI/CLI communication with victusd."""

    def __init__(self, controller: VictusController, socket_path: Path = DEFAULT_SOCKET_PATH) -> None:
        self.controller = controller
        self.socket_path = socket_path
        self._server: _ThreadedUnixStreamServer | None = None
        self._thread: threading.Thread | None = None

    def start(self) -> None:
        self.socket_path.parent.mkdir(parents=True, exist_ok=True)
        if self.socket_path.exists():
            self.socket_path.unlink()

        controller = self.controller

        class Handler(socketserver.StreamRequestHandler):
            def handle(self) -> None:
                raw_line = self.rfile.readline(65536)
                if not raw_line:
                    return
                try:
                    req = json.loads(raw_line.decode("utf-8"))
                    resp = {"ok": True, "result": VictusIpcServer._dispatch(controller, req)}
                except VictusError as exc:
                    resp = {"ok": False, "error": {"code": exc.code, "message": str(exc)}}
                except Exception as exc:
                    resp = {"ok": False, "error": {"code": "INTERNAL_ERROR", "message": str(exc)}}
                self.wfile.write((json.dumps(resp) + "\n").encode("utf-8"))

        self._server = _ThreadedUnixStreamServer(str(self.socket_path), Handler)
        try:
            os.chmod(self.socket_path, 0o666)
        except OSError:
            pass
        self._thread = threading.Thread(target=self._server.serve_forever, daemon=True, name="victusd-ipc")
        self._thread.start()

    def stop(self) -> None:
        if self._server is not None:
            self._server.shutdown()
            self._server.server_close()
            self._server = None
        if self._thread is not None:
            self._thread.join(timeout=1.5)
            self._thread = None
        try:
            if self.socket_path.exists():
                self.socket_path.unlink()
        except OSError:
            pass

    @staticmethod
    def _dispatch(controller: VictusController, req: dict[str, Any]) -> dict[str, Any]:
        method = str(req.get("method", ""))
        params = req.get("params") or {}
        if method == "get_state":
            return controller.get_state(allow_nvidia_smi=bool(params.get("allow_nvidia_smi", False)))
        if method == "set_profile":
            return controller.set_profile(str(params["profile"]))
        if method == "cycle_profile":
            return controller.cycle_profile()
        if method == "set_fan":
            return controller.set_fan(str(params["mode"]), percent=int(params.get("percent", 0)))
        if method == "update_settings":
            return controller.update_settings(dict(params))
        if method == "set_prime_mode":
            return controller.set_prime_mode(str(params["mode"]))
        raise VictusError(f"Unknown RPC method '{method}'.", code="UNKNOWN_METHOD")


class VictusIpcClient:
    def __init__(self, socket_path: Path = DEFAULT_SOCKET_PATH, timeout: float = 4.0) -> None:
        self.socket_path = socket_path
        self.timeout = timeout

    def is_daemon_available(self) -> bool:
        return self.socket_path.exists()

    def call(self, method: str, **params: Any) -> dict[str, Any]:
        payload = json.dumps({"method": method, "params": params}) + "\n"
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as sock:
            sock.settimeout(self.timeout)
            sock.connect(str(self.socket_path))
            sock.sendall(payload.encode("utf-8"))
            chunks: list[bytes] = []
            while True:
                chunk = sock.recv(65536)
                if not chunk:
                    break
                chunks.append(chunk)
                if b"\n" in chunk:
                    break
        resp = json.loads(b"".join(chunks).decode("utf-8"))
        if not resp.get("ok"):
            err = resp.get("error") or {}
            raise VictusError(err.get("message", "Daemon request failed"), code=err.get("code", "IPC_ERROR"))
        return dict(resp["result"])

    def get_state(self, allow_nvidia_smi: bool = False) -> dict[str, Any]:
        return self.call("get_state", allow_nvidia_smi=allow_nvidia_smi)

    def set_profile(self, profile: str) -> dict[str, Any]:
        return self.call("set_profile", profile=profile)

    def cycle_profile(self) -> dict[str, Any]:
        return self.call("cycle_profile")

    def set_fan(self, mode: str, percent: int = 0) -> dict[str, Any]:
        return self.call("set_fan", mode=mode, percent=percent)

    def update_settings(self, **updates: Any) -> dict[str, Any]:
        return self.call("update_settings", **updates)

    def set_prime_mode(self, mode: str) -> dict[str, Any]:
        return self.call("set_prime_mode", mode=mode)
