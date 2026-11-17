from __future__ import annotations

from collections.abc import Callable
import os
from pathlib import Path
import selectors
import struct
import threading

# 64-bit Linux struct input_event: timeval (ll = 16 bytes) + type (H) + code (H) + value (i) = 24 bytes
_INPUT_EVENT = struct.Struct("llHHi")
_EV_KEY = 0x01
# Key codes emitted by hp-wmi for Fn+P / OMEN key across kernel versions:
# KEY_PROG1 (148), KEY_PROG2 (149), KEY_PROG3 (202), KEY_PROG4 (203), KEY_FAVORITES (0x16c)
_OMEN_KEY_CODES = frozenset({148, 149, 202, 203, 0x16C, 0x21A9})


class HpWmiHotkeyListener:
    """Listens to '/dev/input/event*' device named 'HP WMI hotkeys' without external dependencies."""

    def __init__(self, on_cycle: Callable[[], None], sys_root: Path = Path("/sys")) -> None:
        self._on_cycle = on_cycle
        self._sys_root = sys_root
        self._stop_event = threading.Event()
        self._thread: threading.Thread | None = None

    def find_event_device(self) -> Path | None:
        input_root = self._sys_root / "class/input"
        if not input_root.is_dir():
            return None
        for entry in sorted(input_root.iterdir()):
            if not entry.name.startswith("event"):
                continue
            name_file = entry / "device/name"
            try:
                dev_name = name_file.read_text(encoding="utf-8").strip()
            except OSError:
                continue
            if "hp wmi" in dev_name.lower():
                dev_node = Path("/dev/input") / entry.name
                if dev_node.exists():
                    return dev_node
        return None

    def start(self) -> bool:
        dev_node = self.find_event_device()
        if dev_node is None:
            return False
        self._stop_event.clear()
        self._thread = threading.Thread(target=self._run_loop, args=(dev_node,), daemon=True, name="hp-wmi-hotkeys")
        self._thread.start()
        return True

    def stop(self) -> None:
        self._stop_event.set()
        if self._thread is not None:
            self._thread.join(timeout=1.5)
            self._thread = None

    def _run_loop(self, dev_node: Path) -> None:
        try:
            fd = os.open(str(dev_node), os.O_RDONLY | os.O_NONBLOCK)
        except OSError:
            return

        sel = selectors.DefaultSelector()
        sel.register(fd, selectors.EVENT_READ)
        try:
            while not self._stop_event.is_set():
                events = sel.select(timeout=0.5)
                if not events:
                    continue
                try:
                    raw = os.read(fd, _INPUT_EVENT.size * 8)
                except BlockingIOError:
                    continue
                except OSError:
                    break

                for offset in range(0, len(raw) - (len(raw) % _INPUT_EVENT.size), _INPUT_EVENT.size):
                    _sec, _usec, ev_type, code, value = _INPUT_EVENT.unpack_from(raw, offset)
                    # value == 1 is key press (ignore 0 release and 2 auto-repeat)
                    if ev_type == _EV_KEY and value == 1 and code in _OMEN_KEY_CODES:
                        try:
                            self._on_cycle()
                        except Exception:
                            pass
        finally:
            sel.close()
            try:
                os.close(fd)
            except OSError:
                pass
