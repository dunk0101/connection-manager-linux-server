"""
Connection Manager Server - main server program.

PLAIN-ENGLISH DESCRIPTION
-------------------------
Connection Manager is a desktop program (Windows and Ubuntu) that keeps a list
of remote-access "connections" - Remote Desktop (RDP) machines, web pages,
PuTTY (SSH) sessions and RustDesk computers - grouped by location, and opens
any of them with one click.

This server holds ONE shared connection list. Every Connection Manager signs
in to it, so every PC sees the same connections; a change made on one PC
appears on the others within a few seconds (the clients ask
GET /api/v1/revision every 5 s and only reload the list when it changed).

  * Each person has a USER ACCOUNT (username + password). Each PC that signs
    in gets its own DEVICE TOKEN, which an admin can revoke on the website.
  * Connection PASSWORDS are stored encrypted with a key held on the Key
    Server (code/keys.py), never on this server's disk. The list endpoint
    never includes passwords; a client fetches one password only when the
    user presses Connect (GET /api/v1/connections/<id>/secret).
  * The server also hands out the newest Windows and Linux Connection
    Manager builds (code/downloads/, built by run.sh from the newest
    new-win-clinet/ and new-linux-clinet/ version folders). The client's
    About tab compares its own version with /api/v1/version and offers an
    "Update" button.
  * The website (https://connection-manager.lloydstechnical.com/) is a small
    admin page: users, signed-in devices and client downloads.

Emergency commands (from the version folder):
    code/.venv/bin/python code/app.py add-user        (add a user / admin)
    code/.venv/bin/python code/app.py set-password    (reset a password)
    code/.venv/bin/python code/app.py list-users

All JSON routes are described in API.md. Data: code/data/connections.db
(SQLite) plus a daily copy in code/data/backups/ (the last 30 are kept).

HOW TO RUN
----------
./run.sh installs it as the connection-manager.service systemd user service
on 127.0.0.1:5100, behind nginx (connection-manager.lloydstechnical.com).
"""

import getpass
import hashlib
import json
import logging
import os
import re
import secrets
import sqlite3
import sys
import time
import uuid
from datetime import date, timedelta
from functools import wraps

from flask import (
    Flask, abort, flash, g, jsonify, redirect, render_template, request,
    send_file, session, url_for,
)
from werkzeug.exceptions import HTTPException
from werkzeug.middleware.proxy_fix import ProxyFix
from werkzeug.security import check_password_hash, generate_password_hash

CODE_DIR = os.path.dirname(os.path.abspath(__file__))   # this file lives in code/
sys.path.insert(0, CODE_DIR)
import db  # noqa: E402
import keys  # noqa: E402

# The version of the folder this server is ACTUALLY running from
# ("connection-manager-server-v1.0.0" -> "v1.0.0"). run.sh asks
# /api/v1/health for it after starting the service.
_folder = os.path.basename(os.path.dirname(CODE_DIR))
_PREFIX = "connection-manager-server-"
RUNNING_VERSION = _folder[len(_PREFIX):] if _folder.startswith(_PREFIX + "v") else "dev"
API_VERSION = 1

PORT = 5100
BACKUP_DIR = os.path.join(db.DATA_DIR, "backups")
BACKUPS_KEPT = 30
DOWNLOAD_DIR = os.environ.get("CONNMGR_DOWNLOAD_DIR") or os.path.join(CODE_DIR, "downloads")
PLATFORMS = ("windows", "linux", "linux-deb")   # linux = .tar.gz, linux-deb = .deb (v16.0.1)
MAX_FAILED_LOGINS = 5
LOCKOUT_SECONDS = 15 * 60
MAX_TOKENS_PER_USER = 25
MIN_PASSWORD = 8
DEV_MODE = os.environ.get("CONNMGR_DEV") == "1"
MAX_FIELD = 500

app = Flask(
    __name__,
    template_folder=os.path.join(CODE_DIR, "templates"),
    static_folder=os.path.join(CODE_DIR, "static"),
)
app.wsgi_app = ProxyFix(app.wsgi_app, x_for=1, x_proto=1, x_host=1)

if not logging.getLogger().handlers:
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")
log = logging.getLogger("connection-manager")


def _cli_mode():
    return __name__ == "__main__" and len(sys.argv) > 1


