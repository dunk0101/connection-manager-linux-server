#!/usr/bin/env bash
# Connection Manager Server - install + run script. The ONLY script you need.
#
#   ./run.sh            run the NEWEST connection-manager-server-v* folder in the parent folder
#   ./run.sh v1.0.0     run a SPECIFIC version (roll back). Also accepts
#                       1.0.0, connection-manager-server-v1.0.0, or a full path.
#
# Every run:
#   1. finds the newest (or requested) version folder
#   2. installs missing system packages (asks for sudo only when needed):
#      python3-venv, curl, git, rsync and dotnet-sdk-10.0 (builds the clients)
#   3. stops EVERY running copy, whichever version folder it came from
#   4. copies code/data/ (the shared connection list, users, Key Server identity)
#      and code/config-files/ forward from the version that was running, if the
#      new version has no data - the old folder keeps its copy, so rollback still
#      works. Data folders 700, files 600.
#   5. builds / repairs code/.venv and installs the Python dependencies
#   6. BUILDS THE CLIENTS: the newest version folder in new-win-clinet/
#      (connection-manager-win-clinet-vX.Y.Z -> ConnectionManager.exe) and in
#      new-linux-clinet/ (connection-manager-linux-clinet-vX.Y.Z ->
#      connection-manager-linux-x64.tar.gz AND connection-manager_X.Y.Z_amd64.deb)
#      are built with dotnet (and dpkg-deb) into
#      code/downloads/, with their version and SHA-256. Connection Manager's About
#      tab offers them as updates. Skipped when nothing in a client changed.
#   7. installs the connection-manager.service systemd user service, and the
#      first time only: the nginx site + HTTPS certificate
#      (https://connection-manager.lloydstechnical.com) and the first admin user
#   8. starts it and checks the RUNNING version (/api/v1/health). The app waits
#      for its keys from the Key Server before it answers, so if the Key Server
#      is locked this says so (log in and press Unlock) instead of failing.
#   9. backs the PROGRAM CODE up to git: commits the folder (always once for a
#      new version, then whenever files change), tags it vX.Y.Z and pushes it to
#      GitHub (origin = git@github.com:dunk0101/connection-manager-linux-server.git).
#      Only the newest version is backed up, never a rollback. The very first
#      time, it creates the git repository and sets origin itself.
#      NEVER uploaded (see .gitignore): code/data/ (connection list, passwords,
#      users), code/config-files/, code/downloads/ (built clients), client
#      bin/ and obj/, old-win-pc-clinet/, changelogs/ and context/.
#
# Built to the same design as Address Book's run.sh.

set -euo pipefail

# --- settings ------------------------------------------------------------------
PROGRAM_NAME="Connection Manager Server"
PREFIX="connection-manager-server-v"
MAIN_FILE="code/app.py"
UNIT_NAME="connection-manager.service"
SERVICE_TEMPLATE="code/connection-manager.service"     # contains __INSTALL_DIR__
VENV_DIR="code/.venv"
REQUIREMENTS="code/requirements.txt"
DATA_DIRS=("code/data" "code/config-files")             # live data, carried forward
DATA_MARKER="code/data/connections.db"                  # file that proves data exists
DOWNLOAD_DIR="code/downloads"
PORT=5100
SITE="connection-manager.lloydstechnical.com"
HEALTH_PATH="/api/v1/health"
HEALTH_WAIT=130                                         # the app waits up to 100 s for its keys
# Client projects: <folder>/<prefix>X.Y.Z  (the newest version folder is built)
WIN_DIR="new-win-clinet";     WIN_PREFIX="connection-manager-win-clinet-v"
LINUX_DIR="new-linux-clinet"; LINUX_PREFIX="connection-manager-linux-clinet-v"
# .gitignore MUST contain each of these lines exactly, or nothing is backed up.
GITIGNORE_MUST_HAVE=("code/data/" "code/config-files/" "code/.venv/" "code/downloads/" "old-win-pc-clinet/" "changelogs/" "context/")
# Never allowed in git, whatever .gitignore says.
PRIVATE_RE='^(code/data/|code/config-files/|code/downloads/|code/\.venv/|\.venv/|old-win-pc-clinet/|changelogs/|context/)|(^|/)(bin|obj|__pycache__|\.pytest_cache)/|\.(db|enc|key|secret|pem|zip|sqlite|sqlite3|exe|dll|gz|xlsx|xls|csv)$|\.db[.-]|(^|/)keyserver(_identity)?\.json$|(^|/)\.env$'
GIT_REMOTE="git@github.com:dunk0101/connection-manager-linux-server.git"   # used the first time only
GIT_NAME="dunk0101"
GIT_EMAIL="duncan@mltech.co.uk"
# -------------------------------------------------------------------------------

