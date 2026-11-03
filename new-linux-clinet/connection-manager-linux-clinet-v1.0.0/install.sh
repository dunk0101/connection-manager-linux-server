#!/usr/bin/env bash
# Connection Manager for Ubuntu - installer.
#
# Run from the unpacked download (no sudo needed for the program itself):
#     tar -xzf connection-manager-linux-x64.tar.gz
#     cd connection-manager && ./install.sh
#
# It copies the program to ~/.local/share/connection-manager/, adds Connection
# Manager to the applications menu and a `connection-manager` command, and then
# checks for the helper programs used to open connections:
#     RDP       freerdp3-x11 (xfreerdp3)
#     PuTTY     putty
#     RustDesk  the RustDesk .deb from https://rustdesk.com/
#     Tray icon gnome-shell-extension-appindicator (installed on Ubuntu by default)
# Updates later come from the About tab inside Connection Manager.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
DEST="$HOME/.local/share/connection-manager"
APPS="$HOME/.local/share/applications"
BIN="$HOME/.local/bin"

if [ "$(id -u)" -eq 0 ]; then
  echo "Run this as your normal user, not with sudo (it installs for you only)." >&2
  exit 1
fi
if [ ! -f "$HERE/ConnectionManager" ]; then
  echo "ConnectionManager was not found next to install.sh - unpack the whole download first." >&2
  exit 1
fi

echo "==> Installing Connection Manager to $DEST"
# Close a running copy so its file can be replaced cleanly.
pkill -x ConnectionManag 2>/dev/null || true
mkdir -p "$DEST" "$APPS" "$BIN"
for f in "$HERE"/*; do
  name="$(basename "$f")"
  cp -f "$f" "$DEST/$name.new"
  mv -f "$DEST/$name.new" "$DEST/$name"
done
chmod 755 "$DEST/ConnectionManager" "$DEST/install.sh"
ln -sf "$DEST/ConnectionManager" "$BIN/connection-manager"

cat > "$APPS/connection-manager.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Connection Manager
GenericName=Remote Connections
Comment=Shared remote connections (RDP, Web, PuTTY, RustDesk)
Exec="$DEST/ConnectionManager"
Icon=$DEST/icon.png
Terminal=false
Categories=Network;RemoteAccess;
StartupWMClass=ConnectionManager
EOF
update-desktop-database "$APPS" >/dev/null 2>&1 || true
echo "Added to the applications menu (search for \"Connection Manager\")."

echo
echo "==> Checking the programs used to open connections"
MISSING=()
if command -v xfreerdp3 >/dev/null || command -v sdl-freerdp3 >/dev/null || command -v xfreerdp >/dev/null; then
  echo "  RDP      : FreeRDP found"
else
  echo "  RDP      : FreeRDP is missing"; MISSING+=(freerdp3-x11)
fi
if command -v putty >/dev/null; then
  echo "  PuTTY    : found"
else
  echo "  PuTTY    : missing"; MISSING+=(putty)
fi
if command -v rustdesk >/dev/null || [ -x /usr/share/rustdesk/rustdesk ] || flatpak info com.rustdesk.RustDesk >/dev/null 2>&1; then
  echo "  RustDesk : found"
else
  echo "  RustDesk : not installed - get the Ubuntu .deb from https://rustdesk.com/ if you need it"
fi
if [ "${#MISSING[@]}" -gt 0 ]; then
  echo
  read -r -p "Install ${MISSING[*]} now with apt? (asks for your password) [Y/n] " ans || ans="n"
  if [[ "${ans:-Y}" =~ ^[Yy]$ ]]; then
    sudo apt update && sudo apt install -y "${MISSING[@]}"
  else
    echo "Later:  sudo apt install -y ${MISSING[*]}"
  fi
fi

echo
echo "Done. Starting Connection Manager..."
nohup "$DEST/ConnectionManager" >/dev/null 2>&1 &
