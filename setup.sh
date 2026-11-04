#!/usr/bin/env bash
# GodsWar one-step setup: installs what is missing, writes your config, starts the server,
# and creates the administrator account.
#
#   ./setup.sh                 interactive (asks local computer or VPS)
#   ./setup.sh --local         this computer only (127.0.0.1)
#   ./setup.sh --vps           internet-facing server on Ubuntu/Debian (run as root or with sudo)
#   Options: --host ADDRESS  --admin NAME  --edition reborn|classic  --yes (accept defaults)
set -euo pipefail
cd "$(dirname "$0")"

MODE=""; HOST=""; ADMIN=""; EDITION=""; YES=0
while [ $# -gt 0 ]; do
  case "$1" in
    --local) MODE=local ;; --vps) MODE=vps ;; --host) HOST=$2; shift ;; --admin) ADMIN=$2; shift ;; --edition) EDITION=$2; shift ;; --yes|-y) YES=1 ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac; shift
done

say()  { printf '\n\033[1m==> %s\033[0m\n' "$*"; }
die()  { printf '\n\033[31mERROR:\033[0m %s\n' "$*" >&2; exit 1; }
ask()  { local q=$1 d=${2:-} a; [ "$YES" = 1 ] && { echo "$d"; return; }; read -r -p "$q${d:+ [$d]}: " a </dev/tty || a=""; echo "${a:-$d}"; }
rand() { LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c "${1:-20}"; }
SUDO=""; [ "$(id -u)" != 0 ] && command -v sudo >/dev/null && SUDO=sudo

say "GodsWar setup"
[ -n "$MODE" ] || { echo "1) This computer only (testing, LAN)   2) A VPS / server others will join"; c=$(ask "Choose 1 or 2" 1); [ "$c" = 2 ] && MODE=vps || MODE=local; }

# ---- 1. Required tools ---------------------------------------------------------------
say "Checking requirements"
OS=$(uname -s)
need_install=()
command -v python3 >/dev/null || need_install+=(python3)
command -v curl    >/dev/null || need_install+=(curl)
command -v openssl >/dev/null || need_install+=(openssl)
if ! command -v docker >/dev/null || ! docker compose version >/dev/null 2>&1; then
  if [ "$OS" = Linux ] && command -v apt-get >/dev/null; then
    echo "Docker is missing; installing it with the official script (needs root)."
    [ "$(ask 'Install Docker now? (y/n)' y)" = y ] || die "Docker is required."
    curl -fsSL https://get.docker.com | $SUDO sh
    [ -n "$SUDO" ] && $SUDO usermod -aG docker "$USER" || true
  elif [ "$OS" = Darwin ]; then
    die "Install Docker Desktop first: https://www.docker.com/products/docker-desktop  (or: brew install --cask docker). Start it once, then run ./setup.sh again."
  else
    die "Install Docker with the compose plugin for your system, then run ./setup.sh again."
  fi
fi
if [ ${#need_install[@]} -gt 0 ]; then
  [ "$OS" = Linux ] && command -v apt-get >/dev/null && { $SUDO apt-get update -qq && $SUDO apt-get install -y -qq "${need_install[@]}"; } || die "Please install: ${need_install[*]}"
fi
DOCKER=docker; docker info >/dev/null 2>&1 || { $SUDO docker info >/dev/null 2>&1 && DOCKER="$SUDO docker" || die "Docker is installed but not running (start Docker Desktop / the docker service)."; }
COMPOSE="$DOCKER compose --env-file config/godswar.env"

# ---- 2. Config ------------------------------------------------------------------------
say "Writing config/godswar.env"
if [ -f config/godswar.env ]; then
  echo "config/godswar.env already exists; keeping it. Delete it to start over."
else
  if [ -z "$HOST" ]; then
    if [ "$MODE" = vps ]; then
      guess=$(curl -fsS -m 8 https://api.ipify.org 2>/dev/null || true)
      HOST=$(ask "Public IP address or domain players will use" "$guess")
    else HOST=127.0.0.1; fi
  fi
  [ -n "$HOST" ] || die "A host address is required."
  [ -n "$ADMIN" ] || ADMIN=$(ask "Administrator account name" admin)
  if [ -z "$EDITION" ]; then
    echo "Edition: 1) reborn - limits raised past the original game (needs the patched client)   2) classic - original limits"
    [ "$(ask 'Choose 1 or 2' 1)" = 2 ] && EDITION=classic || EDITION=reborn
  fi
  case "$EDITION" in classic|reborn) ;; *) die "--edition must be classic or reborn" ;; esac
  mkdir -p config
  cp config/godswar.env.example config/godswar.env
  python3 - "$HOST" "$ADMIN" "$(rand 24)" "$(rand 14)" "$EDITION" <<'PY'
import re, sys
host, admin, dbpw, adminpw, edition = sys.argv[1:]
p = "config/godswar.env"; t = open(p).read()
for k, v in {"GODSWAR_HOST": host, "GODSWAR_ADMIN_USER": admin, "POSTGRES_PASSWORD": dbpw, "GODSWAR_ADMIN_PASSWORD": adminpw, "GODSWAR_EDITION": edition}.items():
    t = re.sub(rf"^{k}=.*$", f"{k}={v}", t, flags=re.M)
open(p, "w").write(t)
PY
  chmod 600 config/godswar.env
fi
# shellcheck disable=SC1091
set -a; . config/godswar.env; set +a

# ---- 3. Start --------------------------------------------------------------------------
say "Starting the database"
$COMPOSE up -d postgres
for _ in $(seq 1 60); do
  case "$($COMPOSE ps --format '{{.Service}} {{.Health}}' 2>/dev/null)" in *"postgres healthy"*) break ;; esac; sleep 2
