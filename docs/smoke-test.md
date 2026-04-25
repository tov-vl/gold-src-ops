# GoldSrcOps Smoke Test

This smoke test exercises the local PostgreSQL-backed API against a live GoldSrc server.

## 1. Start Local Infrastructure

```powershell
docker compose -f .\ops\docker-compose.yml up -d postgres
```

PostgreSQL listens on `localhost:5432` with database/user/password `goldsrcops`.

## 2. Apply Migrations

```powershell
dotnet tool restore
dotnet tool run dotnet-ef database update --project .\src\GoldSrcOps.Infrastructure --startup-project .\src\GoldSrcOps.Api
```

## 3. Run The API

```powershell
dotnet run --project .\src\GoldSrcOps.Api --launch-profile http
```

The HTTP profile listens on `http://localhost:5142`.

## 4. Register A Live Server

Open a second terminal:

```powershell
$baseUrl = "http://localhost:5142"

$body = @{
  name = "CSOMOD Zombie Server"
  host = "server.csomod.com"
  queryPort = 27015
  rconPort = $null
  pollIntervalSeconds = 30
  notes = "Live smoke test target"
} | ConvertTo-Json

$server = Invoke-RestMethod `
  -Method Post `
  -Uri "$baseUrl/api/servers" `
  -ContentType "application/json" `
  -Body $body

$server
```

## 5. Check Monitoring Reads

The background poller runs every few seconds. Wait briefly, then query status and history:

```powershell
Start-Sleep -Seconds 10

Invoke-RestMethod "$baseUrl/api/servers/$($server.id)/status"
Invoke-RestMethod "$baseUrl/api/servers/$($server.id)/snapshots?limit=10"
Invoke-RestMethod "$baseUrl/api/dashboard/overview"
Invoke-RestMethod "$baseUrl/api/servers/$($server.id)/incidents"
```

Expected result:

- `/status` eventually reports `Online` if the live server is reachable.
- `/snapshots` contains at least one poll attempt.
- `/dashboard/overview` includes the registered server in its counts.
- `/incidents` remains empty while polling succeeds.

If the live server is offline or blocks the query, the status can be `Offline` and an incident can open after the configured failure threshold. In that case, the smoke test still verifies the failure path.