if [ "$(id -u)" -eq 0 ]; then
  echo "error: run this as your normal user, not as root/sudo (it asks for your password when needed)." >&2
  exit 1
fi

# systemctl --user needs this when run from a non-interactive shell.
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

SCRIPT_DIR="$(dirname "$(readlink -f "$0")")"
PARENT_DIR="$(dirname "$SCRIPT_DIR")"
UNIT_DEST="$HOME/.config/systemd/user/$UNIT_NAME"

step() { printf '\n==> %s\n' "$*"; }
is_version_dir() { [ -f "$1/$MAIN_FILE" ]; }
version_of() { local n; n="$(basename "$1")"; echo "v${n##*"$PREFIX"}"; }
# Newest folder "<dir>/<prefix>X.Y.Z" by VERSION order (sort -V: v1.0.10 > v1.0.9).
newest_in() {
  local dir="$1" prefix="$2" d n
  for d in "$dir"/"$prefix"*; do
    [ -d "$d" ] || continue
    n="${d##*/"$prefix"}"
    case "$n" in ''|.*|*.|*..*|*[!0-9.]*) continue ;; esac   # only digits and dots
    printf '%s\t%s\n' "$n" "$(readlink -f "$d")"
  done | sort -t"$(printf '\t')" -k1,1 -V | tail -1 | cut -f2-
}
newest_version_dir() {
  local d n
  for d in "$PARENT_DIR"/"$PREFIX"*; do
    [ -d "$d" ] && is_version_dir "$d" || continue
    n="${d##*/"$PREFIX"}"
    case "$n" in ''|.*|*.|*..*|*[!0-9.]*) continue ;; esac
    printf '%s\t%s\n' "$n" "$(readlink -f "$d")"
  done | sort -t"$(printf '\t')" -k1,1 -V | tail -1 | cut -f2-
}
# Newest OTHER version folder (not INSTALL_DIR) that contains path $1.
newest_other_with() {
  local d n
  for d in "$PARENT_DIR"/"$PREFIX"*; do
    [[ "$(readlink -f "$d")" == "$INSTALL_DIR" ]] && continue
    [[ -e "$d/$1" ]] || continue
    n="${d##*/"$PREFIX"}"
    case "$n" in ''|.*|*.|*..*|*[!0-9.]*) continue ;; esac
    printf '%s\t%s\n' "$n" "$d"
  done | sort -t"$(printf '\t')" -k1,1 -V | tail -1 | cut -f2-
}
can_ask() { [ -t 0 ]; }

# --- 6. build one client ---------------------------------------------------------------
# write_manifest <out dir> <file> <version> <source hash>
write_manifest() {
  python3 - "$1" "$2" "$3" "$4" <<'PY'
import hashlib, json, os, sys, datetime
out, file, ver, src_hash = sys.argv[1:5]
path = os.path.join(out, file)
h = hashlib.sha256()
with open(path, "rb") as f:
    for chunk in iter(lambda: f.read(1 << 20), b""):
        h.update(chunk)
m = {"version": ver, "file": file, "sha256": h.hexdigest(), "size": os.path.getsize(path),
     "built_at": datetime.datetime.now().isoformat(timespec="seconds"), "source_hash": src_hash}
tmp = os.path.join(out, "manifest.json.tmp")
json.dump(m, open(tmp, "w"), indent=2)
os.replace(tmp, os.path.join(out, "manifest.json"))
print(f"  {ver}: {file}  ({m['size'] / 1048576:.1f} MB, sha256 {m['sha256'][:16]}...)")
PY
}
# up_to_date <out dir> <version> <source hash>: is that build already there?
up_to_date() {
  [[ -f "$1/manifest.json" ]] && python3 - "$1/manifest.json" "$2" "$3" <<'PY'
import json, os, sys
m = json.load(open(sys.argv[1]))
ok = m.get("version") == sys.argv[2] and m.get("source_hash") == sys.argv[3] \
     and os.path.isfile(os.path.join(os.path.dirname(sys.argv[1]), m.get("file", "")))
sys.exit(0 if ok else 1)
PY
}

