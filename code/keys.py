"""
Connection Manager Server - keys fetched from the Key Server.

The server keeps no key on its own disk. At start-up it asks the Key Server
for two secrets and keeps them in this process's memory only:

  cm_vault_key     encrypts every saved connection password in the database
  cm_flask_secret  signs the admin website's login cookie

If the Key Server is sealed, unreachable, or hasn't approved this app yet,
load_keys() WAITS (retrying every 10 s, up to max_wait). Under gunicorn the
worker timeout (--timeout 120 in the service file) must be larger than
max_wait. If the wait runs out the worker exits and gunicorn/systemd start it
again, which simply retries.

The only file the client writes is code/data/keyserver_identity.json, which
identifies this app and unlocks nothing on its own.

Built the same way as Address Book's keys.py.
"""

from __future__ import annotations

import base64
import hashlib
import os
from typing import Optional

from cryptography.fernet import Fernet

from keyserver_client import KeyServerClient, harden_memory

KEYSERVER_CONFIG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "config-files", "keyserver.json")

VAULT_KEY_NAME = "cm_vault_key"        # any random value, e.g. "Random 32 bytes as hex"
FLASK_SECRET_NAME = "cm_flask_secret"  # any random value

_client: Optional[KeyServerClient] = None
_fernet: Optional[Fernet] = None

# Testing only: CONNMGR_TEST_KEYS=1 uses made-up keys and never contacts the
# Key Server. It is refused unless CONNMGR_DATA_DIR points at a test copy of
# the data, so the real data can never be encrypted with a test key.
TEST_MODE = os.environ.get("CONNMGR_TEST_KEYS") == "1"
if TEST_MODE and not os.environ.get("CONNMGR_DATA_DIR"):
    raise SystemExit("CONNMGR_TEST_KEYS=1 is only allowed with CONNMGR_DATA_DIR set to a test copy.")
_TEST_SECRETS = {VAULT_KEY_NAME: "test-vault-key", FLASK_SECRET_NAME: "test-flask-secret"}


def _secret(name: str) -> str:
    if TEST_MODE:
        return _TEST_SECRETS[name]
    return keyserver().require(name)[name]


def keyserver() -> KeyServerClient:
    global _client
    if _client is None:
        harden_memory()                      # no core dumps of the process holding the keys
        _client = KeyServerClient.from_config(KEYSERVER_CONFIG)
    return _client


def load_keys(max_wait: Optional[float] = 100) -> None:
    """Fetch this app's secrets once at start-up."""
    if TEST_MODE:
        return
    ks = keyserver()
    ks.fetch(wait=True, max_wait=max_wait)
    ks.require(VAULT_KEY_NAME, FLASK_SECRET_NAME)   # clear error if a secret isn't ticked


def fernet() -> Fernet:
    """The password-encryption key. The Key Server value can be any text; it
    is turned into a 32-byte Fernet key (AES + HMAC) with SHA-256."""
    global _fernet
    if _fernet is None:
        raw = _secret(VAULT_KEY_NAME).encode("utf-8")
        _fernet = Fernet(base64.urlsafe_b64encode(hashlib.sha256(raw).digest()))
    return _fernet


def encrypt(plain: str) -> str:
    """Encrypt one connection password ('' stays '')."""
    if not plain:
        return ""
    return fernet().encrypt(plain.encode("utf-8")).decode("ascii")


def decrypt(token: str) -> str:
    if not token:
        return ""
    return fernet().decrypt(token.encode("ascii")).decode("utf-8")


def flask_secret() -> bytes:
    return _secret(FLASK_SECRET_NAME).encode("utf-8")