done
PSQL="$COMPOSE exec -T postgres psql -U $POSTGRES_USER -d $POSTGRES_DB -v ON_ERROR_STOP=1 -q"
if [ "$(echo "SELECT to_regclass('public.schema_migrations') IS NOT NULL;" | $PSQL -t -A | tr -d '[:space:]')" != t ]; then
  say "Loading the game database (items, monsters, maps, quests; no players)"
  gunzip -c server/database/baseline.sql.gz | $PSQL >/dev/null
else echo "database already loaded; keeping it"; fi

say "Building and starting the server (first build downloads the .NET SDK image; allow several minutes)"
$COMPOSE up -d --build server

say "Waiting for the server"
for _ in $(seq 1 90); do
  st=$($COMPOSE ps --format '{{.Service}} {{.Health}}' 2>/dev/null | tr '\n' ' ')
  case "$st" in *"server healthy"*) break ;; esac; sleep 4
done
case "$st" in *"server healthy"*) echo "server is healthy" ;; *) die "Server did not become healthy. Look at: $COMPOSE logs server";; esac

# ---- 4. Realm address + admin account ---------------------------------------------------
say "Registering the realm address and the administrator account"
echo "UPDATE server SET ip_address='$GODSWAR_HOST', game_port=$GODSWAR_GAME_PORT;" | $PSQL
python3 -I tools/make-admin.py "$GODSWAR_ADMIN_USER" "$GODSWAR_ADMIN_PASSWORD" | $PSQL
ADMIN_ID=$(echo "SELECT id FROM accounts WHERE username='$GODSWAR_ADMIN_USER';" | $PSQL -t -A | head -1 | tr -d '[:space:]')
if [ -n "$ADMIN_ID" ]; then
  say "Enabling GM commands for the administrator account (id $ADMIN_ID)"
  sed -i.bak -e "s/^GODSWAR_DEVELOPER_COMMANDS_ENABLED=.*/GODSWAR_DEVELOPER_COMMANDS_ENABLED=true/" -e "s/^GODSWAR_DEVELOPER_ACCOUNT_IDS=.*/GODSWAR_DEVELOPER_ACCOUNT_IDS=$ADMIN_ID/" config/godswar.env && rm -f config/godswar.env.bak
  $COMPOSE up -d server
  sleep 3
  for _ in $(seq 1 60); do   # the restart recreates the container: wait until it is healthy again
    case "$($COMPOSE ps --format '{{.Service}} {{.Health}}' 2>/dev/null | tr '\n' ' ')" in *"server healthy"*) break ;; esac; sleep 3
  done
fi

# ---- 5. Firewall (VPS) --------------------------------------------------------------------
if [ "$MODE" = vps ] && command -v ufw >/dev/null; then
  if [ "$(ask "Open ports $GODSWAR_LOGIN_PORT and $GODSWAR_GAME_PORT in ufw? (y/n)" y)" = y ]; then
    $SUDO ufw allow "$GODSWAR_LOGIN_PORT/tcp" && $SUDO ufw allow "$GODSWAR_GAME_PORT/tcp"
  fi
  echo "Remember: many hosting providers also have their own firewall panel. Open the same two TCP ports there."
fi

say "Self-test: logging in as the administrator"
if python3 -I signup/test/game_login.py "$GODSWAR_ADMIN_USER" "$GODSWAR_ADMIN_PASSWORD" "127.0.0.1:$GODSWAR_LOGIN_PORT" | tail -1 | grep -q reached; then
  echo "login test passed (reached the character screen)"
else echo "WARNING: the login self-test did not pass; see: $COMPOSE logs server"; fi

cat <<DONE

GodsWar is running.

  Players connect to:  $GODSWAR_HOST   login port $GODSWAR_LOGIN_PORT   (game port $GODSWAR_GAME_PORT)
  Administrator:       $GODSWAR_ADMIN_USER   password in config/godswar.env (GODSWAR_ADMIN_PASSWORD)

Next: point your game client at the server (docs/CLIENT.md).
Handy:  $COMPOSE logs -f server     $COMPOSE stop     $COMPOSE up -d
DONE
