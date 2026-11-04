#!/usr/bin/env python3
"""gwsignup - the account sign-up site for the GodsWar 2.51 "reborn" server.

One page: pick a username, password and email, and the account is created in
reborn's PostgreSQL database exactly the way reborn's own first-login
registration creates it (PostgresAccountStore.TryCreateAccountWithCredentialAsync),
except that the email is filled in. The player then logs in with the game client.

Runs as a small WSGI app on 127.0.0.1 behind Caddy. Python stdlib + Debian's
python3-jinja2 and python3-psycopg2 only.

Settings come from the environment (see README.md); secrets come from files:
  $CREDENTIALS_DIRECTORY/db_password, $CREDENTIALS_DIRECTORY/secret_key   (systemd LoadCredential)
  or GWSIGNUP_DB_PASSWORD_FILE / GWSIGNUP_SECRET_KEY_FILE.
"""
import base64
import collections
import hashlib
import hmac
import ipaddress
import logging
import mimetypes
import os
import re
import secrets
import socketserver
import io
import tarfile
import sys
import threading
import time
from urllib.parse import parse_qs
from wsgiref.simple_server import WSGIRequestHandler, WSGIServer, make_server

import jinja2
import psycopg2
import psycopg2.pool

LOG = logging.getLogger("gwsignup")
HERE = os.path.dirname(os.path.abspath(__file__))


# --------------------------------------------------------------------------- settings

CLASS_NAMES = {0: "Warrior", 1: "Champion", 2: "Priest", 3: "Mage"}
CAMP_NAMES = {0: "Sparta", 1: "Athens"}


def env(name, default=""):
    return os.environ.get("GWSIGNUP_" + name, default)


def env_int(name, default):
    try:
        return int(env(name, str(default)))
    except ValueError:
        return default


def read_secret(cred_name, file_env):
    path = env(file_env)
    if not path and os.environ.get("CREDENTIALS_DIRECTORY"):
        path = os.path.join(os.environ["CREDENTIALS_DIRECTORY"], cred_name)
    if not path:
        sys.exit("missing secret %s (set CREDENTIALS_DIRECTORY or GWSIGNUP_%s)" % (cred_name, file_env))
    with open(path, "r", encoding="ascii") as f:
        value = f.read().strip()
    if not value:
        sys.exit("secret %s is empty" % cred_name)
    return value


class Config:
    def __init__(self):
        self.bind = env("BIND", "127.0.0.1")
        self.port = env_int("PORT", 8090)
        self.db = dict(host=env("DB_HOST", "127.0.0.1"), port=env_int("DB_PORT", 5432),
                       dbname=env("DB_NAME", "godswar"), user=env("DB_USER", "gw_signup"),
                       password=read_secret("db_password", "DB_PASSWORD_FILE"),
                       connect_timeout=5, application_name="gwsignup",
                       options="-c statement_timeout=5000 -c idle_in_transaction_session_timeout=10000")
        self.secret = read_secret("secret_key", "SECRET_KEY_FILE").encode()
        # The public address(es) of the site, for the Origin/Referer check and cookie flags.
        self.origins = [o.strip().rstrip("/") for o in env("ORIGIN", "http://127.0.0.1:8090").split(",") if o.strip()]
        self.secure_cookies = self.origins[0].startswith("https://")
        self.trust_proxy = env("TRUST_PROXY", "1") == "1"   # take the client IP from Caddy's X-Real-IP
        self.static_dir = env("STATIC_DIR", os.path.join(HERE, "..", "static"))
        self.template_dir = env("TEMPLATE_DIR", os.path.join(HERE, "..", "templates"))
        self.closed_flag = env("CLOSED_FLAG", "/etc/gwsignup/closed")  # if this file exists, sign-ups are closed
        self.download_url = env("DOWNLOAD_URL", "")
        self.server_name = env("SERVER_NAME", "GodsWar")
        self.iterations = env_int("KDF_ITERATIONS", 600000)
        self.kdf_slots = env_int("KDF_SLOTS", 2)
        # Rate limits (per client IP unless noted).
        self.lim_post = (env_int("LIMIT_POSTS", 10), env_int("LIMIT_POSTS_WINDOW", 600))        # form submissions
        self.lim_ok_hour = (env_int("LIMIT_ACCOUNTS_HOUR", 3), 3600)                            # accounts created
        self.lim_ok_day = (env_int("LIMIT_ACCOUNTS_DAY", 6), 86400)
        self.lim_global = (env_int("LIMIT_ACCOUNTS_GLOBAL_HOUR", 60), 3600)                     # all IPs together
        self.form_max_age = 2 * 3600
        self.form_min_age = env_int("FORM_MIN_SECONDS", 3)
        # Settings sync for the Mac launcher (/api/settings/pull and /push).
        self.settings_sync = env("SETTINGS_SYNC", "1") == "1"
        self.lim_api = (env_int("LIMIT_API_HOUR", 120), 3600)                                   # requests per IP
        self.lim_api_fail = (env_int("LIMIT_API_FAILS_HOUR", 10), 3600)                         # wrong logins, per IP and per name


