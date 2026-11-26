#!/usr/bin/env bash
# Installs MarktplaatsNotifier as a persistent systemd user service on Linux / Mini PC / Raspberry Pi.
set -euo pipefail

APP_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PUBLISH_DIR="$APP_DIR/publish/linux"
SERVICE_DIR="$HOME/.config/systemd/user"
SERVICE_FILE="$SERVICE_DIR/marktplaats-notifier.service"

echo "Publishing MarktplaatsNotifier to $PUBLISH_DIR ..."
dotnet publish "$APP_DIR/MarktplaatsNotifier.csproj" -c Release -o "$PUBLISH_DIR"

mkdir -p "$SERVICE_DIR"
cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=Marktplaats Notifier Telegram Bot
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=$APP_DIR
Environment=MARKTPLAATS_DATA_DIR=$APP_DIR
Environment=TZ=Europe/Amsterdam
ExecStart=$(command -v dotnet) $PUBLISH_DIR/MarktplaatsNotifier.dll
Restart=always
RestartSec=10

[Install]
WantedBy=default.target
EOF

systemctl --user daemon-reload
systemctl --user enable --now marktplaats-notifier.service

echo ""
echo "Installed and started marktplaats-notifier.service!"
echo "Check status:  systemctl --user status marktplaats-notifier"
echo "View logs:     journalctl --user -u marktplaats-notifier -f"
echo "Run at boot even before login: sudo loginctl enable-linger $USER"
