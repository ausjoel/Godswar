# Run GodsWar on a VPS

## You need
- An x86_64 or ARM64 Ubuntu 22.04+/Debian 12+ VPS, 2 vCPU / 4 GB RAM / 20 GB disk or better, with a public IP
- Root (or sudo) access over SSH
- Your own GodsWar 2.51 client: [CLIENT.md](CLIENT.md)

## Steps
```bash
ssh you@your.server.ip
git clone <this repository> godswar && cd godswar
sudo ./setup.sh --vps
```
The script detects your public IP (you can correct it), installs Docker, writes
`config/godswar.env`, starts everything, creates the administrator, and offers to open
ports 6999 and 7000 in `ufw`.

**Also open those two TCP ports in your hosting provider's firewall panel** — many
providers block them even when `ufw` allows them.

## Optional: sign-up website and editor
See [WEBSITE.md](WEBSITE.md). Put them behind HTTPS on a domain you own.

## Backups
```bash
docker compose --env-file config/godswar.env exec -T postgres pg_dump -U godswar godswar | gzip > local/backup-$(date +%F).sql.gz
```
Test a restore before you rely on it. Never restore an old backup over a running server.

## Updating
```bash
git pull && docker compose --env-file config/godswar.env up -d --build
```
Take a backup first and update when no players are online.