# build_client <platform> <client parent dir> <prefix>
#   windows -> code/downloads/windows/ConnectionManager.exe
#   linux   -> code/downloads/linux/connection-manager-linux-x64.tar.gz   (user install / older clients)
#              code/downloads/linux-deb/connection-manager_X.Y.Z_amd64.deb  (double-click install)
build_client() {
  local platform="$1" parent="$2" prefix="$3" src ver hash stage file pkg size
  src="$(newest_in "$INSTALL_DIR/$parent" "$prefix")"
  if [[ -z "$src" ]]; then
    echo "  $platform: no $parent/${prefix}X.Y.Z folder - skipped."
    return 0
  fi
  ver="v${src##*/"$prefix"}"
  # Fingerprint of the client's source files: rebuild only when something changed.
  hash="$( cd "$src" && find . -type f ! -path './bin/*' ! -path './obj/*' -print0 | sort -z \
           | xargs -0 sha256sum | sha256sum | cut -c1-16 )"
  if [[ "$platform" == "windows" ]] && up_to_date "$DOWNLOAD_DIR/windows" "$ver" "$hash"; then
    echo "  windows: $ver already built (no changes)."; return 0
  fi
  if [[ "$platform" == "linux" ]] && up_to_date "$DOWNLOAD_DIR/linux" "$ver" "$hash" \
       && up_to_date "$DOWNLOAD_DIR/linux-deb" "$ver" "$hash"; then
    echo "  linux: $ver already built (no changes)."; return 0
  fi

  echo "  $platform: building $ver from $(basename "$parent")/$(basename "$src") ..."
  stage="$(mktemp -d)"
  if ! dotnet publish "$src/ConnectionManager.csproj" -c Release -o "$stage/publish" -v quiet -nologo \
        > "$stage/build.log" 2>&1; then
    echo "error: the $platform client did not build:" >&2
    grep -E "error|Error" "$stage/build.log" | head -20 >&2 || tail -20 "$stage/build.log" >&2
    rm -rf "$stage"
    return 1
  fi

  if [[ "$platform" == "windows" ]]; then
    mkdir -p "$DOWNLOAD_DIR/windows"
    file="ConnectionManager.exe"
    cp "$stage/publish/ConnectionManager.exe" "$DOWNLOAD_DIR/windows/$file.new"
    mv -f "$DOWNLOAD_DIR/windows/$file.new" "$DOWNLOAD_DIR/windows/$file"
    write_manifest "$DOWNLOAD_DIR/windows" "$file" "$ver" "$hash"
    rm -rf "$stage"
    return 0
  fi

  # Linux 1: the tarball (extract, double-click ConnectionManager - it installs itself).
  mkdir -p "$DOWNLOAD_DIR/linux" "$stage/tar/connection-manager"
  file="connection-manager-linux-x64.tar.gz"
  cp "$stage/publish/ConnectionManager" "$src/Assets/icon.png" "$src/install.sh" "$stage/tar/connection-manager/"
  chmod 755 "$stage/tar/connection-manager/ConnectionManager" "$stage/tar/connection-manager/install.sh"
  tar -czf "$DOWNLOAD_DIR/linux/$file.new" -C "$stage/tar" connection-manager
  mv -f "$DOWNLOAD_DIR/linux/$file.new" "$DOWNLOAD_DIR/linux/$file"
  write_manifest "$DOWNLOAD_DIR/linux" "$file" "$ver" "$hash"

  # Linux 2: the .deb (double-click -> App Center; brings FreeRDP and PuTTY with it).
  if ! command -v dpkg-deb >/dev/null; then
    echo "  WARNING: dpkg-deb is missing - the .deb was not built."
    rm -rf "$stage"; return 0
  fi
  pkg="$stage/deb"
  mkdir -p "$pkg/DEBIAN" "$pkg/opt/connection-manager" "$pkg/usr/bin" "$pkg/usr/share/applications" \
           "$pkg/usr/share/icons/hicolor/512x512/apps"
  install -m 755 "$stage/publish/ConnectionManager" "$pkg/opt/connection-manager/ConnectionManager"
  install -m 644 "$src/Assets/icon.png" "$pkg/opt/connection-manager/icon.png"
  install -m 644 "$src/Assets/icon.png" "$pkg/usr/share/icons/hicolor/512x512/apps/connection-manager.png"
  install -m 644 "$src/deb/connection-manager.desktop" "$pkg/usr/share/applications/connection-manager.desktop"
  ln -s /opt/connection-manager/ConnectionManager "$pkg/usr/bin/connection-manager"
  install -m 755 "$src/deb/postinst" "$src/deb/postrm" "$pkg/DEBIAN/"
  find "$pkg" -type d -exec chmod 755 {} +          # standard package folder permissions
  size="$(du -sk --exclude=DEBIAN "$pkg" | cut -f1)"
  sed -e "s/__VERSION__/${ver#v}/" -e "s/__SIZE__/$size/" "$src/deb/control" > "$pkg/DEBIAN/control"
  mkdir -p "$DOWNLOAD_DIR/linux-deb"
  rm -f "$DOWNLOAD_DIR"/linux-deb/*.deb
  file="connection-manager_${ver#v}_amd64.deb"
  if ! dpkg-deb --root-owner-group -Zxz --build "$pkg" "$DOWNLOAD_DIR/linux-deb/$file" > "$stage/deb.log" 2>&1; then
    echo "error: the .deb did not build:" >&2; cat "$stage/deb.log" >&2
    rm -rf "$stage"; return 1
  fi
  write_manifest "$DOWNLOAD_DIR/linux-deb" "$file" "$ver" "$hash"
  rm -rf "$stage"
}

# --- 9. back up this version's CODE to git (GitHub) -------------------------------
# Only the NEWEST version folder is backed up: a rollback must not put old code
# on top of the history. The program is already running, so a problem here is
# only reported - it never stops the program.
git_backup() {
  if ! command -v git >/dev/null; then
    echo "git is not installed - skipped.  (sudo apt install -y git)"
    return 0
  fi
  if [[ "$INSTALL_DIR" != "$(newest_version_dir)" ]]; then
    echo "$VERSION is not the newest version folder (a rollback) - not backed up."
    return 0
  fi
  if [[ ! -d .git ]]; then
    local src
    src="$(newest_other_with .git)"
    if [[ -z "$src" ]]; then
      git init -q -b main || return 1
      git remote add origin "$GIT_REMOTE" || return 1
      git config user.name "$GIT_NAME" || return 1
      git config user.email "$GIT_EMAIL" || return 1
      echo "Started a new git repository (origin = $GIT_REMOTE)."
    else
      cp -a "$src/.git" .git || return 1
      [[ -f .gitignore ]] || cp -a "$src/.gitignore" .gitignore 2>/dev/null || true
      echo "Carried the git history forward from $(version_of "$src")."
    fi
  fi
  git config user.name >/dev/null 2>&1 || git config user.name "$GIT_NAME"
  git config user.email >/dev/null 2>&1 || git config user.email "$GIT_EMAIL"
  local line
  for line in "${GITIGNORE_MUST_HAVE[@]}"; do
    if ! grep -qxF "$line" .gitignore 2>/dev/null; then
      echo "WARNING: .gitignore is missing or does not exclude $line - NOT backed up."
      return 0
    fi
  done
  git add -A || return 1
  local bad
  bad="$(git ls-files | grep -E "$PRIVATE_RE" || true)"
  # Belt and braces: whatever a file is called, refuse any database or private key.
  local f
  while IFS= read -r -d '' f; do
    [[ -f "$f" ]] || continue
    if [[ "$(head -c 15 "$f" 2>/dev/null | tr -d '\0')" == "SQLite format 3" ]] \
       || grep -qE -- '-----BEGIN [A-Z ]*PRIVATE KEY-----' "$f" 2>/dev/null; then
      bad+="${bad:+$'\n'}$f"
    fi
  done < <(git diff --cached --name-only -z --diff-filter=ACMR)
  if [[ -n "$bad" ]]; then
    git reset -q
    echo "WARNING: these private files would have been uploaded - NOT backed up:"
    echo "$bad" | sort -u | head -10 | sed 's/^/    /'
    return 0
  fi
  local last_subject
  last_subject="$(git log -1 --format=%s 2>/dev/null || true)"
  if git diff --cached --quiet && [[ "$last_subject" == "$PROGRAM_NAME $VERSION" ]]; then
    echo "No changes since the last backup."
  else
    git commit -q --allow-empty -m "$PROGRAM_NAME $VERSION" || return 1
    echo "Committed: $(git log -1 --format='%h %s')"
  fi
  echo "Backed up $(git ls-files | wc -l) code files. No connection data, passwords or builds are included."
  git tag -f "$VERSION" >/dev/null
  if ! git remote get-url origin >/dev/null 2>&1; then
    echo "No git remote 'origin' is set - saved locally only."
    return 0
  fi
  if GIT_SSH_COMMAND="ssh -o BatchMode=yes -o ConnectTimeout=15" \
       timeout 120 git push -q origin HEAD "+refs/tags/$VERSION:refs/tags/$VERSION"; then
    echo "Pushed $VERSION to $(git remote get-url origin) (branch $(git branch --show-current))."
  else
    echo "WARNING: the push to GitHub failed. The commit is saved in this folder and"
    echo "         will be pushed the next time ./run.sh runs."
  fi
}

# --- 1. choose the version ---------------------------------------------------------
if [ "${1:-}" != "" ]; then
  INSTALL_DIR=""
  for cand in "$1" "$PARENT_DIR/$1" "$PARENT_DIR/${PREFIX}${1#v}"; do
    if [ -d "$cand" ] && is_version_dir "$cand"; then INSTALL_DIR="$(readlink -f "$cand")"; break; fi
  done
  if [ -z "$INSTALL_DIR" ]; then
    echo "error: '$1' is not a $PROGRAM_NAME version folder. Available versions:" >&2
    ls -1d "$PARENT_DIR"/"$PREFIX"* 2>/dev/null | sed "s#.*/${PREFIX}#  v#" >&2
    exit 1
  fi
  step "Using the requested version"
else
  INSTALL_DIR="$(newest_version_dir)"
  INSTALL_DIR="${INSTALL_DIR:-$SCRIPT_DIR}"
  step "Newest version found in $PARENT_DIR"
fi
VERSION="$(version_of "$INSTALL_DIR")"
echo "Running $PROGRAM_NAME $VERSION from $INSTALL_DIR"
cd "$INSTALL_DIR"

# --- 2. system packages --------------------------------------------------------------
step "Checking system prerequisites"
MISSING=()
python3 -c 'import venv, ensurepip' >/dev/null 2>&1 || MISSING+=(python3-venv)
command -v curl  >/dev/null || MISSING+=(curl)
command -v git   >/dev/null || MISSING+=(git)
command -v rsync >/dev/null || MISSING+=(rsync)
{ command -v dotnet >/dev/null && dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; } || MISSING+=(dotnet-sdk-10.0)
if [ "${#MISSING[@]}" -eq 0 ]; then
  echo "All system prerequisites are installed."
else
  echo "Missing: ${MISSING[*]}  (this step needs your sudo password)"
  if ! sudo -n true 2>/dev/null && ! can_ask; then
    echo "error: no terminal to ask for the sudo password. Run this yourself, then ./run.sh again:" >&2
    echo "    sudo apt update && sudo apt install -y ${MISSING[*]}" >&2
    exit 1
  fi
  sudo apt update
  sudo apt install -y "${MISSING[@]}"
fi

# --- 3. stop every running copy (any version) -------------------------------------------
PREV_DIR=""
[[ -f "$UNIT_DEST" ]] && PREV_DIR="$(sed -n 's/^WorkingDirectory=//p' "$UNIT_DEST" | head -1)"
PREV_DIR="${PREV_DIR%/}"; PREV_DIR="${PREV_DIR%/code}"
step "Stopping any running $PROGRAM_NAME (any version)"
if systemctl --user is-active --quiet "$UNIT_NAME"; then
  systemctl --user stop "$UNIT_NAME"
  echo "Stopped the service (was running $( [[ -n "$PREV_DIR" ]] && version_of "$PREV_DIR" || echo '?'))."
else
  echo "The service was not running."
fi
if pkill -f "${PREFIX}[0-9.]+/code/\.venv/bin/"; then
  echo "Stopped $PROGRAM_NAME process(es) running outside systemd."
fi
for _ in $(seq 20); do
  ss -ltnH "sport = :$PORT" 2>/dev/null | grep -q . || break
  sleep 0.5
done
if ss -ltnH "sport = :$PORT" 2>/dev/null | grep -q .; then
  echo "WARNING: port $PORT is still in use by something else:"
  ss -ltnpH "sport = :$PORT" 2>/dev/null || true
fi

# --- 4. carry the data forward -----------------------------------------------------------
step "Checking the data (${DATA_DIRS[*]})"
if [[ -f "$DATA_MARKER" ]]; then
  echo "This version already has its data."
else
  SRC=""
  if [[ -n "$PREV_DIR" && "$PREV_DIR" != "$INSTALL_DIR" && -f "$PREV_DIR/$DATA_MARKER" ]]; then
    SRC="$PREV_DIR"
  else
    SRC="$(newest_other_with "$DATA_MARKER")"
  fi
  if [[ -n "$SRC" ]]; then
    for d in "${DATA_DIRS[@]}"; do
      if [[ -d "$SRC/$d" ]]; then
        mkdir -p "$d"
        cp -a "$SRC/$d/." "$d/"
      fi
    done
    echo "Copied the data from $(version_of "$SRC")."
    echo "The old folder keeps its copy, so './run.sh $(version_of "$SRC")' can still roll back."
  else
    echo "No data yet - this is a first install."
  fi
fi
for d in "${DATA_DIRS[@]}"; do
  mkdir -p "$d"
  find "$d" -type d -exec chmod 700 {} + 2>/dev/null || true
  find "$d" -type f -exec chmod 600 {} + 2>/dev/null || true
done
echo "Data folders are private to $USER (folders 700, files 600)."

# --- 5. dependencies (Python) ----------------------------------------------------------
venv_is_clean() {
  [[ -x "$VENV_DIR/bin/python" ]] || return 1
  "$VENV_DIR/bin/python" -c 'import sys' >/dev/null 2>&1 || return 1
  head -1 "$VENV_DIR/bin/pip" 2>/dev/null | grep -qF "$INSTALL_DIR/$VENV_DIR/bin/" || return 1
  local want have
  want="$(python3 -c 'import sys; print("%d.%d" % sys.version_info[:2])')"
  have="$(sed -n 's/^version *= *\([0-9]*\.[0-9]*\).*/\1/p' "$VENV_DIR/pyvenv.cfg" 2>/dev/null)"
  [[ "$want" == "$have" ]]
}
step "Installing dependencies ($VENV_DIR)"
if ! venv_is_clean; then
  echo "Building a fresh virtual environment..."
  rm -rf "$VENV_DIR"
  python3 -m venv "$VENV_DIR"
fi
"$VENV_DIR/bin/python" -m pip install --quiet --upgrade pip
"$VENV_DIR/bin/python" -m pip install --quiet -r "$REQUIREMENTS"
echo "Dependencies installed."

# --- 6. build the clients ---------------------------------------------------------------
step "Building the Connection Manager clients (Windows + Ubuntu)"
mkdir -p "$DOWNLOAD_DIR"
build_client windows "$WIN_DIR" "$WIN_PREFIX"
build_client linux "$LINUX_DIR" "$LINUX_PREFIX"

# --- 7. systemd user service, nginx (first time), first admin ---------------------------
step "Installing the systemd user service for $VERSION"
TEMPLATE="$INSTALL_DIR/$SERVICE_TEMPLATE"
[[ -f "$TEMPLATE" ]] || TEMPLATE="$SCRIPT_DIR/$SERVICE_TEMPLATE"
mkdir -p "$HOME/.config/systemd/user"
rm -f "$UNIT_DEST"
sed "s|__INSTALL_DIR__|$INSTALL_DIR|g" "$TEMPLATE" > "$UNIT_DEST"
systemctl --user daemon-reload
systemctl --user reenable "$UNIT_NAME" >/dev/null 2>&1
echo "Service file written: $UNIT_DEST"
if loginctl show-user "$USER" -p Linger 2>/dev/null | grep -q '=yes'; then
  echo "Lingering is on - $PROGRAM_NAME starts at boot without anyone logged in."
elif sudo loginctl enable-linger "$USER"; then
  echo "Lingering turned on - $PROGRAM_NAME now starts at boot without anyone logged in."
else
  echo "WARNING: could not turn on lingering. $PROGRAM_NAME will stop when you log out."
fi

if [[ ! -e "/etc/nginx/sites-enabled/$SITE" ]]; then
  step "First run: setting up https://$SITE (nginx + certbot)"
  if can_ask || sudo -n true 2>/dev/null; then
    sudo bash code/deploy/install-nginx-site.sh || echo "WARNING: the nginx/HTTPS setup did not finish. Run: sudo bash code/deploy/install-nginx-site.sh"
  else
    echo "No terminal to ask for the sudo password. Run this yourself:  sudo bash $INSTALL_DIR/code/deploy/install-nginx-site.sh"
  fi
fi

if ! "$VENV_DIR/bin/python" code/app.py has-users >/dev/null 2>&1; then
  step "First run: create the first admin user (signs in to Connection Manager and the website)"
  if can_ask; then
    "$VENV_DIR/bin/python" code/app.py create-admin || true
  else
    echo "No terminal to ask. Run this yourself:  $INSTALL_DIR/$VENV_DIR/bin/python $INSTALL_DIR/code/app.py create-admin"
  fi
fi

# --- 8. start and check ------------------------------------------------------------------------
step "Starting $PROGRAM_NAME $VERSION"
START_TIME="$(date '+%Y-%m-%d %H:%M:%S')"
systemctl --user start "$UNIT_NAME"
echo "Waiting for it to answer (it first fetches its keys from the Key Server - up to ${HEALTH_WAIT}s)..."
RUNNING=""
for _ in $(seq "$HEALTH_WAIT"); do
  RUNNING="$(curl -s --max-time 2 "http://127.0.0.1:$PORT$HEALTH_PATH" 2>/dev/null \
             | python3 -c 'import json,sys; print(json.load(sys.stdin).get("version",""))' 2>/dev/null || true)"
  [[ -n "$RUNNING" ]] && break
  # Stop waiting early when the journal already says the Key Server is the problem.
  if journalctl --user -u "$UNIT_NAME" --since "$START_TIME" --no-pager 2>/dev/null \
       | grep -qiE "keyserver: .*(sealed|unlock|pending|approv)"; then
    break
  fi
  sleep 1
