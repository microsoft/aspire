import os
import json
from fastapi import FastAPI
import redis
import httpx

app = FastAPI(title="Weather API")

redis_url = os.environ.get("REDIS_URL", "localhost:6379")
weather_api_key = os.environ.get("WEATHER_API_KEY", "")
redis_host, redis_port = redis_url.split(":")
cache = redis.Redis(host=redis_host, port=int(redis_port), decode_responses=True)


@app.get("/health")
def health():
    return {"status": "healthy", "service": "api-weather"}


@app.get("/weather/{city}")
async def get_weather(city: str):
    # Check cache first
    cached = cache.get(f"weather:{city}")
    if cached:
        return json.loads(cached)

    # Stub weather data (would call external API with weather_api_key)
    data = {
        "city": city,
        "temp_f": 72,
        "condition": "Partly Cloudy",
        "humidity": 45,
        "source": "stub",
        "api_key_configured": bool(weather_api_key),
    }

    cache.setex(f"weather:{city}", 300, json.dumps(data))
    return data


@app.get("/cities")
def list_cities():
    return ["seattle", "new-york", "san-francisco", "chicago", "austin"]
