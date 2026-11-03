"""keyserver_client.py - fetch secrets from your Key Server.

Copy this ONE file into any Python program (it only needs the `cryptography`
package, which the Password Manager and Asset Manager already use).

    from keyserver_client import KeyServerClient

    ks = KeyServerClient.from_config("config-files/keyserver.json")
    secrets = ks.fetch(wait=True)          # waits while pending approval / sealed
    vault_key = secrets["pm_vault_key"]

KEYS ARE ONLY EVER KEPT IN MEMORY
---------------------------------
This module never writes a secret to disk: no cache file, no fallback copy,
no log line containing a value. Every time your program starts, it asks the
key server again. The ONLY file it writes is the app's *identity file* (by
default next to keyserver.json, mode 0600). That file contains:

  * the app's private signing key - proves to the key server which app is
    asking. On its own it unlocks nothing: the admin must have approved the
    app, and can revoke it at any time from the key server's web page.
  * the key server's public key, pinned the first time the app connects, so
    nobody can pretend to be your key server later.

If the identity file is deleted, the app simply appears as a NEW pending app.

HOW THE CONNECTION IS PROTECTED
-------------------------------
  1. HTTPS (with the key server's certificate pinned by fingerprint when it
     is self-signed), or Tailscale's encrypted tunnel.
  2. Every request is signed with the app's Ed25519 key, time-stamped and
     given a one-off nonce, so it can't be forged or replayed.
  3. The secrets in the reply are encrypted to a one-off X25519 key that
     exists only in this process's memory for that one request.
  4. The reply is signed by the key server, and the signature is checked
     against the pinned server key before anything is decrypted.

keyserver.json
--------------
    {
      "url": "https://100.115.36.45:5577",
      "app_name": "Password Manager",
      "app_version": "v1.0.7",
      "tls_fingerprint": "<from Key Server Settings, only for self-signed TLS>",
      "server_fingerprint": "<from Key Server Settings - recommended>",
      "identity_file": "../data/keyserver_identity.json",
      "enroll_code": "<optional one-time code from Apps -> Enrolment codes>"
    }

Command line test (never prints secret values):
    python keyserver_client.py --config path/to/keyserver.json --test
"""

from __future__ import annotations

import base64
import hashlib
import http.client
import json
import logging
import os
import secrets as _secrets
import socket
import ssl
import sys
import time
import urllib.parse

from cryptography.exceptions import InvalidSignature, InvalidTag
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ed25519, x25519
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

__version__ = "1.0.0"
PROTOCOL = "keyserver-v1"
HKDF_INFO = b"keyserver-v1 response"

log = logging.getLogger("keyserver_client")


# --- errors -------------------------------------------------------------------

class KeyServerError(Exception):
    """Base class. `.status` is the key server's status word."""

    def __init__(self, message: str, status: str = "error"):
        super().__init__(message)
        self.status = status


class PendingApproval(KeyServerError):
    """The app is waiting for the admin to approve it on the key server."""


class Sealed(KeyServerError):
    """The key server is locked (e.g. just rebooted) - the admin must unlock it."""


class Revoked(KeyServerError):
    """The admin revoked or rejected this app."""


class Unreachable(KeyServerError):
    """Could not connect to the key server."""


class NotTrusted(KeyServerError):
    """The server's certificate or signing key doesn't match what was pinned."""


# --- helpers ------------------------------------------------------------------

def _b64e(data: bytes) -> str:
    return base64.b64encode(data).decode("ascii")


def _b64d(text: str) -> bytes:
    return base64.b64decode(text.encode("ascii"), validate=True)


def _sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def fingerprint(public_key_raw: bytes) -> str:
    h = hashlib.sha256(public_key_raw).hexdigest()[:16].upper()
    return "-".join(h[i:i + 4] for i in range(0, 16, 4))


def _norm_fp(fp: str) -> str:
    return (fp or "").replace(":", "").replace("-", "").replace(" ", "").lower()


def harden_memory() -> None:
    """Optional: stop this process's memory (which holds your secrets) being
    written to disk in a crash dump, or read by other programs of the same
    user. Linux only; harmless elsewhere. Call it early in your program.
    (Also make sure the machine's swap is encrypted, or turned off.)"""
    try:
        import resource
        resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
    except (ImportError, ValueError, OSError):
        pass
    if sys.platform.startswith("linux"):
        try:
            import ctypes
            ctypes.CDLL(None).prctl(4, 0, 0, 0, 0)   # PR_SET_DUMPABLE = 0
        except (OSError, AttributeError):
            pass


# --- the client ---------------------------------------------------------------

