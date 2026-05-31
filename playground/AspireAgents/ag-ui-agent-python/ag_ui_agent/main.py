"""AG-UI agent served with FastAPI and Microsoft Agent Framework."""

import os

from agent_framework.ag_ui import add_agent_framework_fastapi_endpoint
from agent_framework.foundry import FoundryChatClient
from azure.identity import DefaultAzureCredential
from fastapi import FastAPI


def _read_foundry_connections() -> tuple[str, str]:
    project_values = _parse_connection_string(os.environ.get("ConnectionStrings__agentsproject", ""))
    chat_values = _parse_connection_string(os.environ.get("ConnectionStrings__chat", ""))

    endpoint = project_values.get("Endpoint")
    deployment = chat_values.get("Deployment")
    if not endpoint or not deployment:
        raise RuntimeError("ConnectionStrings__agentsproject must contain Endpoint and ConnectionStrings__chat must contain Deployment.")

    return endpoint, deployment


def _parse_connection_string(connection_string: str) -> dict[str, str]:
    values: dict[str, str] = {}
    for part in connection_string.split(";"):
        key, separator, value = part.partition("=")
        if separator:
            values[key.strip()] = value.strip()

    return values


endpoint, deployment = _read_foundry_connections()
agent = FoundryChatClient(
    project_endpoint=endpoint,
    credential=DefaultAzureCredential(),
    model=deployment,
).as_agent(
    name="ag-ui-agent",
    instructions="You are a concise assistant exposed through AG-UI.",
)

app = FastAPI(title="AG-UI Agent")
add_agent_framework_fastapi_endpoint(app, agent, "/ag-ui")


@app.get("/health")
async def health() -> dict[str, str]:
    return {"status": "healthy"}
