from __future__ import annotations

import argparse
import logging
from pathlib import Path
import signal
import sys
import threading

from victus_control.controller import VictusController
from victus_control.hotkeys import HpWmiHotkeyListener
from victus_control.ipc import DEFAULT_SOCKET_PATH, VictusIpcServer
from victus_control.sysfs_backend import LinuxSysfsBackend

logger = logging.getLogger("victusd")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="VictusControl privileged hardware daemon for Linux")
    parser.add_argument("--socket", type=Path, default=DEFAULT_SOCKET_PATH, help="Unix socket path")
    parser.add_argument(
        "--settings",
        type=Path,
        default=Path("/var/lib/victus-control/settings.json"),
        help="Persistent daemon settings JSON path",
    )
    parser.add_argument("--interval", type=float, default=2.5, help="Curve & battery monitor interval (seconds)")
    args = parser.parse_args(argv)

    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
    )

    backend = LinuxSysfsBackend()
    controller = VictusController(backend=backend, settings_path=args.settings)
    ipc_server = VictusIpcServer(controller=controller, socket_path=args.socket)

    stop_event = threading.Event()

    def handle_signal(signum: int, _frame: object) -> None:
        logger.info("Received signal %s, initiating fail-safe shutdown...", signum)
        stop_event.set()

    signal.signal(signal.SIGTERM, handle_signal)
    signal.signal(signal.SIGINT, handle_signal)

    def on_hotkey_cycle() -> None:
        state = controller.cycle_profile()
        logger.info("Hardware hotkey Fn+P/OMEN cycled profile -> %s", state["profile"])

    hotkeys = HpWmiHotkeyListener(on_cycle=on_hotkey_cycle)
    if hotkeys.start():
        logger.info("Listening to HP WMI hotkeys (Fn+P / OMEN key).")
    else:
        logger.info("HP WMI hotkey input device not accessible; IPC remains active.")

    ipc_server.start()
    logger.info(
        "victusd started on %s (board=%s, profile=%s)",
        args.socket,
        controller.capabilities.board_name,
        controller.get_state()["profile"],
    )

    try:
        while not stop_event.wait(timeout=max(0.5, args.interval)):
            try:
                controller.step_curve_once()
            except Exception as exc:
                logger.warning("Curve/monitor tick warning: %s", exc)
    finally:
        hotkeys.stop()
        ipc_server.stop()
        controller.shutdown()
        logger.info("victusd stopped cleanly (fans restored to BIOS Auto if overridden).")

    return 0


if __name__ == "__main__":
    sys.exit(main())
