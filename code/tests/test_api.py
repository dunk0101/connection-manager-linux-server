"""Tests for the Connection Manager Server API.

Run from the code/ folder:   .venv/bin/python -m pytest -q tests
Uses made-up keys (CONNMGR_TEST_KEYS=1) and a scratch data folder, so the
real database and the Key Server are never touched.
"""

import importlib
import json
import os
import sqlite3
import sys
import tempfile

import pytest

CODE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


@pytest.fixture()
def env(monkeypatch):
    tmp = tempfile.mkdtemp(prefix="cm-test-")
    monkeypatch.setenv("CONNMGR_TEST_KEYS", "1")
    monkeypatch.setenv("CONNMGR_DATA_DIR", os.path.join(tmp, "data"))
    monkeypatch.setenv("CONNMGR_DOWNLOAD_DIR", os.path.join(tmp, "downloads"))
    monkeypatch.setenv("CONNMGR_DEV", "1")
    sys.path.insert(0, CODE)
    for m in ("app", "db", "keys"):
        sys.modules.pop(m, None)
    app_mod = importlib.import_module("app")
    from werkzeug.security import generate_password_hash
    conn = app_mod.db.connect()
    for name, admin in (("admin", 1), ("bob", 0)):
        conn.execute("INSERT INTO users (username, password_hash, is_admin, created_at, updated_at) "
                     "VALUES (?, ?, ?, '', '')", (name, generate_password_hash("password123"), admin))
    conn.commit()
    conn.close()
    return app_mod, tmp


def sign_in(client, user="bob"):
    r = client.post("/api/v1/auth/token", json={"username": user, "password": "password123",
                                                "device_name": "PC1", "platform": "linux"})
    assert r.status_code == 200, r.json
    return {"Authorization": "Bearer " + r.json["token"]}


def test_health_and_auth(env):
    app_mod, _ = env
    c = app_mod.app.test_client()
    assert c.get("/api/v1/health").json["version"]
    assert c.get("/api/v1/connections").status_code == 401
    assert c.post("/api/v1/auth/token", json={"username": "bob", "password": "nope"}).status_code == 401
    h = sign_in(c)
    assert c.get("/api/v1/auth/me", headers=h).json["username"] == "bob"
    assert c.delete("/api/v1/auth/token", headers=h).json["ok"]
    assert c.get("/api/v1/auth/me", headers=h).status_code == 401


def test_lockout(env):
    app_mod, _ = env
    c = app_mod.app.test_client()
    for _ in range(5):
        c.post("/api/v1/auth/token", json={"username": "bob", "password": "bad"})
    r = c.post("/api/v1/auth/token", json={"username": "bob", "password": "password123"})
    assert r.status_code == 429


def test_crud_order_copy_secret(env):
    app_mod, tmp = env
    c = app_mod.app.test_client()
    h = sign_in(c)
    rev0 = c.get("/api/v1/revision", headers=h).json["revision"]

    assert c.post("/api/v1/connections", headers=h, json={"name": "", "ipAddress": "x", "location": "L"}).status_code == 400
    a = c.post("/api/v1/connections", headers=h, json={
        "name": "Server A", "ipAddress": "10.0.0.1", "location": "Office", "username": "admin",
        "password": "s3cret!", "connectionType": "RDP"}).json["connection"]
    b = c.post("/api/v1/connections", headers=h, json={
        "name": "Router", "ipAddress": "https://10.0.0.254", "location": "Office", "connectionType": "Web"}).json["connection"]
    assert (a["sortOrder"], b["sortOrder"]) == (0, 1)
    assert a["hasPassword"] and not b["hasPassword"]
    assert "password" not in json.dumps(c.get("/api/v1/connections", headers=h).json).replace("hasPassword", "")
    assert c.get("/api/v1/revision", headers=h).json["revision"] == rev0 + 2

    # Password is encrypted in the database, and only /secret returns it.
    raw = sqlite3.connect(os.path.join(tmp, "data", "connections.db")).execute(
        "SELECT password_enc FROM connections WHERE id = ?", (a["id"],)).fetchone()[0]
    assert raw and "s3cret" not in raw
    assert c.get(f"/api/v1/connections/{a['id']}/secret", headers=h).json["password"] == "s3cret!"

    # Edit with a blank password keeps it; moving location puts it at the end of the new group.
    r = c.put(f"/api/v1/connections/{a['id']}", headers=h, json={"name": "Server A2", "password": "",
                                                                  "location": "Home"}).json["connection"]
    assert r["name"] == "Server A2" and r["location"] == "Home" and r["sortOrder"] == 0
    assert c.get(f"/api/v1/connections/{a['id']}/secret", headers=h).json["password"] == "s3cret!"

    # Copy keeps the password and goes to the end of its group.
    cp = c.post(f"/api/v1/connections/{a['id']}/copy", headers=h).json["connection"]
    assert cp["name"] == "Server A2 (Copy)" and cp["sortOrder"] == 1
    assert c.get(f"/api/v1/connections/{cp['id']}/secret", headers=h).json["password"] == "s3cret!"

    # Reorder.
    c.post("/api/v1/connections/order", headers=h, json={"ids": [cp["id"], a["id"]]})
    home = [x for x in c.get("/api/v1/connections", headers=h).json["connections"] if x["location"] == "Home"]
    assert [x["id"] for x in home] == [cp["id"], a["id"]]

    # Delete.
    assert c.delete(f"/api/v1/connections/{cp['id']}", headers=h).json["ok"]
    assert c.get(f"/api/v1/connections/{cp['id']}/secret", headers=h).status_code == 404


