"""
Connection Manager Server - database helpers.

Everything lives in one SQLite file, code/data/connections.db.

Tables
------
users        people who may sign in: username, password hash (scrypt), admin flag
api_tokens   one row per signed-in PC (device). Only a SHA-256 hash of each
             token is stored, never the token itself. Deleting a row signs
             that PC out. v16.0.8 added device_id (a GUID the client keeps in
             its settings.json, so a PC is recognised after signing in again)
             and symbol (its badge, "shape:colour", unique among signed-in PCs).
             device_name can be changed from the website or the PC's Settings.
connections  the shared connection list: name, address (IP / URL / RustDesk ID),
             location (the group), username, the password ENCRYPTED with the
             Key Server key (keys.encrypt), type and the order inside its group.
settings     key/value store: "device_symbol:<device_id>" (v16.0.8, the badge a
             PC keeps after signing out and in again), "revision" (goes up by one on every change to the
             connection list, so clients can cheaply ask "has anything
             changed?"), login lockouts, last backup date, "auto_update"
             (v16.0.7: "1"/"0", the website switch for automatic client updates;
             missing = on).

The JSON shape sent to the clients (connection_dict) uses the same field names
as the Windows/Linux client's Connection model (camelCase).
"""

import hashlib
import os
import re
import sqlite3
from datetime import datetime, timezone

CODE_DIR = os.path.dirname(os.path.abspath(__file__))      # the code/ folder
PROJECT_DIR = os.path.dirname(CODE_DIR)                     # connection-manager-server-vX.Y.Z
# CONNMGR_DATA_DIR lets a test copy of the data be used instead.
DATA_DIR = os.environ.get("CONNMGR_DATA_DIR") or os.path.join(CODE_DIR, "data")
DB_PATH = os.path.join(DATA_DIR, "connections.db")

CONNECTION_TYPES = ("RDP", "Web", "Putty", "Ubuntu", "RustDesk")

SCHEMA = """
CREATE TABLE IF NOT EXISTS users (
    id INTEGER PRIMARY KEY,
    username TEXT NOT NULL UNIQUE COLLATE NOCASE,
    password_hash TEXT NOT NULL,
    is_admin INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS api_tokens (
    id INTEGER PRIMARY KEY,
    user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash TEXT NOT NULL UNIQUE,
    device_name TEXT NOT NULL DEFAULT '',
    platform TEXT NOT NULL DEFAULT '',
    client_version TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL,
    last_used_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS connections (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    address TEXT NOT NULL,
    location TEXT NOT NULL,
    username TEXT NOT NULL DEFAULT '',
    password_enc TEXT NOT NULL DEFAULT '',
    connection_type TEXT NOT NULL DEFAULT 'RDP',
    sort_order INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    updated_by TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
"""


def now():
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def connect():
    os.makedirs(DATA_DIR, mode=0o700, exist_ok=True)
    conn = sqlite3.connect(DB_PATH, timeout=15)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA foreign_keys = ON")
    conn.execute("PRAGMA journal_mode = WAL")
    return conn


# Columns added after the first release: (table, column, definition).
MIGRATIONS = (
    ("api_tokens", "device_id", "TEXT NOT NULL DEFAULT ''"),     # v16.0.8
    ("api_tokens", "symbol", "TEXT NOT NULL DEFAULT ''"),        # v16.0.8
)


def migrate(conn):
    for table, column, definition in MIGRATIONS:
        have = {r["name"] for r in conn.execute(f"PRAGMA table_info({table})")}
        if column not in have:
            conn.execute(f"ALTER TABLE {table} ADD COLUMN {column} {definition}")


def init():
    conn = connect()
    conn.executescript(SCHEMA)
    migrate(conn)
    conn.commit()
    conn.close()
    try:
        os.chmod(DB_PATH, 0o600)
    except OSError:
        pass


def get_setting(conn, key, default=""):
    row = conn.execute("SELECT value FROM settings WHERE key = ?", (key,)).fetchone()
    return row["value"] if row else default


def set_setting(conn, key, value):
    conn.execute("INSERT INTO settings (key, value) VALUES (?, ?) "
                 "ON CONFLICT(key) DO UPDATE SET value = excluded.value", (key, str(value)))


def auto_update(conn):
    """Website switch: do Connection Manager clients update themselves? (default on)"""
    return get_setting(conn, "auto_update", "1") == "1"


# ---------------------------------------------------------------------------
# PC badges (v16.0.8): a white shape on a coloured circle, "shape:colour".
# The same paths (24 x 24 box) are drawn by the website (SVG) and the clients
# (Avalonia Path) - keep them in step with the clients' Models/DeviceSymbol.cs.
# ---------------------------------------------------------------------------