done
if [[ "$RUNNING" == "$VERSION" ]]; then
  echo "Confirmed: $PROGRAM_NAME $RUNNING is running."
elif [[ -z "$RUNNING" ]] \
     && systemctl --user is-active --quiet "$UNIT_NAME" \
     && grep -qxF "WorkingDirectory=$INSTALL_DIR/code" "$UNIT_DEST" \
     && journalctl --user -u "$UNIT_NAME" --since "$START_TIME" --no-pager 2>/dev/null | grep -qiE "keyserver: .*(sealed|unlock|retrying|unreachable|pending|approv)"; then
  echo "$PROGRAM_NAME $VERSION has started but is WAITING FOR THE KEY SERVER."
  echo ">>> Log in to the Key Server: approve 'Connection Manager' on the Apps page (first time"
  echo "    only, with secrets cm_vault_key and cm_flask_secret ticked), or press Unlock."
  echo "    $PROGRAM_NAME then opens by itself."
else
  echo "error: expected $VERSION to be running, but the server answered '${RUNNING:-nothing}'." >&2
  journalctl --user -u "$UNIT_NAME" -n 30 --no-pager >&2 || true
  exit 1
fi

# --- 9. back up to git ---------------------------------------------------------------------------
step "Backing up the $VERSION code to git (no connection data, passwords or builds)"
git_backup || echo "WARNING: the git backup did not finish. The program is running normally."