# Wait for the keys from the Key Server before doing anything else.
# (The user commands below don't need the keys - they only touch password hashes.)
if not _cli_mode():
    keys.load_keys()
db.init()

app.config.update(
    SECRET_KEY=(keys.flask_secret() if not _cli_mode() else secrets.token_bytes(32)),
    SESSION_COOKIE_NAME="connmgr_session",
    SESSION_COOKIE_HTTPONLY=True,
    SESSION_COOKIE_SAMESITE="Lax",
    SESSION_COOKIE_SECURE=not DEV_MODE,
    PERMANENT_SESSION_LIFETIME=timedelta(days=7),
    MAX_CONTENT_LENGTH=5 * 1024 * 1024,
)


# ---------------------------------------------------------------------------
# Database connection per request, and the once-a-day backup
# ---------------------------------------------------------------------------

def get_db():
    if "db" not in g:
        g.db = db.connect()
    return g.db


@app.teardown_appcontext
def close_db(exc):
    conn = g.pop("db", None)
    if conn is not None:
        conn.close()


def daily_backup(conn):
    """A copy of the database once a day (the passwords inside stay encrypted)."""
    today = date.today().isoformat()
    if db.get_setting(conn, "last_backup") == today:
        return
    os.makedirs(BACKUP_DIR, mode=0o700, exist_ok=True)
    path = os.path.join(BACKUP_DIR, f"connections-{today}.db")
    target = sqlite3.connect(path)
    conn.backup(target)
    target.close()
    os.chmod(path, 0o600)
    db.set_setting(conn, "last_backup", today)
    conn.commit()
    old = sorted(f for f in os.listdir(BACKUP_DIR) if f.startswith("connections-"))
    for f in old[:-BACKUPS_KEPT]:
        os.remove(os.path.join(BACKUP_DIR, f))


# ---------------------------------------------------------------------------
# Users and signing in
# ---------------------------------------------------------------------------

def _hash_token(token):
    return hashlib.sha256(token.encode("utf-8")).hexdigest()


def find_user(conn, username):
    return conn.execute("SELECT * FROM users WHERE username = ?", ((username or "").strip(),)).fetchone()


# A fixed hash, so a wrong username takes as long as a wrong password.
_DUMMY_HASH = generate_password_hash("not-a-real-password")


def check_login(conn, username, password):
    user = find_user(conn, username)
    ok = check_password_hash(user["password_hash"] if user else _DUMMY_HASH, password or "")
    return user if (user and ok) else None


def _lock_key(username):
    return "locked_until:" + (username or "").strip().lower()