class KeyServerClient:
    def __init__(self, url: str, app_name: str, identity_file: str, *, app_version: str = "",
                 tls_fingerprint: str | None = None, server_fingerprint: str | None = None,
                 enroll_code: str | None = None, ca_file: str | None = None, timeout: float = 10.0):
        parsed = urllib.parse.urlsplit(url.rstrip("/"))
        if parsed.scheme not in ("http", "https") or not parsed.hostname:
            raise ValueError(f"bad key server url {url!r}")
        self.url = url.rstrip("/")
        self._scheme = parsed.scheme
        self._host = parsed.hostname
        self._port = parsed.port or (443 if parsed.scheme == "https" else 80)
        self._base_path = parsed.path.rstrip("/")
        self.app_name = app_name
        self.app_version = app_version
        self.identity_file = identity_file
        self.tls_fingerprint = _norm_fp(tls_fingerprint or "")
        self.server_fingerprint = (server_fingerprint or "").upper().strip()
        self.enroll_code = enroll_code or ""
        self.ca_file = ca_file
        self.timeout = timeout
        if self._scheme == "http" and not self.server_fingerprint:
            log.warning("keyserver: plain http without 'server_fingerprint' - the server's identity is only "
                        "checked from the first connection onwards. Set server_fingerprint in keyserver.json.")
        self._identity = self._load_or_create_identity()
        self._signing_key = ed25519.Ed25519PrivateKey.from_private_bytes(_b64d(self._identity["private_key"]))
        self.public_key = _b64e(self._signing_key.public_key().public_bytes(
            serialization.Encoding.Raw, serialization.PublicFormat.Raw))
        self.fingerprint = fingerprint(_b64d(self.public_key))
        self._secrets: dict[str, str] = {}      # memory only - never written anywhere
        self._versions: dict[str, int] = {}
        self._previous: dict[str, str] = {}

    @classmethod
    def from_config(cls, path: str, **overrides) -> "KeyServerClient":
        with open(path, encoding="utf-8") as f:
            cfg = json.load(f)
        cfg.update(overrides)
        base = os.path.dirname(os.path.abspath(path))
        ident = cfg.get("identity_file") or "keyserver_identity.json"
        if not os.path.isabs(ident):
            ident = os.path.normpath(os.path.join(base, ident))
        ca = cfg.get("ca_file")
        if ca and not os.path.isabs(ca):
            ca = os.path.join(base, ca)
        return cls(cfg["url"], cfg["app_name"], ident, app_version=cfg.get("app_version", ""),
                   tls_fingerprint=cfg.get("tls_fingerprint"), server_fingerprint=cfg.get("server_fingerprint"),
                   enroll_code=cfg.get("enroll_code"), ca_file=ca, timeout=float(cfg.get("timeout", 10)))

    # --- identity file (holds NO secrets) ---

    def _load_or_create_identity(self) -> dict:
        try:
            with open(self.identity_file, encoding="utf-8") as f:
                ident = json.load(f)
            if "private_key" in ident:
                return ident
        except FileNotFoundError:
            pass
        key = ed25519.Ed25519PrivateKey.generate()
        ident = {"format": 1, "created": int(time.time()), "server_key": None,
                 "private_key": _b64e(key.private_bytes(serialization.Encoding.Raw, serialization.PrivateFormat.Raw,
                                                        serialization.NoEncryption()))}
        self._write_identity(ident, create=True)
        log.info("keyserver: created a new app identity at %s", self.identity_file)
        return ident

    def _write_identity(self, ident: dict, create: bool = False) -> None:
        os.makedirs(os.path.dirname(os.path.abspath(self.identity_file)), exist_ok=True)
        tmp = self.identity_file + ".tmp"
        fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            json.dump(ident, f, indent=2)
        os.chmod(tmp, 0o600)
        if create and os.path.exists(self.identity_file):
            os.unlink(tmp)       # another process created it first - keep theirs
            return
        os.replace(tmp, self.identity_file)

    def _check_server_key(self, server_key_b64: str | None) -> None:
        """Pin the server's signing key (trust on first use + optional configured fingerprint)."""
        if not server_key_b64:
            return
        fp = fingerprint(_b64d(server_key_b64))
        if self.server_fingerprint and fp != self.server_fingerprint:
            raise NotTrusted(f"key server identity {fp} does not match server_fingerprint "
                             f"{self.server_fingerprint} in keyserver.json", "not_trusted")
        pinned = self._identity.get("server_key")
        if pinned and pinned != server_key_b64:
            raise NotTrusted("the key server's signing key has CHANGED since this app first connected. If you "
                             "rebuilt the key server on purpose, delete this app's identity file and approve "
                             "it again; otherwise someone may be impersonating your key server.", "not_trusted")
        if not pinned:
            self._identity["server_key"] = server_key_b64
            self._write_identity(self._identity)
            log.info("keyserver: pinned key server identity %s", fp)

    # --- transport ---

    def _connection(self):
        if self._scheme == "http":
            return http.client.HTTPConnection(self._host, self._port, timeout=self.timeout)
        if self.tls_fingerprint:
            ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
            ctx.check_hostname = False
            ctx.verify_mode = ssl.CERT_NONE        # replaced by the fingerprint check below
        else:
            ctx = ssl.create_default_context(cafile=self.ca_file)
        ctx.minimum_version = ssl.TLSVersion.TLSv1_2
        return http.client.HTTPSConnection(self._host, self._port, timeout=self.timeout, context=ctx)

    def _request(self, path: str, payload: dict) -> tuple[int, dict, str]:
        full_path = self._base_path + "/api/v1" + path
        body = json.dumps(payload).encode("utf-8")
        ts = str(int(time.time()))
        nonce = _secrets.token_urlsafe(24)
        msg = f"POST\n{full_path}\n{ts}\n{nonce}\n{_sha256_hex(body)}".encode("utf-8")
        headers = {"Content-Type": "application/json", "X-KS-Key": self.public_key, "X-KS-Timestamp": ts,
                   "X-KS-Nonce": nonce, "X-KS-Signature": _b64e(self._signing_key.sign(msg)),
                   "User-Agent": f"keyserver-client/{__version__}"}
        status, raw = self._send(full_path, body, headers)
        try:
            data = json.loads(raw)
        except ValueError:
            raise KeyServerError(f"key server sent an unexpected reply (HTTP {status})")
        return status, data, nonce

    def _send(self, full_path: str, body: bytes, headers: dict) -> tuple[int, bytes]:
        conn = self._connection()
        try:
            try:
                conn.connect()
            except (OSError, socket.timeout) as exc:
                raise Unreachable(f"cannot reach key server at {self.url}: {exc}", "unreachable") from exc
            if self._scheme == "https" and self.tls_fingerprint:
                der = conn.sock.getpeercert(binary_form=True)
                if _sha256_hex(der) != self.tls_fingerprint:
                    raise NotTrusted("the key server's TLS certificate does not match tls_fingerprint in "
                                     "keyserver.json - refusing to send anything", "not_trusted")
            try:
                conn.request("POST", full_path, body=body, headers=headers)
                resp = conn.getresponse()
                return resp.status, resp.read(1024 * 1024)
            except (OSError, http.client.HTTPException, socket.timeout) as exc:
                raise Unreachable(f"key server connection failed: {exc}", "unreachable") from exc
        finally:
            conn.close()

    @staticmethod
    def _raise_for(data: dict) -> None:
        status = data.get("status", "error")
        text = data.get("error", status)
        if status == "pending":
            raise PendingApproval(text, status)
        if status == "sealed":
            raise Sealed(text, status)
        if status == "revoked":
            raise Revoked(text, status)
        raise KeyServerError(text, status)

    # --- public API ---

    def enroll(self) -> str:
        """Introduce this app to the key server. Returns 'pending' or 'approved'."""
        _, data, _ = self._request("/enroll", {"app_name": self.app_name, "hostname": socket.gethostname(),
                                               "app_version": self.app_version, "enroll_code": self.enroll_code})
        if data.get("status") not in ("pending", "approved"):
            self._raise_for(data)
        self._check_server_key(data.get("server_key"))
        if data["status"] == "pending":
            log.warning("keyserver: '%s' is waiting for approval on the key server. Its fingerprint is %s - "
                        "check this matches on the key server's Apps page before approving.",
                        self.app_name, self.fingerprint)
        return data["status"]

    def _fetch_once(self, include_previous: bool) -> dict[str, str]:
        if not self._identity.get("server_key"):
            self.enroll()
        reply_priv = x25519.X25519PrivateKey.generate()      # one-off, memory only
        reply_pub = reply_priv.public_key().public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw)
        payload = {"reply_key": _b64e(reply_pub), "include_previous": include_previous}
        _, data, nonce = self._request("/secrets", payload)
        if data.get("status") == "unknown":
            self.enroll()
            _, data, nonce = self._request("/secrets", payload)
        if data.get("status") != "ok":
            self._raise_for(data)

        box = data["box"]
        canonical = json.dumps(box, sort_keys=True, separators=(",", ":")).encode("utf-8")
        signed = f"{PROTOCOL}\n{nonce}\n{_sha256_hex(canonical)}".encode("utf-8")
        try:
            ed25519.Ed25519PublicKey.from_public_bytes(_b64d(self._identity["server_key"])).verify(
                _b64d(data["signature"]), signed)
        except (InvalidSignature, ValueError, KeyError, TypeError) as exc:
            raise NotTrusted("the reply was not signed by the pinned key server - ignoring it", "not_trusted") from exc
        try:
            shared = reply_priv.exchange(x25519.X25519PublicKey.from_public_bytes(_b64d(box["epk"])))
            key = HKDF(algorithm=hashes.SHA256(), length=32, salt=None, info=HKDF_INFO).derive(shared)
            plain = AESGCM(key).decrypt(_b64d(box["nonce"]), _b64d(box["ct"]), nonce.encode("utf-8"))
        except (InvalidTag, ValueError, KeyError) as exc:
            raise KeyServerError("could not decrypt the key server's reply", "bad_reply") from exc
        result = json.loads(plain)
        self._secrets = dict(result["secrets"])
        self._versions = dict(result.get("versions", {}))
        self._previous = dict(result.get("previous", {}))
        log.info("keyserver: received %d secret(s) for '%s': %s", len(self._secrets), self.app_name,
                 ", ".join(f"{k} v{self._versions.get(k, '?')}" for k in sorted(self._secrets)) or "none assigned")
        return dict(self._secrets)

    def fetch(self, wait: bool = False, max_wait: float | None = None, poll_seconds: float = 10.0,
              include_previous: bool = False) -> dict[str, str]:
        """Get this app's secrets as {name: value}. Kept in memory only.

        wait=True keeps retrying (every poll_seconds) while the app is pending
        approval, the key server is sealed, or it can't be reached - up to
        max_wait seconds (None = forever). Revoked / not-trusted errors are
        raised immediately."""
        start = time.monotonic()
        last_state = None
        while True:
            try:
                return self._fetch_once(include_previous)
            except (PendingApproval, Sealed, Unreachable) as exc:
                if not wait or (max_wait is not None and time.monotonic() - start + poll_seconds > max_wait):
                    raise
                if exc.status != last_state:
                    log.warning("keyserver: %s - retrying every %ss", exc, int(poll_seconds))
                    last_state = exc.status
                time.sleep(poll_seconds)

    def get(self, name: str, default: str | None = None) -> str | None:
        """One secret by name (fetches first if nothing has been fetched yet)."""
        if not self._secrets:
            self.fetch(wait=True)
        return self._secrets.get(name, default)

    def require(self, *names: str) -> dict[str, str]:
        """Like get() for several names, raising KeyError listing any the admin hasn't assigned."""
        if not self._secrets:
            self.fetch(wait=True)
        missing = [n for n in names if n not in self._secrets]
        if missing:
            raise KeyError(f"key server has not assigned these secrets to '{self.app_name}': {', '.join(missing)} "
                           f"- tick them on the app's page in the key server")
        return {n: self._secrets[n] for n in names}

    def previous(self, name: str) -> str | None:
        """The value before the latest rotation (only if fetched with include_previous=True)."""
        return self._previous.get(name)

    def version(self, name: str) -> int | None:
        return self._versions.get(name)

    def heartbeat(self) -> bool:
        """Tell the key server this app is still alive. Returns True if the server is sealed."""
        _, data, _ = self._request("/heartbeat", {"hostname": socket.gethostname(), "app_version": self.app_version})
        if data.get("status") != "ok":
            self._raise_for(data)
        return bool(data.get("sealed"))

    def forget(self) -> None:
        """Drop the secrets held by this client object."""
        self._secrets.clear()
        self._previous.clear()