# --------------------------------------------------------------------------- rules

USERNAME_RE = re.compile(r"[a-z0-9]{4,16}")            # always used with fullmatch()
EMAIL_RE = re.compile(r"[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+")
PASSWORD_RE = re.compile(r"[\x21-\x7e]{8,32}")   # printable ASCII, no spaces; the client's box holds 32

# Names nobody may register. Exact matches, plus anything that starts with RESERVED_PREFIX or
# contains the words in RESERVED_PARTS. "player" is the name reborn falls back to for an empty
# login name. Digits that look like letters are read as letters too (adm1n, supp0rt, g4memaster).
RESERVED_EXACT = {
    "admin", "administrator", "root", "system", "server", "support", "staff", "moderator", "mod",
    "gm", "gamemaster", "owner", "official", "igg", "godswar", "reborn", "player", "guest", "test",
    "tester", "null", "none", "undefined", "anonymous", "webmaster", "postmaster", "hostmaster",
    "abuse", "security", "info", "help", "admin", "zeus", "hades", "athena", "ares",
    "poseidon", "hermes", "cronus",
}
RESERVED_PARTS = ("admin", "gamemaster", "moderator", "support", "official", "staff", "godswar")
RESERVED_PREFIX = ("gm", "admin", "igg")
LOOKALIKE = (str.maketrans("013457", "oieast"), str.maketrans("013457", "oleast"))


def check_username(name):
    """Return an error message, or None. Format errors are specific; they reveal nothing."""
    if not name:
        return "Pick a username."
    if any(c.isupper() for c in name):
        return "Use lowercase letters only. The game client changes capital letters, so a name with capitals would not work in the game."
    if not USERNAME_RE.fullmatch(name):
        return "Usernames are 4 to 16 characters: lowercase letters a-z and numbers 0-9 only."
    return None


def is_reserved(name):
    for n in {name} | {name.translate(t) for t in LOOKALIKE}:
        if n in RESERVED_EXACT or n.startswith(RESERVED_PREFIX) or any(p in n for p in RESERVED_PARTS):
            return True
    return False


def check_password(pw, confirm, name):
    if not pw:
        return "Pick a password."
    if not PASSWORD_RE.fullmatch(pw):
        return "Passwords are 8 to 32 characters: letters, numbers and symbols, with no spaces."
    if pw != confirm:
        return "The two passwords are not the same."
    if name and name in pw.lower():
        return "The password must not contain your username."
    if len(set(pw)) < 4:
        return "That password is too easy to guess. Use a mix of letters, numbers and symbols."
    return None


def check_email(email):
    if not email:
        return "Enter your email address."
    if len(email) > 254 or not EMAIL_RE.fullmatch(email):
        return "That does not look like an email address."
    return None


# --------------------------------------------------------------------------- passwords

def game_verifier(password, iterations):
    """The verifier reborn stores in accounts.password.

    The 2.51 client sends md5(password) as 32 lowercase hex characters, and reborn runs
    PBKDF2-HMAC-SHA256 over those 32 bytes (Security/Authentication/PasswordVerifierRecord.cs).
    Salt and hash are standard padded Base64; reborn rejects any other encoding.
    """
    md5hex = hashlib.md5(password.encode("ascii")).hexdigest().encode("ascii")
    salt = secrets.token_bytes(16)
    dk = hashlib.pbkdf2_hmac("sha256", md5hex, salt, iterations, 32)
    return "gws$pbkdf2-sha256$v1$%d$%s$%s" % (iterations, base64.b64encode(salt).decode("ascii"),
                                              base64.b64encode(dk).decode("ascii"))


def check_game_verifier(verifier, md5hex):
    """True if md5hex (what the game client sends) matches reborn's verifier.

    Only the versioned form game_verifier() writes. Older plain-text rows become versioned at the
    account's next game login, so their owners can sync after playing once.
    """
    parts = (verifier or "").split("$")
    if len(parts) != 6 or parts[:3] != ["gws", "pbkdf2-sha256", "v1"] or not parts[3].isdigit():
        return False
    iterations = int(parts[3])
    if not 1 <= iterations <= 10000000:
        return False
    try:
        salt = base64.b64decode(parts[4], validate=True)
        want = base64.b64decode(parts[5], validate=True)
    except ValueError:
        return False
    got = hashlib.pbkdf2_hmac("sha256", md5hex.encode("ascii"), salt, iterations, len(want))
    return hmac.compare_digest(got, want)


