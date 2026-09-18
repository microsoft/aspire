"""AG-UI and ACP agent served with Microsoft Agent Framework."""

import os

from acp_sdk.models import Message, MessagePart
from acp_sdk.server import Context, Server
from acp_sdk.server.app import create_app
from agent_framework.ag_ui import add_agent_framework_fastapi_endpoint
from agent_framework.foundry import FoundryChatClient
from azure.identity import DefaultAzureCredential

server = Server()


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


def _get_text(input: list[Message]) -> str:
    parts: list[str] = []
    for message in input:
        for part in message.parts:
            if part.content:
                parts.append(part.content)

    return " ".join(parts).strip() or "Hello"


endpoint, deployment = _read_foundry_connections()
agent = FoundryChatClient(
    project_endpoint=endpoint,
    credential=DefaultAzureCredential(),
    model=deployment,
).as_agent(
    name="agui-acp-agent",
    instructions="You are a concise assistant exposed through AG-UI and ACP.",
)


@server.agent(
    name="agui-acp-agent",
    description="Foundry-backed AG-UI and ACP agent.",
    input_content_types=["text/plain"],
    output_content_types=["text/plain"],
)
async def acp_agent(input: list[Message], context: Context) -> Message:
    """Run the Microsoft Agent Framework agent and return its response over ACP."""
    del context

    response = await agent.run(_get_text(input))
    return Message(
        role="agent/agui-acp-agent",
        parts=[
            MessagePart(
                content_type="text/plain",
                content=response.text,
            )
        ],
    )


app = create_app(*server.agents)
add_agent_framework_fastapi_endpoint(app, agent, "/ag-ui")


@app.get("/health")
async def health() -> dict[str, str]:
    return {"status": "healthy"}
