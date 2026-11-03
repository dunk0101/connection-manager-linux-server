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
# Install packages with apt. If Software Updater / automatic updates is using apt
# at the same time ("Could not get lock"), wait for it (up to 10 minutes) instead
# of failing straight away.
apt_busy() { pgrep -x aptd >/dev/null || pgrep -x apt >/dev/null || pgrep -x apt-get >/dev/null \
             || pgrep -x dpkg >/dev/null || pgrep -f unattended-upgrade >/dev/null; }
apt_install() {
  local i
  for i in $(seq 1 40); do
    if apt_busy; then
      [ "$i" -eq 1 ] && echo "Another program (Software Updater or automatic updates) is using apt."
      echo "  waiting for it to finish... ($((i * 15)) s)"
      sleep 15
      continue
    fi
    sudo apt-get -o DPkg::Lock::Timeout=120 update -q && \
      sudo apt-get -o DPkg::Lock::Timeout=120 install -y "$@" && return 0
    apt_busy || return 1          # failed for another reason - don't keep retrying
  done
  return 1
}

APT_FAILED=0
if [ "${#MISSING[@]}" -gt 0 ]; then
  echo
  read -r -p "Install ${MISSING[*]} now with apt? (asks for your password) [Y/n] " ans || ans="n"
  if [[ "${ans:-Y}" =~ ^[Yy]$ ]]; then
    if apt_install "${MISSING[@]}"; then
      echo "Installed: ${MISSING[*]}"
    else
      APT_FAILED=1
    fi
  else
    echo "Later:  sudo apt install -y ${MISSING[*]}"
  fi
fi

echo
if [ "$APT_FAILED" -eq 1 ]; then
  echo "Connection Manager is installed, but ${MISSING[*]} could NOT be installed."
  echo "When the Software Updater has finished, run:  sudo apt install -y ${MISSING[*]}"
  echo "(Until then RDP / PuTTY connections can't open.)"
  echo
fi
echo "Done. Starting Connection Manager..."
nohup "$DEST/ConnectionManager" >/dev/null 2>&1 &