# --- final banner ----------------------------------------------------------------------------------
cat <<EOF

================================================================
  $PROGRAM_NAME $VERSION is running  ($INSTALL_DIR)

  Website (users, signed-in PCs, client downloads):
    https://$SITE/
    http://127.0.0.1:$PORT/   (this machine only)

  Client builds offered as updates (code/downloads/):
$(for p in windows linux linux-deb; do [[ -f "$DOWNLOAD_DIR/$p/manifest.json" ]] && python3 -c "import json;m=json.load(open('$DOWNLOAD_DIR/$p/manifest.json'));print(f'    $p: {m[\"version\"]}  {m[\"file\"]}')"; done)

  After any restart it needs the Key Server to be unlocked.

  Useful commands:
    systemctl --user status $UNIT_NAME
    journalctl --user -u $UNIT_NAME -f
    code/.venv/bin/python code/app.py add-user | set-password | list-users
    ./run.sh               (re-run / upgrade to the newest version folder)
    ./run.sh v1.0.0        (roll back to a specific version - nothing is uploaded)

  New CLIENT version: copy new-win-clinet/${WIN_PREFIX}X.Y.Z (or the Linux one)
  to a higher version number, edit it, then ./run.sh - every PC is offered the update.
  New SERVER version: cp -r ${PREFIX}${VERSION#v} ${PREFIX}<new>, edit it, then ./run.sh.
================================================================
EOF