def test_import_skips_duplicates(env):
    app_mod, _ = env
    c = app_mod.app.test_client()
    h = sign_in(c)
    c.post("/api/v1/connections", headers=h, json={"name": "A", "ipAddress": "10.0.0.1", "location": "X"})
    r = c.post("/api/v1/connections/import", headers=h, json={"connections": [
        {"name": "A again", "ipAddress": "10.0.0.1", "location": "X"},
        {"name": "B", "ipAddress": "10.0.0.2", "location": "X", "password": "pw", "connectionType": "Putty"},
        {"name": "B dup in file", "ipAddress": "10.0.0.2", "location": "X"},
    ]}).json
    assert (r["added"], r["skipped"]) == (1, 2)


def test_version_and_download(env):
    app_mod, tmp = env
    c = app_mod.app.test_client()
    h = sign_in(c)
    assert c.get("/api/v1/version", headers=h).json["clients"] == {}
    d = os.path.join(tmp, "downloads", "linux")
    os.makedirs(d)
    with open(os.path.join(d, "cm.tar.gz"), "wb") as f:
        f.write(b"hello")
    with open(os.path.join(d, "manifest.json"), "w") as f:
        json.dump({"version": "v1.0.1", "file": "cm.tar.gz", "sha256": "abc", "size": 5}, f)
    v = c.get("/api/v1/version", headers=h).json
    assert v["clients"]["linux"]["version"] == "v1.0.1"
    assert c.get("/api/v1/downloads/linux", headers=h).data == b"hello"
    assert c.get("/api/v1/downloads/linux").status_code == 401
    # v16.0.1: the .deb is offered separately ("linux-deb"); "linux" (tarball) stays for older clients.
    d = os.path.join(tmp, "downloads", "linux-deb")
    os.makedirs(d)
    with open(os.path.join(d, "connection-manager_16.0.1_amd64.deb"), "wb") as f:
        f.write(b"deb!")
    with open(os.path.join(d, "manifest.json"), "w") as f:
        json.dump({"version": "v16.0.1", "file": "connection-manager_16.0.1_amd64.deb", "sha256": "x", "size": 4}, f)
    v = c.get("/api/v1/version", headers=h).json
    assert v["clients"]["linux-deb"]["url"] == "/api/v1/downloads/linux-deb"
    assert v["clients"]["linux"]["version"] == "v1.0.1"
    assert c.get("/api/v1/downloads/linux-deb", headers=h).data == b"deb!"


def test_auto_update_switch(env):
    app_mod, _ = env
    c = app_mod.app.test_client()
    h = sign_in(c)
    assert c.get("/api/v1/version", headers=h).json["auto_update"] is True   # default on
    conn = app_mod.db.connect()
    app_mod.db.set_setting(conn, "auto_update", "0")
    conn.commit()
    conn.close()
    assert c.get("/api/v1/version", headers=h).json["auto_update"] is False
    # Website switch (admin only): ticking sends "0" and "1", unticking only "0".
    with c.session_transaction() as s:
        s["csrf"] = "t"
    c.post("/login", data={"username": "admin", "password": "password123", "csrf": "t"})
    with c.session_transaction() as s:
        s["csrf"] = "t"
    c.post("/settings/auto-update", data={"csrf": "t", "auto_update": ["0", "1"]})
    assert c.get("/api/v1/version", headers=h).json["auto_update"] is True
    c.post("/settings/auto-update", data={"csrf": "t", "auto_update": "0"})
    assert c.get("/api/v1/version", headers=h).json["auto_update"] is False
    assert b"Automatically update Connection Manager on every PC" in c.get("/").data


def _sign_in_device(c, user, name, device_id):
    r = c.post("/api/v1/auth/token", json={"username": user, "password": "password123", "device_name": name,
                                           "platform": "windows", "device_id": device_id})
    assert r.status_code == 200, r.json
    return {"Authorization": "Bearer " + r.json["token"]}, r.json["device"]


def _website_login(c, user):
    with c.session_transaction() as s:
        s["csrf"] = "t"
    c.post("/login", data={"username": user, "password": "password123", "csrf": "t"})
    with c.session_transaction() as s:
        s["csrf"] = "t"


def test_device_badges_unique_and_kept(env):
    app_mod, _ = env
    c = app_mod.app.test_client()
    devices = [_sign_in_device(c, "bob", "SAME-NAME", f"id-{i}")[1] for i in range(10)]
    symbols = [d["symbol"] for d in devices]
    assert len(set(symbols)) == 10                           # every PC has its own badge
    assert all(s in app_mod.db.ALL_SYMBOLS for s in symbols)
    # Sign out and in again on the same PC: same badge back.
    h, first = _sign_in_device(c, "bob", "PC-A", "keep-me")
    c.delete("/api/v1/auth/token", headers=h)
    _, again = _sign_in_device(c, "bob", "PC-A", "keep-me")
    assert again["symbol"] == first["symbol"]