# --------------------------------------------------------------------------- abuse controls

class RateLimiter:
    """Sliding-window counters in memory. Lost on restart, which is fine for this purpose."""

    def __init__(self):
        self.lock = threading.Lock()
        self.events = collections.defaultdict(collections.deque)
        self.last_sweep = time.monotonic()

    def _trim(self, q, window, now):
        while q and q[0] <= now - window:
            q.popleft()

    def blocked(self, key, limit, window):
        now = time.monotonic()
        with self.lock:
            q = self.events[key]
            self._trim(q, window, now)
            return len(q) >= limit

    def hit(self, key, window):
        now = time.monotonic()
        with self.lock:
            self.events[key].append(now)
            if now - self.last_sweep > 600:
                self.last_sweep = now
                for k in list(self.events):
                    self._trim(self.events[k], 86400, now)
                    if not self.events[k]:
                        del self.events[k]


class UsedTokens:
    """Each form token may be submitted once."""

    def __init__(self, ttl):
        self.ttl = ttl
        self.lock = threading.Lock()
        self.seen = {}

    def consume(self, token):
        now = time.monotonic()
        with self.lock:
            if len(self.seen) > 50000 or (self.seen and next(iter(self.seen.values())) < now - self.ttl):
                self.seen = {k: v for k, v in self.seen.items() if v > now - self.ttl}
            if token in self.seen:
                return False
            self.seen[token] = now
            return True


NUMBER_WORDS = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
                "eleven", "twelve"]


class Forms:
    """CSRF + challenge tokens, stateless apart from the used-token set.

    The browser gets a random cookie C. The form carries ts and
    tok = HMAC(secret, "form|C|ts"); the challenge answer is checked with
    chk = HMAC(secret, "q|C|ts|answer"). Nothing in the page reveals the answer.
    """

    def __init__(self, cfg):
        self.cfg = cfg
        self.used = UsedTokens(cfg.form_max_age + 60)

    def _mac(self, *parts):
        return hmac.new(self.cfg.secret, "|".join(parts).encode(), hashlib.sha256).hexdigest()

    def new(self, cookie):
        ts = str(int(time.time()))
        a, b = secrets.randbelow(9) + 2, secrets.randbelow(9) + 2
        if secrets.randbelow(2) and a > b:
            question, answer = "What is %s minus %s?" % (NUMBER_WORDS[a], NUMBER_WORDS[b]), a - b
        else:
            question, answer = "What is %s plus %s?" % (NUMBER_WORDS[a], NUMBER_WORDS[b]), a + b
        return {"ts": ts, "tok": self._mac("form", cookie, ts), "chk": self._mac("q", cookie, ts, str(answer)),
                "question": question}

    def check(self, cookie, form):
        """Returns (ok_csrf, ok_challenge, reason)."""
        ts, tok, chk = form.get("ts", ""), form.get("tok", ""), form.get("chk", "")
        if not cookie or not ts.isdigit() or not tok:
            return False, False, "missing"
        if not hmac.compare_digest(tok, self._mac("form", cookie, ts)):
            return False, False, "bad-token"
        age = time.time() - int(ts)
        if age > self.cfg.form_max_age or age < -60:
            return False, False, "expired"
        if not self.used.consume(tok):
            return False, False, "reused"
        if age < self.cfg.form_min_age:
            return True, False, "too-fast"
        answer = form.get("answer", "").strip()
        if not re.fullmatch(r"-?[0-9]{1,2}", answer):
            return True, False, "bad-answer"
        if not hmac.compare_digest(chk, self._mac("q", cookie, ts, str(int(answer)))):
            return True, False, "bad-answer"
        return True, True, "ok"


# --------------------------------------------------------------------------- database

