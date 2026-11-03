# GodsWar

An open, community-run server for **GodsWar Online** (2.51 client protocol), with the tools
around it: account sign-up site, web editor for characters/items/rates, and a launcher that
downloads and updates a client.

> The game client and its art belong to their owners and are **not** included. You supply
> your own copy of the client. See [docs/CLIENT.md](docs/CLIENT.md).

## Quick start

```bash
git clone <this repository> godswar && cd godswar
./setup.sh
```

The script asks whether you are running **on this computer** or on **a VPS**, installs what
is missing (Docker), generates all passwords, builds and starts the server, and creates your
administrator account. Details: [docs/INSTALL-LOCAL.md](docs/INSTALL-LOCAL.md) and
[docs/INSTALL-VPS.md](docs/INSTALL-VPS.md).

## What is in this folder

| Folder | What it is |
|---|---|
| `setup.sh` | Guided install (local or VPS) |
| `config/` | `godswar.env.example` → your `godswar.env` (ignored by git). Every address and secret comes from here |
| `server/` | The game server (C# / .NET 10, PostgreSQL), GPL-3. Start at `server/README.md` |
| `editor/` | Web editor: characters, items, rates, accounts |
| `signup/` | Public sign-up / password-reset website |
| `launcher/` | Player launcher with self-updating client (Go) |
| `tools/` | Helper scripts (admin account, update-signing key) |
| `docs/` | Guides |
| `local/` | **Your private files.** Ignored by git |

## Working on it without leaking your own details

Settings, addresses, keys and notes never go in tracked files. Keep them in
`config/godswar.env` and `local/`; both are git-ignored. Before sending a change, run
`python3 -I tools/leak-scan.py` — it fails if your IP, home paths or keys are in the tree.
See [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md).

## Editions

`GODSWAR_EDITION=classic` runs the original game's limits; `reborn` (default) lifts them
(gear quality 20 / grade 25). See [docs/EDITIONS.md](docs/EDITIONS.md).

## Credits

The server descends from open-source work: [godswar-server-reborn](https://github.com/hejing900/godswar-server-reborn)
(C#/.NET, hejing900), itself a port of [sush1lemon/godswar-server](https://github.com/sush1lemon/godswar-server)
(Go) and based on [Godswar-Reborn](https://github.com/p5y-Ph3R/Godswar-Reborn). This repository contains
modified versions (changes from 2026-10 onward). GodsWar Online is the property of its owners; this is an
unofficial preservation project and is not affiliated with them.

## Licence

GPL-3.0, see `server/LICENSE`. Credits and upstream projects: `server/NOTICE.md`.
