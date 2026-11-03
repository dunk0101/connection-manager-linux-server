# Connection Manager Server - project notes for Claude

- **What it is:** one server holding a shared list of remote connections (RDP, Web,
  PuTTY, RustDesk) plus two desktop clients (Windows, Ubuntu) that sign in to it.
  Replaces the old Windows-only Connection Manager v15.0.4 (its full source is in
  `changelogs/01-baseline-...md`; the `old-win-pc-clinet/` folder may be deleted).
- **Server:** Python/Flask `code/app.py` (+ `db.py`, `keys.py`, `keyserver_client.py`),
  gunicorn `--workers 1` as the `connection-manager.service` systemd user unit on
  127.0.0.1:5100; nginx serves connection-manager.lloydstechnical.com
  (`code/deploy/`). Version = server folder name `connection-manager-server-vX.Y.Z`.
- **Clients:** Avalonia 11.3 + .NET 10, one self-contained program each:
  - `new-win-clinet/connection-manager-win-clinet-vX.Y.Z/` -> `ConnectionManager.exe`
  - `new-linux-clinet/connection-manager-linux-clinet-vX.Y.Z/` -> tarball with `install.sh`
  - The client version comes from its folder name (csproj `Version` regex). A new
    client version = copy the folder to a higher number; `run.sh` builds the newest
    folder of each into `code/downloads/` and the About tab offers it as an update.
  - The two client folders hold the SAME shared code (Models, Services, ViewModels,
    Views, Styles, App, Program). Only these differ: `Platform/WindowsPlatform.cs` vs
    `Platform/LinuxPlatform.cs`, `Platform/PlatformFactory.cs`, the csproj
    (net10.0-windows/win-x64 + ProtectedData vs net10.0/linux-x64) and Linux `install.sh`.
    **A UI/logic change must be made in both** (copy the shared files across).
  - Tabs: Connections (must keep working exactly like the old program), Settings
    (clipboard, start with OS, start minimised + Server card), About (versions + Update).
    The Configuration and Other tabs were removed on purpose.
- `./run.sh` (in the server version folder) deploys the newest server version (or
  `./run.sh v1.0.0` to roll back), builds the clients, checks `GET /api/v1/health`,
  then **commits, tags and pushes the code** to
  `git@github.com:dunk0101/connection-manager-linux-server.git`. Claude never commits or
  pushes by hand. Run it in a Bash command **of its own** (its `pkill -f` pattern can
  kill a shell whose command line contains a `.venv/bin/` path of a version folder).
  First run needs a terminal (sudo for nginx/certbot, typing the first admin password).
- **Never upload:** `code/data/` (connections.db: list + encrypted passwords + users),
  `code/config-files/`, `code/downloads/`, client `bin/`/`obj/`, `old-win-pc-clinet/`,
  `changelogs/`, `context/`.
- **Passwords:** connection passwords are encrypted with `cm_vault_key` from the Key
  Server (`keys.encrypt/decrypt`); the list API never returns them - only
  `/connections/<id>/secret` when Connect is pressed. Website cookie: `cm_flask_secret`.
  Users/devices: `users` + `api_tokens` tables; admin website at `/`.
- **API:** see `API.md`. Keep `/api/v1` compatible - old clients use it.
- **Testing:**
  - Server: `cd code && .venv/bin/python -m pytest -q tests` (uses `CONNMGR_TEST_KEYS=1`
    and a scratch `CONNMGR_DATA_DIR`; `CONNMGR_DOWNLOAD_DIR` for builds).
  - Clients: this server has no desktop. Test with an Avalonia.Headless harness that
    compiles the client source and renders screenshots (see changelog 02).
- Change-log rules in `~/.claude/CLAUDE.md` still apply: every change gets a numbered
  `changelogs/NN-...-YYYY-MM-DD.md` with the full code, and states which server and
  client versions it belongs to.