def _locked_message(conn, username):
    locked_until = float(db.get_setting(conn, _lock_key(username), "0"))
    if time.time() < locked_until:
        mins = int((locked_until - time.time()) // 60) + 1
        return f"Too many attempts. Try again in {mins} minute{'s' if mins != 1 else ''}."
    return None


def _record_failed_login(conn, username):
    key = "failed_logins:" + (username or "").strip().lower()
    fails = int(db.get_setting(conn, key, "0")) + 1
    if fails >= MAX_FAILED_LOGINS:
        db.set_setting(conn, _lock_key(username), str(time.time() + LOCKOUT_SECONDS))
        fails = 0
    db.set_setting(conn, key, str(fails))
    conn.commit()


def _clear_failed_logins(conn, username):
    db.set_setting(conn, "failed_logins:" + (username or "").strip().lower(), "0")


def _token_auth():
    """Returns (token row, user row) for a valid 'Authorization: Bearer' header."""
    header = request.headers.get("Authorization", "")
    if not header.lower().startswith("bearer "):
        return None, None
    conn = get_db()
    row = conn.execute("SELECT * FROM api_tokens WHERE token_hash = ?",
                       (_hash_token(header[7:].strip()),)).fetchone()
    if not row:
        return None, None
    user = conn.execute("SELECT * FROM users WHERE id = ?", (row["user_id"],)).fetchone()
    if not user:
        return None, None
    version = request.headers.get("X-Client-Version", "")[:40]
    conn.execute("UPDATE api_tokens SET last_used_at = ?, client_version = CASE WHEN ? = '' "
                 "THEN client_version ELSE ? END WHERE id = ?",
                 (db.now(), version, version, row["id"]))
    conn.commit()
    return row, user


def _session_user():
    uid = session.get("uid")
    if not uid:
        return None
    user = get_db().execute("SELECT * FROM users WHERE id = ?", (uid,)).fetchone()
    # Changing a password signs out every website session of that user.
    if not user or session.get("pw") != user["password_hash"][-16:]:
        session.clear()
        return None
    return user


def api_login_required(view):
    """JSON API: a device token from a Connection Manager client."""
    @wraps(view)
    def wrapped(*args, **kwargs):
        token, user = _token_auth()
        if not user:
            return jsonify(error="Not signed in"), 401
        g.user = user
        g.token_id = token["id"]
        return view(*args, **kwargs)
    return wrapped


def page_login_required(admin=False):
    """Website pages: a signed-in browser session (admins only if admin=True)."""
    def deco(view):
        @wraps(view)
        def wrapped(*args, **kwargs):
            user = _session_user()
            if not user:
                return redirect(url_for("login"))
            if admin and not user["is_admin"]:
                abort(403)
            if request.method == "POST" and request.form.get("csrf") != session.get("csrf"):
                flash("Your session expired. Please try again.", "error")
                return redirect(request.path)
            g.user = user
            return view(*args, **kwargs)
        return wrapped
    return deco


@app.after_request
def security_headers(resp):
    resp.headers["X-Content-Type-Options"] = "nosniff"
    resp.headers["X-Frame-Options"] = "DENY"
    resp.headers["Referrer-Policy"] = "same-origin"
    if request.path.startswith("/api/"):
        resp.headers.setdefault("Cache-Control", "private, no-store")
    return resp


@app.errorhandler(HTTPException)
def http_error(exc):
    # The API always answers in JSON, so a client never has to read HTML.
    if request.path.startswith("/api/"):
        return jsonify(error=exc.description or exc.name), exc.code
    return exc


def body():
    b = request.get_json(silent=True)
    return b if isinstance(b, (dict, list)) else {}


def _text(value, field, required=False):
    value = "" if value is None else str(value).strip()
    if required and not value:
        abort(400, description=f"{field} is required.")
    if len(value) > MAX_FIELD:
        abort(400, description=f"{field} is too long.")
    return value


# ---------------------------------------------------------------------------
# Client downloads (built by run.sh into code/downloads/<platform>/)
# ---------------------------------------------------------------------------

def download_info(platform):
    """manifest.json written by run.sh: {"version", "file", "sha256", "size", "built_at"}."""
    try:
        with open(os.path.join(DOWNLOAD_DIR, platform, "manifest.json"), encoding="utf-8") as f:
            info = json.load(f)
        if os.path.isfile(os.path.join(DOWNLOAD_DIR, platform, info["file"])):
            return info
    except (OSError, ValueError, KeyError):
        pass
    return None


def _send_download(platform):
    if platform not in PLATFORMS:
        abort(404)
    info = download_info(platform)
    if not info:
        abort(404, description=f"No {platform} build is available on the server yet.")
    return send_file(os.path.join(DOWNLOAD_DIR, platform, info["file"]), as_attachment=True,
                     download_name=info["file"], max_age=0)


# ---------------------------------------------------------------------------
# API: health, sign-in, version
# ---------------------------------------------------------------------------

@app.route("/api/v1/health")
def health():
    # run.sh calls this after starting the service. No login; only versions.
    return jsonify({"version": RUNNING_VERSION, "api_version": API_VERSION})


@app.route("/api/v1/auth/token", methods=["POST"])
def api_token_create():
    """Sign in from Connection Manager.
    Body: {"username", "password", "device_name", "platform", "client_version"}."""
    conn = get_db()
    b = body()
    username = str(b.get("username") or "")
    if msg := _locked_message(conn, username):
        return jsonify(error=msg), 429
    user = check_login(conn, username, str(b.get("password") or ""))
    if not user:
        _record_failed_login(conn, username)
        return jsonify(error="That username or password is not right."), 401
    _clear_failed_logins(conn, username)
    token = secrets.token_urlsafe(32)
    conn.execute(
        "INSERT INTO api_tokens (user_id, token_hash, device_name, platform, client_version, created_at, last_used_at) "
        "VALUES (?, ?, ?, ?, ?, ?, ?)",
        (user["id"], _hash_token(token), str(b.get("device_name") or "PC")[:80],
         str(b.get("platform") or "")[:20], str(b.get("client_version") or "")[:40], db.now(), db.now()))
    # Keep only each user's newest devices.
    conn.execute("DELETE FROM api_tokens WHERE user_id = ? AND id NOT IN "
                 "(SELECT id FROM api_tokens WHERE user_id = ? ORDER BY id DESC LIMIT ?)",
                 (user["id"], user["id"], MAX_TOKENS_PER_USER))
    conn.commit()
    log.info("sign-in: %s on %s", user["username"], b.get("device_name"))
    return jsonify(token=token, username=user["username"], is_admin=bool(user["is_admin"]),
                   server_version=RUNNING_VERSION, api_version=API_VERSION)


@app.route("/api/v1/auth/token", methods=["DELETE"])
@api_login_required
def api_token_revoke_current():
    """Sign this PC out (forget the token it used)."""
    conn = get_db()
    conn.execute("DELETE FROM api_tokens WHERE id = ?", (g.token_id,))
    conn.commit()
    return jsonify(ok=True)


@app.route("/api/v1/auth/me")
@api_login_required
def api_me():
    return jsonify(username=g.user["username"], is_admin=bool(g.user["is_admin"]),
                   server_version=RUNNING_VERSION, api_version=API_VERSION)


@app.route("/api/v1/version")
@api_login_required
def api_version():
    """Server version plus the newest client build for each platform."""
    clients = {}
    for p in PLATFORMS:
        info = download_info(p)
        if info:
            clients[p] = {"version": info["version"], "file": info["file"], "sha256": info["sha256"],
                          "size": info.get("size", 0), "url": f"/api/v1/downloads/{p}"}
    return jsonify(server_version=RUNNING_VERSION, api_version=API_VERSION, clients=clients)


@app.route("/api/v1/downloads/<platform>")
@api_login_required
def api_download(platform):
    return _send_download(platform)


# ---------------------------------------------------------------------------
# API: connections
# ---------------------------------------------------------------------------

def connection_or_404(conn, cid):
    row = conn.execute("SELECT * FROM connections WHERE id = ?", (cid,)).fetchone()
    if not row:
        abort(404, description="That connection no longer exists (someone may have deleted it).")
    return row


def _type(value):
    value = str(value or "RDP")
    for t in db.CONNECTION_TYPES:
        if t.lower() == value.lower():
            return t
    abort(400, description=f"Unknown connection type '{value}'.")


def _changed(conn):
    rev = db.bump_revision(conn)
    conn.commit()
    return rev


@app.route("/api/v1/revision")
@api_login_required
def api_revision():
    """Cheap poll: the number goes up whenever the connection list changes."""
    return jsonify(revision=db.revision(get_db()))


@app.route("/api/v1/connections")
@api_login_required
def api_connections():
    conn = get_db()
    daily_backup(conn)
    rows = conn.execute("SELECT * FROM connections ORDER BY location COLLATE NOCASE, sort_order, name").fetchall()
    return jsonify(revision=db.revision(conn), connections=[db.connection_dict(r) for r in rows])


def _insert(conn, b, sort_order=None):
    name = _text(b.get("name"), "Connection name", required=True)
    address = _text(b.get("ipAddress"), "IP address or URL", required=True)
    location = _text(b.get("location"), "Location", required=True)
    cid = str(uuid.uuid4())
    if sort_order is None:
        sort_order = db.next_sort_order(conn, location)
    conn.execute(
        "INSERT INTO connections (id, name, address, location, username, password_enc, connection_type, "
        "sort_order, created_at, updated_at, updated_by) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
        (cid, name, address, location, _text(b.get("username"), "Username"),
         keys.encrypt(str(b.get("password") or "")), _type(b.get("connectionType")),
         sort_order, db.now(), db.now(), g.user["username"]))
    return cid


@app.route("/api/v1/connections", methods=["POST"])
@api_login_required
def api_connection_create():
    """Add a connection. It goes to the end of its location group."""
    conn = get_db()
    cid = _insert(conn, body())
    rev = _changed(conn)
    return jsonify(revision=rev, connection=db.connection_dict(connection_or_404(conn, cid))), 201


@app.route("/api/v1/connections/<cid>", methods=["PUT", "PATCH"])
@api_login_required
def api_connection_update(cid):
    """Edit a connection. "password" missing, null or "" = keep the saved
    password (as in the old Edit dialog). A new location moves it to the end
    of that group."""
    conn = get_db()
    row = connection_or_404(conn, cid)
    b = body()
    name = _text(b.get("name", row["name"]), "Connection name", required=True)
    address = _text(b.get("ipAddress", row["address"]), "IP address or URL", required=True)
    location = _text(b.get("location", row["location"]), "Location", required=True)
    username = _text(b.get("username", row["username"]), "Username")
    ctype = _type(b.get("connectionType", row["connection_type"]))
    password_enc = row["password_enc"]
    if b.get("password"):
        password_enc = keys.encrypt(str(b["password"]))
    elif b.get("clearPassword"):
        password_enc = ""
    sort_order = row["sort_order"]
    if location.lower() != row["location"].lower():
        sort_order = db.next_sort_order(conn, location)
    conn.execute(
        "UPDATE connections SET name = ?, address = ?, location = ?, username = ?, password_enc = ?, "
        "connection_type = ?, sort_order = ?, updated_at = ?, updated_by = ? WHERE id = ?",
        (name, address, location, username, password_enc, ctype, sort_order, db.now(),
         g.user["username"], cid))
    rev = _changed(conn)
    return jsonify(revision=rev, connection=db.connection_dict(connection_or_404(conn, cid)))


@app.route("/api/v1/connections/<cid>", methods=["DELETE"])
@api_login_required
def api_connection_delete(cid):
    conn = get_db()
    connection_or_404(conn, cid)
    conn.execute("DELETE FROM connections WHERE id = ?", (cid,))
    rev = _changed(conn)
    log.info("deleted connection %s (by %s)", cid, g.user["username"])
    return jsonify(revision=rev, ok=True)


@app.route("/api/v1/connections/<cid>/copy", methods=["POST"])
@api_login_required
def api_connection_copy(cid):
    """Duplicate a connection: name + " (Copy)", same password, end of its group."""
    conn = get_db()
    row = connection_or_404(conn, cid)
    new_id = str(uuid.uuid4())
    conn.execute(
        "INSERT INTO connections (id, name, address, location, username, password_enc, connection_type, "
        "sort_order, created_at, updated_at, updated_by) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
        (new_id, (row["name"] + " (Copy)")[:MAX_FIELD], row["address"], row["location"], row["username"],
         row["password_enc"], row["connection_type"], db.next_sort_order(conn, row["location"]),
         db.now(), db.now(), g.user["username"]))
    rev = _changed(conn)
    return jsonify(revision=rev, connection=db.connection_dict(connection_or_404(conn, new_id))), 201


@app.route("/api/v1/connections/<cid>/secret")
@api_login_required
def api_connection_secret(cid):
    """The saved password - fetched only when the user presses Connect."""
    row = connection_or_404(get_db(), cid)
    try:
        password = keys.decrypt(row["password_enc"])
    except Exception:  # noqa: BLE001 - a wrong key must not crash the server
        log.error("could not decrypt the password of connection %s", cid)
        abort(500, description="The saved password could not be decrypted on the server.")
    return jsonify(id=cid, username=row["username"], password=password)


@app.route("/api/v1/connections/order", methods=["POST"])
@api_login_required
def api_connection_order():
    """Move Up / Move Down. Body: {"ids": [...]} - one group's connections in
    their new order; each gets sort_order = its position in the list."""
    conn = get_db()
    ids = body().get("ids") if isinstance(body(), dict) else None
    if not isinstance(ids, list) or not ids:
        abort(400, description="ids must be a list of connection ids.")
    for i, cid in enumerate(ids):
        conn.execute("UPDATE connections SET sort_order = ? WHERE id = ?", (i, str(cid)))
    rev = _changed(conn)
    return jsonify(revision=rev, ok=True)


@app.route("/api/v1/connections/import", methods=["POST"])
@api_login_required
def api_connection_import():
    """Bulk upload, e.g. from an old Connection Manager's config.json.
    Body: {"connections": [{name, ipAddress, location, username, password,
    connectionType, sortOrder}, ...]}. A connection whose IP address / URL is
    already on the server is skipped (the same rule the old program used)."""
    conn = get_db()
    b = body()
    items = b.get("connections") if isinstance(b, dict) else b
    if not isinstance(items, list):
        abort(400, description="connections must be a list.")
    existing = {r["address"].strip().lower() for r in conn.execute("SELECT address FROM connections")}
    added = skipped = 0
    for item in sorted((i for i in items if isinstance(i, dict)),
                       key=lambda i: (str(i.get("location", "")).lower(), int(i.get("sortOrder") or 0))):
        address = str(item.get("ipAddress") or "").strip()
        if not address or address.lower() in existing:
            skipped += 1
            continue
        _insert(conn, item)
        existing.add(address.lower())
        added += 1
    rev = _changed(conn) if added else db.revision(conn)
    log.info("import by %s: %d added, %d skipped", g.user["username"], added, skipped)
    return jsonify(revision=rev, added=added, skipped=skipped)


# ---------------------------------------------------------------------------
# Website: sign in, admin pages (users, devices, downloads)
# ---------------------------------------------------------------------------

def _csrf():
    if "csrf" not in session:
        session["csrf"] = secrets.token_hex(16)
    return session["csrf"]


@app.context_processor
def template_globals():
    return {"version": RUNNING_VERSION, "csrf": _csrf(), "me": g.get("user")}


@app.route("/login", methods=["GET", "POST"])
def login():
    conn = get_db()
    error = None
    username = ""
    if request.method == "POST":
        username = (request.form.get("username") or "").strip()
        if msg := _locked_message(conn, username):
            error = msg
        elif request.form.get("csrf") != session.get("csrf"):
            error = "Your session expired. Please try again."
        elif user := check_login(conn, username, request.form.get("password", "")):
            _clear_failed_logins(conn, username)
            conn.commit()
            session.clear()
            session.permanent = True
            session["uid"] = user["id"]
            session["pw"] = user["password_hash"][-16:]
            return redirect(url_for("index"))
        else:
            _record_failed_login(conn, username)
            error = "That username or password is not right."
    return render_template("login.html", error=error, username=username)


@app.route("/logout", methods=["POST"])
def logout():
    session.clear()
    return redirect(url_for("login"))


@app.route("/")
@page_login_required()
def index():
    conn = get_db()
    daily_backup(conn)
    users = conn.execute("SELECT * FROM users ORDER BY username COLLATE NOCASE").fetchall() if g.user["is_admin"] else []
    q = ("SELECT t.*, u.username FROM api_tokens t JOIN users u ON u.id = t.user_id "
         + ("" if g.user["is_admin"] else "WHERE t.user_id = ? ") + "ORDER BY t.last_used_at DESC")
    devices = conn.execute(q, () if g.user["is_admin"] else (g.user["id"],)).fetchall()
    counts = conn.execute("SELECT COUNT(*) AS n, COUNT(DISTINCT location) AS l FROM connections").fetchone()
    return render_template("index.html", users=users, devices=devices, counts=counts,
                           downloads={p: download_info(p) for p in PLATFORMS})


@app.route("/download/<platform>")
@page_login_required()
def page_download(platform):
    return _send_download(platform)


def _valid_new_password(pw):
    if len(pw or "") < MIN_PASSWORD:
        flash(f"The password must be at least {MIN_PASSWORD} characters.", "error")
        return False
    return True


@app.route("/users", methods=["POST"])
@page_login_required(admin=True)
def page_user_add():
    conn = get_db()
    username = (request.form.get("username") or "").strip()
    password = request.form.get("password") or ""
    if not re.fullmatch(r"[A-Za-z0-9._@-]{2,60}", username):
        flash("Usernames are 2-60 letters, numbers or . _ @ -", "error")
    elif find_user(conn, username):
        flash(f"There is already a user called {username}.", "error")
    elif _valid_new_password(password):
        conn.execute("INSERT INTO users (username, password_hash, is_admin, created_at, updated_at) "
                     "VALUES (?, ?, ?, ?, ?)",
                     (username, generate_password_hash(password), 1 if request.form.get("is_admin") else 0,
                      db.now(), db.now()))
        conn.commit()
        flash(f"Added {username}.", "ok")
    return redirect(url_for("index") + "#users")


@app.route("/users/<int:uid>/password", methods=["POST"])
@page_login_required(admin=True)
def page_user_password(uid):
    conn = get_db()
    user = conn.execute("SELECT * FROM users WHERE id = ?", (uid,)).fetchone() or abort(404)
    password = request.form.get("password") or ""
    if _valid_new_password(password):
        conn.execute("UPDATE users SET password_hash = ?, updated_at = ? WHERE id = ?",
                     (generate_password_hash(password), db.now(), uid))
        # A new password signs that user out of every PC.
        conn.execute("DELETE FROM api_tokens WHERE user_id = ?", (uid,))
        conn.commit()
        flash(f"Password changed for {user['username']}. They must sign in again on every PC.", "ok")
    if uid == g.user["id"]:
        return redirect(url_for("login"))
    return redirect(url_for("index") + "#users")


@app.route("/users/<int:uid>/delete", methods=["POST"])
@page_login_required(admin=True)
def page_user_delete(uid):
    conn = get_db()
    user = conn.execute("SELECT * FROM users WHERE id = ?", (uid,)).fetchone() or abort(404)
    if uid == g.user["id"]:
        flash("You can't delete yourself.", "error")
    else:
        conn.execute("DELETE FROM users WHERE id = ?", (uid,))
        conn.commit()
        flash(f"Deleted {user['username']} and signed out their PCs.", "ok")
    return redirect(url_for("index") + "#users")


@app.route("/devices/<int:tid>/delete", methods=["POST"])
@page_login_required()
def page_device_delete(tid):
    conn = get_db()
    row = conn.execute("SELECT * FROM api_tokens WHERE id = ?", (tid,)).fetchone() or abort(404)
    if not g.user["is_admin"] and row["user_id"] != g.user["id"]:
        abort(403)
    conn.execute("DELETE FROM api_tokens WHERE id = ?", (tid,))
    conn.commit()
    flash(f"Signed out {row['device_name']}.", "ok")
    return redirect(url_for("index") + "#devices")


# ---------------------------------------------------------------------------
# Terminal commands (no Key Server needed)
# ---------------------------------------------------------------------------

def _ask_password():
    while True:
        pw = getpass.getpass(f"Password (at least {MIN_PASSWORD} characters): ")
        if len(pw) < MIN_PASSWORD:
            print("Too short.")
        elif pw != getpass.getpass("Same password again: "):
            print("They don't match.")
        else:
            return pw


def cli(argv):
    conn = db.connect()
    cmd = argv[0]
    if cmd == "has-users":                      # used by run.sh: exit 0 if any user exists
        n = conn.execute("SELECT COUNT(*) FROM users").fetchone()[0]
        return 0 if n else 1
    if cmd == "list-users":
        for r in conn.execute("SELECT * FROM users ORDER BY username"):
            print(f"{r['username']}{'  (admin)' if r['is_admin'] else ''}")
        return 0
    if cmd in ("add-user", "create-admin"):
        username = input("New username: ").strip()
        if not username or find_user(conn, username):
            print("Empty or already taken.")
            return 1
        is_admin = cmd == "create-admin" or input("Admin? [y/N] ").strip().lower() == "y"
        conn.execute("INSERT INTO users (username, password_hash, is_admin, created_at, updated_at) "
                     "VALUES (?, ?, ?, ?, ?)",
                     (username, generate_password_hash(_ask_password()), int(is_admin), db.now(), db.now()))
        conn.commit()
        print(f"Added {username}{' (admin)' if is_admin else ''}.")
        return 0
    if cmd == "set-password":
        user = find_user(conn, input("Username: ").strip())
        if not user:
            print("No such user.")
            return 1
        conn.execute("UPDATE users SET password_hash = ?, updated_at = ? WHERE id = ?",
                     (generate_password_hash(_ask_password()), db.now(), user["id"]))
        conn.execute("DELETE FROM api_tokens WHERE user_id = ?", (user["id"],))
        conn.commit()
        print("Password changed; that user must sign in again on every PC.")
        return 0
    print("Commands: add-user, create-admin, set-password, list-users")
    return 2


if __name__ == "__main__":
    if len(sys.argv) > 1:
        sys.exit(cli(sys.argv[1:]))
    DEV_MODE = True
    app.config["SESSION_COOKIE_SECURE"] = False
    app.run(host="127.0.0.1", port=PORT, debug=False)