class Store:
    INSERT_ACCOUNT = """
        INSERT INTO public.accounts (
            uuid, email, username, password, login_status,
            last_login_time, last_logout_time, last_login_ip,
            last_login_mac, total_online_time, status
        )
        VALUES ('', %(email)s, %(username)s, %(verifier)s, 0, now(), now(), '', '', 0, 0)
        ON CONFLICT (username) DO NOTHING
        RETURNING id
    """
    # The same columns and values as reborn's TryCreateAccountWithCredentialAsync; only email differs.
    # account_realm is not written here: reborn adds that row itself when the account first uses a realm.

    def __init__(self, cfg):
        self.cfg = cfg
        self.pool = None
        self.pool_lock = threading.Lock()
        self.slots = threading.BoundedSemaphore(4)   # the pool holds at most 4 connections

    def _pool(self):
        with self.pool_lock:
            if self.pool is None:
                self.pool = psycopg2.pool.ThreadedConnectionPool(0, 4, **self.cfg.db)
            return self.pool

    def _run(self, fn, retry=True):
        """Run fn(cursor) in one transaction. Retries once on a dropped connection, but only
        when asked: a write is never retried, because its first try may have committed."""
        if not self.slots.acquire(timeout=10):
            raise psycopg2.OperationalError("all database connections are busy")
        try:
            for attempt in (1, 2):
                pool = self._pool()
                c = pool.getconn()
                broken = False
                try:
                    with c:
                        with c.cursor() as cur:
                            return fn(cur)
                except (psycopg2.OperationalError, psycopg2.InterfaceError):
                    broken = True
                    if attempt == 2 or not retry:
                        raise
                finally:
                    pool.putconn(c, close=broken or c.closed != 0)
        finally:
            self.slots.release()

    def ping(self):
        return self._run(lambda cur: (cur.execute("SELECT 1"), cur.fetchone()[0])[1]) == 1

    def account_verifier(self, username):
        def read(cur):
            cur.execute("SELECT id, password FROM public.accounts WHERE username = %s", (username,))
            return cur.fetchone()
        return self._run(read)

    def settings_get(self, account_id):
        def read(cur):
            cur.execute("SELECT name, content FROM signup.account_settings WHERE account_id = %s ORDER BY name",
                        (account_id,))
            return [(name, bytes(content)) for name, content in cur.fetchall()]
        return self._run(read)

    def settings_put(self, account_id, files):
        def tx(cur):
            for name, content in sorted(files.items()):
                cur.execute("""
                    INSERT INTO signup.account_settings (account_id, name, content, updated_at)
                    VALUES (%s, %s, %s, now())
                    ON CONFLICT (account_id, name) DO UPDATE SET content = EXCLUDED.content, updated_at = now()
                """, (account_id, name, psycopg2.Binary(content)))
        self._run(tx, retry=False)

    def ranking_rows(self):
        def read(cur):
            cur.execute("SELECT name, level, profession, camp, prestige, money FROM signup.public_rankings LIMIT 5000")
            return cur.fetchall()
        return self._run(read)

    def name_or_email_taken(self, cur, username, email_lower):
        cur.execute("SELECT 1 FROM public.accounts WHERE lower(username) = %s OR (email <> '' AND lower(email) = %s) LIMIT 1",
                    (username, email_lower))
        if cur.fetchone():
            return True
        # Forget emails of accounts that no longer exist (deleted by a GM), so the address can be used again.
        cur.execute("""DELETE FROM signup.account_emails e WHERE e.email_lower = %s
                       AND NOT EXISTS (SELECT 1 FROM public.accounts a WHERE a.id = e.account_id)""", (email_lower,))
        cur.execute("SELECT 1 FROM signup.account_emails WHERE email_lower = %s", (email_lower,))
        return cur.fetchone() is not None

    def precheck(self, username, email_lower):
        return self._run(lambda cur: self.name_or_email_taken(cur, username, email_lower))

    def create(self, username, email, verifier, ip_hash):
        """Returns the new account id, or None if the name or email is taken."""
        def tx(cur):
            # One sign-up at a time, so two people cannot take the same name or email in a race.
            cur.execute("SELECT pg_advisory_xact_lock(hashtext('gwsignup'))")
            if self.name_or_email_taken(cur, username, email.lower()):
                return None
            cur.execute(self.INSERT_ACCOUNT, {"email": email, "username": username, "verifier": verifier})
            row = cur.fetchone()
            if not row:
                return None
            cur.execute("""INSERT INTO signup.account_emails (account_id, email, email_lower, signup_ip_hash)
                           VALUES (%s, %s, %s, %s)""", (row[0], email, email.lower(), ip_hash))
            return row[0]
        return self._run(tx, retry=False)


def on_account_created(account_id, username, email):
    """HOOK for email verification (not in this version: there is no mail provider yet).

    When a provider exists: create a token with secrets.token_urlsafe(32), store its SHA-256 in
    signup.email_tokens (purpose 'verify_email', 24 h expiry), send the link, and on click set
    signup.account_emails.verified_at = now(). Password reset uses the same table with purpose
    'reset_password' and must write a new verifier made by game_verifier().
    """
    LOG.info("email verification not sent (no mail provider configured) account_id=%s", account_id)


# --------------------------------------------------------------------------- web app

STATUS = {200: "200 OK", 303: "303 See Other", 400: "400 Bad Request", 401: "401 Unauthorized", 403: "403 Forbidden",
          404: "404 Not Found", 405: "405 Method Not Allowed", 413: "413 Payload Too Large",
          429: "429 Too Many Requests", 500: "500 Internal Server Error", 503: "503 Service Unavailable"}
MAX_BODY = 8192