def test_device_rename_api_and_revision(env):
    app_mod, _ = env
    c = app_mod.app.test_client()
    h, dev = _sign_in_device(c, "bob", "DUNCAN-MIDDLEMU", "dev-1")
    assert dev["name"] == "DUNCAN-MIDDLEMU"
    r = c.put("/api/v1/device", headers=h, json={"name": "  duncan-middlemuir-pc "})
    assert r.status_code == 200 and r.json["name"] == "duncan-middlemuir-pc"
    assert r.json["symbol"] == dev["symbol"]
    for bad in ("", "   ", "x" * 61, "<script>", "a/b"):
        assert c.put("/api/v1/device", headers=h, json={"name": bad}).status_code == 400
    rev = c.get("/api/v1/revision", headers=h).json
    assert rev["device"] == {"name": "duncan-middlemuir-pc", "symbol": dev["symbol"]}
    assert c.get("/api/v1/auth/me", headers=h).json["device"]["name"] == "duncan-middlemuir-pc"


def test_old_pc_records_device_id(env):
    app_mod, _ = env
    c = app_mod.app.test_client()
    h = sign_in(c)                                           # an older client: no device_id
    sym = c.get("/api/v1/revision", headers=h).json["device"]["symbol"]
    c.get("/api/v1/revision", headers={**h, "X-Device-Id": "later-id"})
    c.delete("/api/v1/auth/token", headers=h)
    _, again = _sign_in_device(c, "bob", "PC1", "later-id")
    assert again["symbol"] == sym


def test_device_rename_website(env):
    app_mod, _ = env
    c = app_mod.app.test_client()
    h_admin, _ = _sign_in_device(c, "admin", "ADMIN-PC", "a1")
    h_bob, _ = _sign_in_device(c, "bob", "BOB-PC", "b1")
    conn = app_mod.db.connect()
    ids = {r["device_name"]: r["id"] for r in conn.execute("SELECT id, device_name FROM api_tokens")}
    conn.close()
    # bob may rename his own PC, not the admin's.
    _website_login(c, "bob")
    c.post(f"/devices/{ids['BOB-PC']}/rename", data={"csrf": "t", "name": "Bob laptop"})
    assert c.get("/api/v1/revision", headers=h_bob).json["device"]["name"] == "Bob laptop"
    assert c.post(f"/devices/{ids['ADMIN-PC']}/rename", data={"csrf": "t", "name": "x"}).status_code == 403
    page = c.get("/").data
    assert b"Bob laptop" in page and b"pc-symbol" in page
    # The admin may rename any PC; a bad name changes nothing.
    c.post("/logout")
    _website_login(c, "admin")
    c.post(f"/devices/{ids['BOB-PC']}/rename", data={"csrf": "t", "name": "Front desk (2)"})
    assert c.get("/api/v1/revision", headers=h_bob).json["device"]["name"] == "Front desk (2)"
    c.post(f"/devices/{ids['BOB-PC']}/rename", data={"csrf": "t", "name": "<b>"})
    assert c.get("/api/v1/revision", headers=h_bob).json["device"]["name"] == "Front desk (2)"


def test_migration_old_database(env):
    app_mod, tmp = env
    old = os.path.join(tmp, "old.db")
    conn = sqlite3.connect(old)
    conn.row_factory = sqlite3.Row
    conn.execute("CREATE TABLE api_tokens (id INTEGER PRIMARY KEY, user_id INTEGER, token_hash TEXT, "
                 "device_name TEXT, platform TEXT, client_version TEXT, created_at TEXT, last_used_at TEXT)")
    conn.execute("CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)")
    conn.execute("INSERT INTO api_tokens VALUES (1, 1, 'h1', 'OLD-PC', 'windows', 'v16.0.7', '', '')")
    conn.execute("INSERT INTO api_tokens VALUES (2, 1, 'h2', 'OLD-PC', 'windows', 'v16.0.7', '', '')")
    app_mod.db.migrate(conn)
    assert app_mod.db.ensure_symbols(conn)
    syms = [r["symbol"] for r in conn.execute("SELECT symbol FROM api_tokens")]
    assert len(set(syms)) == 2 and all(syms)
    assert not app_mod.db.ensure_symbols(conn)              # nothing left to do
    conn.close()


def test_website_admin(env):
    app_mod, _ = env
    c = app_mod.app.test_client()
    assert c.get("/").status_code == 302
    c.get("/login")
    with c.session_transaction() as s:
        csrf = s["csrf"]
    r = c.post("/login", data={"username": "admin", "password": "password123", "csrf": csrf})
    assert r.status_code == 302
    page = c.get("/").data.decode()
    assert "Users" in page and "bob" in page
    with c.session_transaction() as s:
        csrf = s["csrf"]
    c.post("/users", data={"username": "carol", "password": "longenough", "csrf": csrf})
    assert "carol" in c.get("/").data.decode()
