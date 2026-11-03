# Connection Manager Server - JSON API (v1)

Base address: `https://connection-manager.lloydstechnical.com`. Every route is under
`/api/v1/`. Answers are JSON; errors are `{"error": "message for the user"}` with an HTTP
status (400 bad input, 401 not signed in, 404 not found, 429 too many sign-in attempts).

Except for `health` and `auth/token` (POST), every request needs
`Authorization: Bearer <device token>`. Clients also send `X-Client-Version: vX.Y.Z`
(shown on the website's "Signed-in PCs" list).

Do not break `/api/v1` - older clients keep using it. Add `/api/v2` for incompatible changes.

## Health and sign-in

| Method | Path | Body / answer |
|---|---|---|
| GET | `/api/v1/health` | `{"version": "v1.0.0", "api_version": 1}` - no sign-in |
| POST | `/api/v1/auth/token` | body `{"username", "password", "device_name", "platform": "windows"/"linux", "client_version"}` → `{"token", "username", "is_admin", "server_version", "api_version"}`. 5 wrong passwords lock that username for 15 minutes (429). |
| DELETE | `/api/v1/auth/token` | sign this PC out (deletes its token) |
| GET | `/api/v1/auth/me` | `{"username", "is_admin", "server_version", "api_version"}` |

## Connections

A connection (as sent to clients - **no password**):

```json
{"id": "uuid", "name": "Office Server", "ipAddress": "10.0.0.5", "location": "Head Office",
 "username": "MLTECH\\duncan", "hasPassword": true, "connectionType": "RDP",
 "sortOrder": 0, "dateCreated": "...", "updatedAt": "...", "updatedBy": "duncan"}
```

`connectionType` is one of `RDP`, `Web`, `Putty`, `Ubuntu` (old connections only), `RustDesk`.
`sortOrder` is the position inside its location group.

| Method | Path | Body / answer |
|---|---|---|
| GET | `/api/v1/revision` | `{"revision": 42}` - goes up on every change; clients poll this every 5 s |
| GET | `/api/v1/connections` | `{"revision", "connections": [...]}` |
| POST | `/api/v1/connections` | `{"name", "ipAddress", "location", "username", "password", "connectionType"}` (name, ipAddress, location required) → 201 `{"revision", "connection"}`. Goes to the end of its group. |
| PUT | `/api/v1/connections/<id>` | same fields; missing/null/"" `password` = keep the saved one; `"clearPassword": true` removes it. A new location moves it to the end of that group. |
| DELETE | `/api/v1/connections/<id>` | `{"revision", "ok": true}` |
| POST | `/api/v1/connections/<id>/copy` | duplicate: name + " (Copy)", same password, end of group → 201 |
| GET | `/api/v1/connections/<id>/secret` | `{"id", "username", "password"}` - only called when Connect is pressed |
| POST | `/api/v1/connections/order` | `{"ids": [...]}` - one group's ids in their new order |
| POST | `/api/v1/connections/import` | `{"connections": [{name, ipAddress, location, username, password, connectionType, sortOrder}]}` → `{"revision", "added", "skipped"}`. An IP/URL already on the server is skipped. |

## Client updates

| Method | Path | Body / answer |
|---|---|---|
| GET | `/api/v1/version` | `{"server_version", "api_version", "clients": {"windows": {"version", "file", "sha256", "size", "url"}, "linux": {...}, "linux-deb": {...}}}` |
| GET | `/api/v1/downloads/windows` | `ConnectionManager.exe` |
| GET | `/api/v1/downloads/linux` | `connection-manager-linux-x64.tar.gz` (folder `connection-manager/` with `ConnectionManager`, `icon.png`, `install.sh`). Used by ~/.local installs and by v16.0.0 clients. |
| GET | `/api/v1/downloads/linux-deb` | `connection-manager_X.Y.Z_amd64.deb` (v16.0.1+). Used by /opt (.deb) installs. |

The builds and their `manifest.json` are made by `run.sh` from the newest
`new-win-clinet/connection-manager-win-clinet-vX.Y.Z` and
`new-linux-clinet/connection-manager-linux-clinet-vX.Y.Z` folders. A client offers the
update when the server's version is newer than its own; it checks the SHA-256 before installing.
