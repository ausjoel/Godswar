# Run GodsWar on your own computer

Good for testing and for playing with friends on your home network.

## You need
- macOS, Linux or Windows (WSL2) with about 8 GB free disk and 4 GB RAM
- Docker (Docker Desktop on Mac/Windows). `setup.sh` installs it on Ubuntu/Debian; elsewhere it tells you what to install.
- Your own GodsWar 2.51 client: [CLIENT.md](CLIENT.md)

## Steps
```bash
./setup.sh --local
```
1. It checks for Docker, python3, curl and openssl.
2. It writes `config/godswar.env` with random passwords (host `127.0.0.1`).
3. It builds and starts PostgreSQL and the server (the first build takes several minutes).
4. It creates your administrator account. The password is in `config/godswar.env`.

For friends on your network, run `./setup.sh --host YOUR.LAN.IP` instead.

## Everyday commands
```bash
docker compose --env-file config/godswar.env logs -f server   # watch the log
docker compose --env-file config/godswar.env stop             # stop
docker compose --env-file config/godswar.env up -d            # start again
```
Data lives in the Docker volume `godswar_godswar-postgres-data`; removing it deletes every character.
