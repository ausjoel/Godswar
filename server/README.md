# GodsWar server

C# / .NET 10 game and login server backed by PostgreSQL. Normally you run it through the
top-level `setup.sh` (Docker). To build it directly:

```bash
dotnet build GodswarServer.sln -c Release
dotnet run --project src/Godswar.Server -c Release     # reads appsettings.json and GODSWAR_* environment variables
dotnet run --project tests/Godswar.Server.ProtocolChecks -c Release   # protocol checks (several minutes)
```
Settings: `appsettings.json` and `GODSWAR_*` environment variables (see `.env.example`).
Database migrations in `database/postgres` and the server's embedded migrations apply on start.

Credits and licence: `LICENSE` (GPL-3.0) and `NOTICE.md`.
