from __future__ import annotations

import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from tests.test_sysfs_backend import FakeSysfsTree
from victus_control.cli import main as cli_main
from victus_control.controller import VictusController
from victus_control.ipc import VictusIpcServer
from victus_control.sysfs_backend import LinuxSysfsBackend


class CliEndToEndTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)
        self.sys_root = self.root / "sys"
        self.tree = FakeSysfsTree(self.sys_root)
        self.backend = LinuxSysfsBackend(sys_root=self.sys_root, command_runner=lambda _: (1, ""))
        self.ctrl = VictusController(self.backend, settings_path=self.root / "settings.json")
        self.sock = self.root / "victusd.sock"
        self.server = VictusIpcServer(self.ctrl, socket_path=self.sock)
        self.server.start()

    def tearDown(self) -> None:
        self.server.stop()
        self.ctrl.shutdown()
        self._tmp.cleanup()

    def _run_cli(self, *args: str) -> tuple[int, str, str]:
        out_buf = io.StringIO()
        err_buf = io.StringIO()
        with (
            patch("sys.stdout", out_buf),
            patch("sys.stderr", err_buf),
            patch("victus_control.cli.DisplayRefreshManager.set_prefer_60hz", return_value=True),
        ):
            rc = cli_main(list(args), socket_path=self.sock)
        return rc, out_buf.getvalue(), err_buf.getvalue()

    def test_cli_status_and_json(self) -> None:
        rc, out, _ = self._run_cli("--status")
        self.assertEqual(rc, 0)
        self.assertIn("8C9C", out)
        self.assertIn("D3cold", out)

        rc, jout, _ = self._run_cli("--json")
        self.assertEqual(rc, 0)
        parsed = json.loads(jout)
        self.assertTrue(parsed["daemon_connected"])

    def test_cli_set_cycle_and_fan_commands(self) -> None:
        rc, out, _ = self._run_cli("--set", "eco")
        self.assertEqual(rc, 0)
        self.assertIn("ECO", out)

        rc, out, _ = self._run_cli("--cycle")
        self.assertEqual(rc, 0)
        self.assertIn("BALANCED", out)

        rc, out, _ = self._run_cli("--fan", "60")
        self.assertEqual(rc, 0)
        self.assertIn("60%", out)

        rc, out, _ = self._run_cli("--fan", "auto")
        self.assertEqual(rc, 0)
        self.assertIn("AUTO", out)

    def test_cli_invalid_arguments_returns_exit_code_2(self) -> None:
        rc, _, err = self._run_cli("--fan", "999")
        self.assertEqual(rc, 2)
        self.assertIn("Invalid arguments", err)

        rc2, _, _ = self._run_cli("--set", "nonexistent")
        self.assertEqual(rc2, 2)


if __name__ == "__main__":
    unittest.main()
