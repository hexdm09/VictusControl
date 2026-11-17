#!/usr/bin/env bash
set -euo pipefail

INSTALL_DIR="/opt/victus-control"
SERVICE_NAME="victus-controld.service"
APP_ID="io.github.hexdm09.VictusControl"
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ "${1:-}" == "--uninstall" ]]; then
    echo "[VictusControl] Removing service and binaries..."
    sudo systemctl disable --now "$SERVICE_NAME" 2>/dev/null || true
    sudo rm -f "/etc/systemd/system/$SERVICE_NAME"
    sudo systemctl daemon-reload
    sudo rm -f /usr/local/bin/victusctl /usr/local/bin/victusd /usr/local/bin/victus-control-gui
    sudo rm -f "/usr/share/applications/${APP_ID}.desktop" /usr/share/applications/victus-control.desktop
    sudo rm -f "/usr/share/icons/hicolor/scalable/apps/${APP_ID}.svg"
    sudo gtk-update-icon-cache -f -t /usr/share/icons/hicolor 2>/dev/null || true
    sudo rm -rf "$INSTALL_DIR"
    echo "[VictusControl] Uninstalled cleanly."
    exit 0
fi

echo "[VictusControl] Running test suite before install..."
PYTHONPATH="$REPO_DIR/src:$REPO_DIR" python3 -m unittest discover -s "$REPO_DIR/tests" -v

echo "[VictusControl] Installing to $INSTALL_DIR..."
sudo mkdir -p "$INSTALL_DIR"
sudo cp -r "$REPO_DIR/src" "$REPO_DIR/pyproject.toml" "$INSTALL_DIR/"

sudo tee /usr/local/bin/victusctl >/dev/null <<'EOF'
#!/usr/bin/env bash
export PYTHONPATH="/opt/victus-control/src"
exec /usr/bin/python3 -m victus_control.cli "$@"
EOF

sudo tee /usr/local/bin/victusd >/dev/null <<'EOF'
#!/usr/bin/env bash
export PYTHONPATH="/opt/victus-control/src"
exec /usr/bin/python3 -m victus_control.daemon "$@"
EOF

sudo tee /usr/local/bin/victus-control-gui >/dev/null <<'EOF'
#!/usr/bin/env bash
export PYTHONPATH="/opt/victus-control/src"
exec /usr/bin/python3 -m victus_control.ui.app "$@"
EOF

sudo chmod +x /usr/local/bin/victusctl /usr/local/bin/victusd /usr/local/bin/victus-control-gui
sudo cp "$REPO_DIR/systemd/$SERVICE_NAME" "/etc/systemd/system/$SERVICE_NAME"

sudo mkdir -p /usr/share/icons/hicolor/scalable/apps /usr/share/applications
sudo cp "$REPO_DIR/src/victus_control/ui/icons/hicolor/scalable/apps/${APP_ID}.svg" \
    "/usr/share/icons/hicolor/scalable/apps/${APP_ID}.svg"
sudo rm -f /usr/share/applications/victus-control.desktop
sudo cp "$REPO_DIR/desktop/${APP_ID}.desktop" "/usr/share/applications/${APP_ID}.desktop"
sudo gtk-update-icon-cache -f -t /usr/share/icons/hicolor 2>/dev/null || true

sudo systemctl daemon-reload
sudo systemctl enable --now "$SERVICE_NAME"

echo ""
echo "[VictusControl] Installed and started '$SERVICE_NAME'!"
echo "  CLI : victusctl --status"
echo "  GUI : victus-control-gui"
