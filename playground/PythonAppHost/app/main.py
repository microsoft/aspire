import contextlib
import datetime
import json
import logging
import os
import random

import fastapi
import fastapi.responses
import fastapi.staticfiles
import opentelemetry.instrumentation.fastapi as otel_fastapi
import opentelemetry.instrumentation.redis as otel_redis
import redis
import telemetry


@contextlib.asynccontextmanager
async def lifespan(app):
    telemetry.configure_opentelemetry()
    yield


app = fastapi.FastAPI(lifespan=lifespan)
otel_fastapi.FastAPIInstrumentor.instrument_app(app, exclude_spans=["send"])
otel_redis.RedisInstrumentor().instrument()
logger = logging.getLogger(__name__)
redis_client = None


def get_redis_client():
    """Get the Redis client instance."""
    global redis_client
    if redis_client is None:
        if cache_uri := os.environ.get("CACHE_URI"):
            try:
                redis_client = redis.from_url(
                    cache_uri,
                    socket_connect_timeout=5,
                    socket_timeout=5,
                    decode_responses=True,
                )
            except Exception as exc:
                logger.warning(f"Failed to connect to Redis: {exc}")
                redis_client = None
        else:
            logger.info("No CACHE_URI environment variable found, Redis caching disabled")
    return redis_client


if not os.path.exists("static"):
    @app.get("/", response_class=fastapi.responses.HTMLResponse)
    async def root():
        return "API service is running. Navigate to <a href='/api/weatherforecast'>/api/weatherforecast</a> to see sample data."


@app.get("/api/weatherforecast")
async def weather_forecast(redis_client=fastapi.Depends(get_redis_client)):
    cache_key = "weatherforecast"
    cache_ttl = 5

    if redis_client:
        try:
            cached_data = redis_client.get(cache_key)
            if cached_data:
                logger.info("Returning cached weather forecast data")
                return json.loads(cached_data)
        except Exception as exc:
            logger.warning(f"Redis cache read error: {exc}")

    summaries = [
        "Freezing",
        "Bracing",
        "Chilly",
        "Cool",
        "Mild",
        "Warm",
        "Balmy",
        "Hot",
        "Sweltering",
        "Scorching",
    ]

    forecast = []
    for index in range(1, 6):
        temp_c = random.randint(-20, 55)
        forecast_date = datetime.datetime.now() + datetime.timedelta(days=index)
        forecast_item = {
            "date": forecast_date.isoformat(),
            "temperatureC": temp_c,
            "temperatureF": int(temp_c * 9 / 5) + 32,
            "summary": random.choice(summaries),
        }
        forecast.append(forecast_item)

    if redis_client:
        try:
            redis_client.setex(cache_key, cache_ttl, json.dumps(forecast))
        except Exception as exc:
            logger.warning(f"Redis cache write error: {exc}")

    return forecast


@app.get("/health", response_class=fastapi.responses.PlainTextResponse)
async def health_check(redis_client=fastapi.Depends(get_redis_client)):
    if redis_client:
        redis_client.ping()
    return "Healthy"


if os.path.exists("static"):
    app.mount(
        "/",
        fastapi.staticfiles.StaticFiles(directory="static", html=True),
        name="static",
    )