SYMBOL_COLOURS = {
    "blue": "#0070C0", "teal": "#0F8B8D", "green": "#2E7D32", "amber": "#B7791F",
    "orange": "#DD6B20", "red": "#C53030", "purple": "#6B46C1", "slate": "#475569",
}
SYMBOL_SHAPES = {
    "circle": "M12 7 A5 5 0 1 1 11.99 7 Z",
    "square": "M7.5 7.5 H16.5 V16.5 H7.5 Z",
    "triangle": "M12 6.5 L18 17 H6 Z",
    "diamond": "M12 5.8 L18.2 12 L12 18.2 L5.8 12 Z",
    "star": "M12 6 L13.65 10.33 L18.28 10.56 L14.66 13.47 L15.88 17.94 L12 15.4 "
            "L8.12 17.94 L9.34 13.47 L5.72 10.56 L10.35 10.33 Z",
    "hexagon": "M18 12 L15 17.2 H9 L6 12 L9 6.8 H15 Z",
}
_COLOURS = list(SYMBOL_COLOURS)
_SHAPES = list(SYMBOL_SHAPES)
# All 48 combinations; neighbours always differ in colour and shape.
ALL_SYMBOLS = [f"{_SHAPES[(k // 8 + k % 8) % 6]}:{_COLOURS[k % 8]}" for k in range(48)]


def symbol_parts(symbol):
    """(colour hex, SVG path) for a symbol; unknown values fall back to a blue circle."""
    shape, _, colour = (symbol or "").partition(":")
    return SYMBOL_COLOURS.get(colour, SYMBOL_COLOURS["blue"]), SYMBOL_SHAPES.get(shape, SYMBOL_SHAPES["circle"])


def assign_symbol(conn, token_id, device_id=""):
    """Give a signed-in PC a badge no other signed-in PC has. A PC that signs in again
    (same device_id) gets its old badge back if it is still free. Saved on the row."""
    used = {r["symbol"] for r in conn.execute(
        "SELECT symbol FROM api_tokens WHERE id != ? AND symbol != '' AND (? = '' OR device_id != ?)",
        (token_id, device_id, device_id))}
    remembered = get_setting(conn, "device_symbol:" + device_id) if device_id else ""
    if remembered in ALL_SYMBOLS and remembered not in used:
        symbol = remembered
    else:
        seed = device_id or f"token-{token_id}"
        start = int(hashlib.sha256(seed.encode()).hexdigest(), 16) % len(ALL_SYMBOLS)
        free = [ALL_SYMBOLS[(start + i) % len(ALL_SYMBOLS)] for i in range(len(ALL_SYMBOLS))]
        symbol = next((x for x in free if x not in used), ALL_SYMBOLS[start])
    conn.execute("UPDATE api_tokens SET symbol = ? WHERE id = ?", (symbol, token_id))
    if device_id:
        set_setting(conn, "device_symbol:" + device_id, symbol)
    return symbol


def ensure_symbols(conn):
    """PCs signed in before v16.0.8 have no badge yet: give them one. Returns True if any changed."""
    rows = conn.execute("SELECT id, device_id FROM api_tokens WHERE symbol = '' ORDER BY id").fetchall()
    for r in rows:
        assign_symbol(conn, r["id"], r["device_id"])
    return bool(rows)


DEVICE_NAME_MAX = 60
_DEVICE_NAME_RE = re.compile(r"[\w .()'\-]+")


def clean_device_name(name):
    """The PC name as it will be saved, or None if it is not allowed."""
    name = " ".join(str(name or "").split())
    if 1 <= len(name) <= DEVICE_NAME_MAX and _DEVICE_NAME_RE.fullmatch(name):
        return name
    return None


DEVICE_NAME_RULE = (f"A PC name is 1-{DEVICE_NAME_MAX} characters: letters, numbers, spaces "
                    "and . _ - ( ) '")


def device_dict(row):
    return {"name": row["device_name"], "symbol": row["symbol"]}


def revision(conn):
    return int(get_setting(conn, "revision", "0"))


def bump_revision(conn):
    """Call after every change to the connection list (before commit)."""
    rev = revision(conn) + 1
    set_setting(conn, "revision", rev)
    return rev


def connection_dict(row):
    return {
        "id": row["id"],
        "name": row["name"],
        "ipAddress": row["address"],
        "location": row["location"],
        "username": row["username"],
        "hasPassword": bool(row["password_enc"]),
        "connectionType": row["connection_type"],
        "sortOrder": row["sort_order"],
        "dateCreated": row["created_at"],
        "updatedAt": row["updated_at"],
        "updatedBy": row["updated_by"],
    }


def next_sort_order(conn, location):
    """New connections go to the END of their group (as in the old program)."""
    row = conn.execute("SELECT COUNT(*) AS n FROM connections WHERE location = ? COLLATE NOCASE",
                       (location,)).fetchone()
    return int(row["n"])
