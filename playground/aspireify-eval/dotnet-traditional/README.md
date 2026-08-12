# BoardApp — .NET Traditional (Pre-Aspirification)

A traditional .NET LOB app with a Vue frontend. This app is **intentionally not aspirified** — it represents the "before" state for evaluating the `aspireify` skill.

## Architecture

```text
frontend/          → Vue 3 + Vite (port 5173), proxies /api/* to BoardApi
src/BoardApi/      → ASP.NET minimal API (port 5220), EF Core + Postgres, Redis caching
src/AdminDashboard/→ Blazor Server (port 5230), shares DB with BoardApi
src/MigrationRunner/→ Worker service, runs EF Core migrations then exits
src/BoardData/     → Class library, shared EF Core DbContext + models
BoardApp.slnx      → Solution file tying it all together
.env               → All config: DB connection, Redis, API keys, secrets
```

## Dependencies

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 20+](https://nodejs.org/)
- PostgreSQL (localhost:5432)
- Redis (localhost:6379)

## Configuration

All config lives in `.env` at the repo root:

| Variable | Purpose | Secret? |
|----------|---------|---------|
| `DATABASE_URL` | Postgres connection string | Yes (contains password) |
| `REDIS_URL` | Redis host:port | No |
| `EXTERNAL_API_KEY` | Third-party notification service key | Yes |
| `ADMIN_SECRET` | Admin dashboard auth token | Yes |
| `FEATURE_ENABLE_NOTIFICATIONS` | Feature flag | No |

Each .NET service reads these via `Environment.GetEnvironmentVariable()`. The frontend reads `API_URL` in `vite.config.ts` for the dev proxy target.

## Running locally (without Aspire)

You need 4 terminal windows and 2 infrastructure services running.

### 1. Start infrastructure

```bash
# Terminal 1: Start Postgres (or use an existing instance)
docker run -d --name boardapp-pg \
  -e POSTGRES_PASSWORD=localdev123 \
  -e POSTGRES_DB=boardapp \
  -p 5432:5432 \
  postgres:16

# Terminal 1: Start Redis
docker run -d --name boardapp-redis \
  -p 6379:6379 \
  redis:7
```

### 2. Load environment variables

```bash
# In each terminal where you run a .NET service:
export $(cat .env | xargs)
```

### 3. Run database migrations

```bash
# Terminal 2:
cd src/MigrationRunner
dotnet run
# Wait for "Migrations complete." then this process exits
```

### 4. Start the API

```bash
# Terminal 2 (reuse after migrations):
cd src/BoardApi
dotnet run --urls http://localhost:5220
```

### 5. Start the admin dashboard

```bash
# Terminal 3:
cd src/AdminDashboard
dotnet run --urls http://localhost:5230
```

### 6. Start the frontend

```bash
# Terminal 4:
cd frontend
npm install
npm run dev
# Opens on http://localhost:5173
```

## Verifying it works

- **Frontend**: http://localhost:5173 — should show "BoardApp" with a list of seeded items
- **API health**: http://localhost:5220/api/health — should return `{"status":"healthy"}`
- **API items**: http://localhost:5220/api/items — should return seeded board items
- **Admin stats**: http://localhost:5230/admin/stats — should return item/user counts
- **Cached count**: http://localhost:5220/api/cached-count — tests Redis connectivity
