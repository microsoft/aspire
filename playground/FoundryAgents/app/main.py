import datetime
import json
import os
import random

from agent_framework import Agent, tool
from agent_framework.foundry import FoundryChatClient
from agent_framework_foundry_hosting import ResponsesHostServer
from azure.identity import DefaultAzureCredential
from starlette.requests import Request
from starlette.responses import JSONResponse
from starlette.routing import Route


@tool(name="get_forecast", description="Get a weather forecast")
async def get_forecast() -> str:
    """Get a weather forecast for the next 5 days."""
    try:
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

        return json.dumps(forecast, indent=2)
    except Exception as e:
        return json.dumps({"error": str(e)})


def main():
    """Main function to run the agent as a web server."""
    project_endpoint = os.environ.get("PROJ_MYPROJECT_URI") or os.environ.get("ConnectionStrings__proj-myproject", "")
    deployment_name = os.environ.get("CHAT_MODELNAME", "chat")

    # Parse endpoint from connection string format if needed (Endpoint=https://...)
    if project_endpoint.startswith("Endpoint="):
        project_endpoint = project_endpoint.split("Endpoint=", 1)[1].split(";")[0]

    client = FoundryChatClient(
        project_endpoint=project_endpoint,
        model=deployment_name,
        credential=DefaultAzureCredential(),
    )

    agent = Agent(
        client=client,
        name="weather-agent",
        instructions="""You are the Weather Intelligence Agent that can return weather forecast using your tools.""",
        tools=[get_forecast],
        default_options={"store": False},
    )

    async def liveness(request: Request) -> JSONResponse:
        return JSONResponse({"status": "healthy"})

    port = int(os.environ.get("DEFAULT_AD_PORT", "8088"))
    server = ResponsesHostServer(agent, routes=[Route("/liveness", liveness, methods=["GET"])])
    server.run(port=port)


if __name__ == "__main__":
    main()