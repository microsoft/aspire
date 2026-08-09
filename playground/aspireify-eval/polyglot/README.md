# CityServices — Polyglot (Pre-Aspirification)

A polyglot microservices app with Python, Go, C#, and React. This app is **intentionally not aspirified** — it represents the "before" state for evaluating the `aspireify` skill.

## Architecture

```text
api-weather/    → Python FastAPI (port 8001), weather data with Redis caching
api-geo/        → Go stdlib HTTP (port 8002), geocoding stub with external API key
api-events/     → C# minimal API (port 8003), city events endpoint
frontend/       → React + Vite (port 5173), calls all three APIs directly
.env            → All config: Redis URL, API keys
```

No solution file, no AppHost — just four independent services that talk to each other via hardcoded URLs.

## Dependencies

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 20+](https://nodejs.org/)
- [Python 3.12+](https://python.org/) with pip
- [Go 1.23+](https://go.dev/)
- Redis (localhost:6379)

## Configuration

All config lives in `.env` at the repo root:

| Variable | Purpose | Secret? |
|----------|---------|---------|
| `REDIS_URL` | Redis host:port for weather cache | No |
| `GEOCODING_API_KEY` | External geocoding API key | Yes |
| `WEATHER_API_KEY` | External weather API key | Yes |
| `OPENAI_API_KEY` | OpenAI API key (reserved for future advisor feature) | Yes |

Services read these via `os.environ` (Python), `os.Getenv` (Go), or aren't wired yet (C#, React).

The React frontend has **hardcoded backend URLs** in `App.tsx`:
```typescript
const WEATHER_API = import.meta.env.VITE_WEATHER_API_URL || 'http://localhost:8001';
const GEO_API = import.meta.env.VITE_GEO_API_URL || 'http://localhost:8002';
const EVENTS_API = import.meta.env.VITE_EVENTS_API_URL || 'http://localhost:8003';
```

## Running locally (without Aspire)

You need 5 terminal windows and Redis running.

### 1. Start infrastructure

```bash
# Terminal 1: Start Redis
docker run -d --name cityservices-redis \
  -p 6379:6379 \
  redis:7
```

### 2. Load environment variables

```bash
# In each terminal:
export $(cat .env | xargs)
```

### 3. Start the weather API (Python)

```bash
# Terminal 2:
cd api-weather
pip install -r requirements.txt
uvicorn main:app --host 0.0.0.0 --port 8001
```

### 4. Start the geo API (Go)

```bash
# Terminal 3:
cd api-geo
PORT=8002 go run .
```

### 5. Start the events API (C#)

```bash
# Terminal 4:
cd api-events
dotnet run --urls http://localhost:8003
```

### 6. Start the frontend (React)

```bash
# Terminal 5:
cd frontend
npm install
npm run dev
# Opens on http://localhost:5173
```

## Verifying it works

- **Frontend**: http://localhost:5173 — should show "CityServices" with weather, geo, and events data for Seattle
- **Weather API**: http://localhost:8001/weather/seattle — should return weather stub data
- **Weather cities**: http://localhost:8001/cities — should return list of cities
- **Geo API**: http://localhost:8002/geocode/seattle — should return lat/lng stub
- **Events API**: http://localhost:8003/events — should return all events
- **Events by city**: http://localhost:8003/events/seattle — should return Seattle events
- **Health checks**: each service has `/health` returning its name and status
