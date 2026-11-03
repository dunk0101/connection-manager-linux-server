"""
Connection Manager Server - database helpers.

Everything lives in one SQLite file, code/data/connections.db.

Tables
------
users        people who may sign in: username, password hash (scrypt), admin flag
api_tokens   one row per signed-in PC (device). Only a SHA-256 hash of each
             token is stored, never the token itself. Deleting a row signs
             that PC out.
connections  the shared connection list: name, address (IP / URL / RustDesk ID),
             location (the group), username, the password ENCRYPTED with the
             Key Server key (keys.encrypt), type and the order inside its group.
settings     key/value store: "revision" (goes up by one on every change to the
             connection list, so clients can cheaply ask "has anything
             changed?"), login lockouts, last backup date.

The JSON shape sent to the clients (connection_dict) uses the same field names
as the Windows/Linux client's Connection model (camelCase).
"""

import os
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


def init():
    conn = connect()
    conn.executescript(SCHEMA)
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