# Settings sync. Files from the client's Settings/User folder, except the two that belong to one
# computer (its last server and its screen settings).
SETTINGS_NAME_RE = re.compile(r"[A-Za-z][A-Za-z0-9_]{0,39}\.xml")
SETTINGS_SKIP = {"LastSelectServer.xml", "SystemSetup.xml"}
SETTINGS_MAX_FILE = 65536
SETTINGS_MAX_FILES = 16
SETTINGS_MAX_BODY = 3 * SETTINGS_MAX_FILE * 2   # url-encoding can triple a byte; a few files at most
SETTINGS_USER_RE = re.compile(r"[\x21-\x7e]{1,32}")   # reborn's own login-name rule


class App:
    def __init__(self, cfg):
        self.cfg = cfg
        self.store = Store(cfg)
        self.forms = Forms(cfg)
        self.limits = RateLimiter()
        self.kdf = threading.BoundedSemaphore(cfg.kdf_slots)
        self.dummy_verifier = game_verifier(secrets.token_hex(8), cfg.iterations)   # for unknown names
        self.jinja = jinja2.Environment(loader=jinja2.FileSystemLoader(cfg.template_dir),
                                        autoescape=True, trim_blocks=True, lstrip_blocks=True)
        self.static_dir = os.path.realpath(cfg.static_dir)
        self.cookie_name = "__Host-gw_form" if cfg.secure_cookies else "gw_form"
        self.rankings_cache = (0.0, None)
        self.rankings_lock = threading.Lock()

    # ---- helpers
    def client_ip(self, environ):
        peer = environ.get("REMOTE_ADDR", "")
        if self.cfg.trust_proxy and peer in ("127.0.0.1", "::1"):
            real = environ.get("HTTP_X_REAL_IP", "").strip()
            try:
                return str(ipaddress.ip_address(real))
            except ValueError:
                pass
        return peer

    def ip_key(self, ip):
        """IPv6 clients are limited per /64, since one user usually owns a whole /64."""
        try:
            addr = ipaddress.ip_address(ip)
        except ValueError:
            return ip
        if addr.version == 6:
            return str(ipaddress.ip_network(ip + "/64", strict=False))
        return ip

    def ip_hash(self, ip):
        return hmac.new(self.cfg.secret, ("ip|" + ip).encode(), hashlib.sha256).hexdigest()[:32]

    def cookie(self, environ):
        for part in environ.get("HTTP_COOKIE", "").split(";"):
            k, _, v = part.strip().partition("=")
            if k == self.cookie_name and re.fullmatch(r"[A-Za-z0-9_-]{32,64}", v):
                return v
        return ""

    def set_cookie(self, value):
        flags = "; Path=/; HttpOnly; SameSite=Strict; Max-Age=7200"
        if self.cfg.secure_cookies:
            flags += "; Secure"
        return ("Set-Cookie", "%s=%s%s" % (self.cookie_name, value, flags))

    def render(self, start, status, template, extra_headers=(), **ctx):
        ctx.setdefault("server_name", self.cfg.server_name)
        ctx.setdefault("download_url", self.cfg.download_url)
        body = self.jinja.get_template(template).render(**ctx).encode("utf-8")
        headers = [("Content-Type", "text/html; charset=utf-8"), ("Content-Length", str(len(body))),
                   ("Cache-Control", "no-store"), ("X-Content-Type-Options", "nosniff"),
                   ("X-Frame-Options", "DENY"), ("Referrer-Policy", "same-origin")]
        headers.extend(extra_headers)
        start(STATUS[status], headers)
        return [body]

    def message(self, start, status, title, text, retry=True):
        return self.render(start, status, "message.html", title=title, text=text, retry=retry)

    def signup_form(self, start, environ, status=200, error=None, values=None, field=None):
        cookie = self.cookie(environ)
        headers = []
        if not cookie:
            cookie = secrets.token_urlsafe(32)
            headers.append(self.set_cookie(cookie))
        return self.render(start, status, "signup.html", headers, form=self.forms.new(cookie),
                           error=error, field=field, v=values or {}, closed=self.closed())

    def closed(self):
        return bool(self.cfg.closed_flag) and os.path.exists(self.cfg.closed_flag)

    def origin_ok(self, environ):
        origin = environ.get("HTTP_ORIGIN", "")
        if origin and origin != "null":
            return origin.rstrip("/") in self.cfg.origins
        if origin == "null":
            return False
        ref = environ.get("HTTP_REFERER", "")
        if ref:
            return any(ref == o or ref.startswith(o + "/") for o in self.cfg.origins)
        return True   # no Origin and no Referer: the CSRF token alone decides

    # ---- WSGI entry
    def __call__(self, environ, start):
        try:
            return self.route(environ, start)
        except (psycopg2.OperationalError, psycopg2.InterfaceError) as e:
            LOG.warning("database unreachable on %s %s: %s", environ.get("REQUEST_METHOD"), environ.get("PATH_INFO"),
                        str(e).strip().splitlines()[0][:200] if str(e).strip() else type(e).__name__)
            return self.message(start, 503, "Sign-up is resting",
                                "The account server is not answering right now, so no account was made. Please try again in a few minutes.")
        except Exception:
            LOG.exception("unhandled error on %s %s", environ.get("REQUEST_METHOD"), environ.get("PATH_INFO"))
            return self.message(start, 500, "Something went wrong",
                                "The sign-up could not be finished because of a problem on our side. Please try again in a few minutes.")

    def route(self, environ, start):
        method, path = environ.get("REQUEST_METHOD", "GET"), environ.get("PATH_INFO", "/")
        if path == "/" or path == "/signup":
            if method in ("GET", "HEAD"):
                return self.signup_form(start, environ)
            if method == "POST":
                return self.post_signup(environ, start)
            return self.message(start, 405, "Not allowed", "That request is not allowed here.", retry=False)
        if path in ("/api/settings/pull", "/api/settings/push"):
            if method != "POST" or not self.cfg.settings_sync:
                return self.text(start, 405 if self.cfg.settings_sync else 404, "not available\n")
            return self.api_settings(environ, start, path.rsplit("/", 1)[1])
        if path == "/rankings" and method in ("GET", "HEAD"):
            return self.rankings(start)
        if path == "/healthz" and method in ("GET", "HEAD"):
            ok = False
            try:
                ok = self.store.ping()
            except Exception as e:
                LOG.warning("health check: database unreachable: %s", type(e).__name__)
            body = b"ok\n" if ok else b"database unreachable\n"
            start(STATUS[200 if ok else 503], [("Content-Type", "text/plain"), ("Content-Length", str(len(body))),
                                                ("Cache-Control", "no-store")])
            return [body]
        if path.startswith("/static/") and method in ("GET", "HEAD"):
            return self.static(path[len("/static/"):], start)
        if path in ("/robots.txt", "/favicon.ico"):
            return self.static(path[1:], start)
        return self.message(start, 404, "Page not found", "There is nothing at this address.", retry=True)

    # ---- settings sync (Mac launcher)
    def text(self, start, status, body, content_type="text/plain; charset=utf-8"):
        if isinstance(body, str):
            body = body.encode("utf-8")
        start(STATUS[status], [("Content-Type", content_type), ("Content-Length", str(len(body))),
                               ("Cache-Control", "no-store"), ("X-Content-Type-Options", "nosniff")])
        return [body]

    def api_settings(self, environ, start, action):
        """pull: a tar of the account's stored files. push: f_<name>=<file> fields replace stored files.
        Both need user=<account> and key=<md5 of the password, 32 lowercase hex>, as the game sends."""
        cfg = self.cfg
        ip = self.client_ip(environ)
        ipk = self.ip_key(ip)
        if self.limits.blocked(("api", ipk), *cfg.lim_api) or self.limits.blocked(("api-fail", ipk), *cfg.lim_api_fail):
            return self.text(start, 429, "too many requests\n")
        self.limits.hit(("api", ipk), cfg.lim_api[1])
        try:
            length = int(environ.get("CONTENT_LENGTH") or 0)
        except ValueError:
            length = -1
        if length < 0 or length > SETTINGS_MAX_BODY:
            return self.text(start, 413, "too much data\n")
        raw = environ["wsgi.input"].read(length) if length else b""
        try:
            fields = parse_qs(raw.decode("ascii"), keep_blank_values=True, strict_parsing=False,
                              max_num_fields=SETTINGS_MAX_FILES + 4, encoding="latin-1")
        except (UnicodeDecodeError, ValueError):
            return self.text(start, 400, "bad request\n")
        user = (fields.get("user") or [""])[0]
        key = (fields.get("key") or [""])[0]
        if not SETTINGS_USER_RE.fullmatch(user) or not re.fullmatch(r"[0-9a-f]{32}", key):
            return self.text(start, 400, "bad request\n")
        if self.limits.blocked(("api-fail-user", user.lower()), *cfg.lim_api_fail):
            return self.text(start, 429, "too many requests\n")

        files = {}
        if action == "push":
            for field, values in fields.items():
                if not field.startswith("f_"):
                    continue
                name, content = field[2:], values[0].encode("latin-1")
                if (not SETTINGS_NAME_RE.fullmatch(name) or name in SETTINGS_SKIP or len(content) > SETTINGS_MAX_FILE
                        or not content.lstrip(b"\xef\xbb\xbf\r\n\t ").startswith(b"<")):
                    return self.text(start, 400, "bad file %s\n" % name[:48])
                files[name] = content
            if not files or len(files) > SETTINGS_MAX_FILES:
                return self.text(start, 400, "no files\n")

        row = self.store.account_verifier(user)
        if not self.kdf.acquire(timeout=20):
            return self.text(start, 503, "busy\n")
        try:
            # An unknown name costs the same time as a wrong password.
            ok = check_game_verifier(row[1] if row else self.dummy_verifier, key) and row is not None
        finally:
            self.kdf.release()
        if not ok:
            self.limits.hit(("api-fail", ipk), cfg.lim_api_fail[1])
            self.limits.hit(("api-fail-user", user.lower()), cfg.lim_api_fail[1])
            LOG.info("settings %s refused ip_hash=%s", action, self.ip_hash(ip)[:12])
            return self.text(start, 401, "wrong account or password\n")

        account_id = row[0]
        if action == "push":
            self.store.settings_put(account_id, files)
            LOG.info("settings push account=%s files=%d", account_id, len(files))
            return self.text(start, 200, "ok %d\n" % len(files))
        buf = io.BytesIO()
        with tarfile.open(fileobj=buf, mode="w", format=tarfile.USTAR_FORMAT) as tar:
            for name, content in self.store.settings_get(account_id):
                info = tarfile.TarInfo(name)
                info.size, info.mode, info.mtime = len(content), 0o644, int(time.time())
                tar.addfile(info, io.BytesIO(content))
        LOG.info("settings pull account=%s", account_id)
        return self.text(start, 200, buf.getvalue(), "application/x-tar")

    def ranking_boards(self):
        """The boards for /rankings, read at most once a minute."""
        with self.rankings_lock:
            at, boards = self.rankings_cache
            if boards is not None and time.monotonic() - at < 60:
                return boards
            players = [dict(name=r[0], level=r[1], cls=CLASS_NAMES.get(r[2], "Unknown"), camp=CAMP_NAMES.get(r[3], "Unknown"),
                            prestige=r[4], money=r[5]) for r in self.store.ranking_rows()]
            def top(rows, key):
                return sorted(rows, key=lambda p: (-p[key], -p["level"], p["name"].lower()))[:50]
            boards = [dict(title="Highest level", column="Level", key="level", rows=top(players, "level")),
                      dict(title="Wealth", column="Silver", key="money", rows=top(players, "money")),
                      dict(title="Reputation", column="Reputation", key="prestige", rows=top([p for p in players if p["prestige"] > 0], "prestige"))]
            for name in CLASS_NAMES.values():
                boards.append(dict(title=name + "s", column="Level", key="level", rows=top([p for p in players if p["cls"] == name], "level")))
            self.rankings_cache = (time.monotonic(), boards)
            return boards

    def rankings(self, start):
        return self.render(start, 200, "rankings.html", boards=self.ranking_boards())

    def static(self, rel, start):
        full = os.path.realpath(os.path.join(self.static_dir, rel))
        if not full.startswith(self.static_dir + os.sep) or not os.path.isfile(full):
            body = b"not found\n"
            start(STATUS[404], [("Content-Type", "text/plain"), ("Content-Length", str(len(body)))])
            return [body]
        ctype = mimetypes.guess_type(full)[0] or "application/octet-stream"
        if full.endswith(".ico"):
            ctype = "image/x-icon"
        with open(full, "rb") as f:
            body = f.read()
        start(STATUS[200], [("Content-Type", ctype), ("Content-Length", str(len(body))),
                            ("Cache-Control", "public, max-age=3600"), ("X-Content-Type-Options", "nosniff")])
        return [body]

    def read_form(self, environ):
        try:
            length = int(environ.get("CONTENT_LENGTH") or 0)
        except ValueError:
            length = 0
        if length > MAX_BODY:
            return None
        ctype = environ.get("CONTENT_TYPE", "").split(";")[0].strip().lower()
        if ctype != "application/x-www-form-urlencoded":
            return {}
        raw = environ["wsgi.input"].read(length) if length > 0 else b""
        try:
            parsed = parse_qs(raw.decode("utf-8"), keep_blank_values=True, max_num_fields=20, strict_parsing=False)
        except (UnicodeDecodeError, ValueError):
            return {}
        return {k: v[0] for k, v in parsed.items()}

    def post_signup(self, environ, start):
        cfg = self.cfg
        ip = self.client_ip(environ)
        ipk = self.ip_key(ip)

        if self.limits.blocked(("post", ipk), *cfg.lim_post):
            LOG.info("refused rate-limit=posts ip_hash=%s", self.ip_hash(ip)[:12])
            return self.message(start, 429, "Too many tries",
                                "You have sent the form too many times. Please wait a few minutes and try again.")
        self.limits.hit(("post", ipk), cfg.lim_post[1])

        if not self.origin_ok(environ):
            LOG.info("refused bad-origin ip_hash=%s", self.ip_hash(ip)[:12])
            return self.message(start, 403, "Form expired", "This form came from somewhere else. Open the sign-up page again and retry.")

        form = self.read_form(environ)
        if form is None:
            return self.message(start, 413, "Too much data", "The form was too large.")

        ok_csrf, ok_challenge, reason = self.forms.check(self.cookie(environ), form)
        if not ok_csrf:
            LOG.info("refused csrf=%s ip_hash=%s", reason, self.ip_hash(ip)[:12])
            return self.message(start, 403, "Form expired",
                                "This form has expired or was already sent. Open the sign-up page again and retry. "
                                "(Your browser needs to accept cookies from this site.)")

        username = form.get("username", "").strip()
        email = form.get("email", "").strip()
        password, confirm = form.get("password", ""), form.get("confirm", "")
        values = {"username": username, "email": email}

        if form.get("website", ""):
            # Honeypot: a hidden field that people never see. Bots fill it. Pretend it worked.
            LOG.info("refused honeypot ip_hash=%s", self.ip_hash(ip)[:12])
            return self.render(start, 200, "done.html", username=username)

        if self.closed():
            return self.message(start, 503, "Sign-ups are closed", "New accounts cannot be made right now. Please try again later.")

        if not ok_challenge:
            msg = ("That was quick! Please take a moment and fill in the form again." if reason == "too-fast"
                   else "The answer to the question was not right. Please try again.")
            return self.signup_form(start, environ, 400, msg, values, "answer")

        for fld, err in (("username", check_username(username)), ("password", check_password(password, confirm, username.lower())),
                         ("email", check_email(email))):
            if err:
                return self.signup_form(start, environ, 400, err, values, fld)

        if (self.limits.blocked(("ok-h", ipk), *cfg.lim_ok_hour) or self.limits.blocked(("ok-d", ipk), *cfg.lim_ok_day)):
            LOG.info("refused rate-limit=accounts ip_hash=%s", self.ip_hash(ip)[:12])
            return self.message(start, 429, "Too many new accounts",
                                "Too many accounts were made from your connection. Please try again tomorrow.")
        if self.limits.blocked(("ok-global",), *cfg.lim_global):
            LOG.warning("refused rate-limit=global")
            return self.message(start, 503, "Busy", "Lots of people are signing up right now. Please try again in an hour.")

        taken_msg = "That username or email cannot be used. Please choose a different one."
        if is_reserved(username):
            LOG.info("refused reserved-name ip_hash=%s", self.ip_hash(ip)[:12])
            return self.signup_form(start, environ, 400, taken_msg, values, "username")
        if self.store.precheck(username, email.lower()):
            LOG.info("refused taken ip_hash=%s", self.ip_hash(ip)[:12])
            return self.signup_form(start, environ, 400, taken_msg, values, "username")

        if not self.kdf.acquire(timeout=10):
            return self.message(start, 503, "Busy", "The server is busy. Please try again in a minute.")
        try:
            verifier = game_verifier(password, cfg.iterations)
        finally:
            self.kdf.release()

        account_id = self.store.create(username, email, verifier, self.ip_hash(ip))
        if account_id is None:
            LOG.info("refused taken-at-insert ip_hash=%s", self.ip_hash(ip)[:12])
            return self.signup_form(start, environ, 400, taken_msg, values, "username")

        for key, window in ((("ok-h", ipk), 3600), (("ok-d", ipk), 86400), (("ok-global",), 3600)):
            self.limits.hit(key, window)
        LOG.info("account created id=%s username=%s ip_hash=%s", account_id, username, self.ip_hash(ip)[:12])
        on_account_created(account_id, username, email)
        return self.render(start, 200, "done.html", username=username)


# --------------------------------------------------------------------------- server

class QuietHandler(WSGIRequestHandler):
    timeout = 20

    def log_message(self, fmt, *args):
        pass   # Caddy keeps the access log; the app logs only sign-up outcomes.

    def address_string(self):
        return self.client_address[0]


class ThreadingWSGIServer(socketserver.ThreadingMixIn, WSGIServer):
    daemon_threads = True
    request_queue_size = 64


def main():
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s", stream=sys.stdout)
    cfg = Config()
    app = App(cfg)
    try:
        app.store.ping()
        LOG.info("database %s as %s: ok", cfg.db["dbname"], cfg.db["user"])
    except Exception as e:
        LOG.warning("database not reachable at start (%s); will retry per request", type(e).__name__)
    httpd = make_server(cfg.bind, cfg.port, app, server_class=ThreadingWSGIServer, handler_class=QuietHandler)
    LOG.info("listening on http://%s:%d (origin %s)", cfg.bind, cfg.port, ", ".join(cfg.origins))
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