# --- command line -----------------------------------------------------------

def _main(argv: list[str]) -> int:
    import argparse
    p = argparse.ArgumentParser(description="Key Server client test tool (never prints secret values).")
    p.add_argument("--config", default="keyserver.json", help="path to keyserver.json")
    p.add_argument("--test", action="store_true", help="enrol if needed and fetch secrets once")
    p.add_argument("--wait", action="store_true", help="with --test: wait while pending/sealed")
    p.add_argument("--fingerprint", action="store_true", help="print this app's fingerprint")
    a = p.parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")
    ks = KeyServerClient.from_config(a.config)
    print(f"App: {ks.app_name}\nFingerprint: {ks.fingerprint}\nKey server: {ks.url}")
    if a.fingerprint and not a.test:
        return 0
    try:
        secrets = ks.fetch(wait=a.wait)
    except PendingApproval:
        print("Status: PENDING - approve this app on the key server's Apps page (check the fingerprint above).")
        return 2
    except Sealed:
        print("Status: SEALED - log in to the key server and unlock it.")
        return 3
    except KeyServerError as exc:
        print(f"Status: {exc.status.upper()} - {exc}")
        return 1
    print(f"Status: OK - received {len(secrets)} secret(s):")
    for name in sorted(secrets):
        print(f"  {name}  (v{ks.version(name)}, {len(secrets[name])} characters)")
    return 0


if __name__ == "__main__":
    sys.exit(_main(sys.argv[1:]))
