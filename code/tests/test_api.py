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
