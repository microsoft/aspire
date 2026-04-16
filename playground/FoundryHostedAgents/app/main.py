import asyncio
import datetime
import json
import os
import random

# Microsoft Agent Framework
from agent_framework import Agent, tool
from agent_framework.azure import AzureOpenAIChatClient
from azure.ai.agentserver.agentframework import from_agent_framework
from azure.identity import DefaultAzureCredential

@tool(name="get_forecast", description="Get a weather forecast")
async def get_forecast() -> str:
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
        for index in range(1, 6):  # Range 1 to 5 (inclusive)
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

async def main():
    """Main function to run the agent as a web server."""

    # client = FoundryChatClient(project_endpoint=os.getenv("CHAT_URI"), credential=AzureCliCredential(), model="chat")
    agent = AzureOpenAIChatClient(endpoint=os.getenv("CHAT_URI"), credential=DefaultAzureCredential(), deployment_name="chat").as_agent(
        # client = client,
        name="weather-agent",
        instructions="""You are the Weather Intelligence Agent that can return weather forecast using your tools.""",
        tools=[get_forecast],
    )


    app = from_agent_framework(agent)

    await app.run_async()


if __name__ == "__main__":
    asyncio.run(main())