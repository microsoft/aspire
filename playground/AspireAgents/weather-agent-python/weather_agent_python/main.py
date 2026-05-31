"""A2A JSON-RPC agent served with FastAPI and Microsoft Agent Framework."""

import logging
import os

from a2a.server.request_handlers import DefaultRequestHandler
from a2a.server.routes import create_agent_card_routes, create_jsonrpc_routes
from a2a.server.tasks import InMemoryTaskStore
from a2a.types import AgentCapabilities, AgentCard, AgentInterface, AgentSkill
from agent_framework.foundry import FoundryChatClient
from agent_framework.observability import configure_otel_providers
from agent_framework_a2a import A2AExecutor
from azure.identity import DefaultAzureCredential
from fastapi import FastAPI
from opentelemetry.instrumentation.fastapi import FastAPIInstrumentor

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)


def _read_foundry_connections() -> tuple[str, str]:
    project_connection_string = os.environ.get("ConnectionStrings__agentsproject", "")
    chat_connection_string = os.environ.get("ConnectionStrings__chat", "")

    project_values = _parse_connection_string(project_connection_string)
    chat_values = _parse_connection_string(chat_connection_string)

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


class AgentExecutor(A2AExecutor):
    """A2A executor that adapts a Microsoft Agent Framework agent."""

    def __init__(self) -> None:
        endpoint, deployment = _read_foundry_connections()
        agent = FoundryChatClient(
            project_endpoint=endpoint,
            credential=DefaultAzureCredential(),
            model=deployment,
        ).as_agent(
            name="a2a-jsonrpc-agent",
            instructions="You are a concise assistant exposed through the Agent2Agent JSON-RPC protocol.",
        )
        super().__init__(agent, stream=False)


def _get_agent_card(agent_url: str) -> AgentCard:
    return AgentCard(
        name="a2a-jsonrpc-agent",
        description="Basic agent exposed through the Agent2Agent JSON-RPC protocol.",
        version="1.0.0",
        default_input_modes=["text"],
        default_output_modes=["text"],
        supported_interfaces=[
            AgentInterface(
                url=agent_url,
                protocol_binding="JSONRPC",
                protocol_version="1.0",
            )
        ],
        capabilities=AgentCapabilities(streaming=False, push_notifications=False),
        skills=[
            AgentSkill(
                id="chat",
                name="Chat",
                description="Answers user prompts with the Foundry model deployment.",
                examples=["Hello, what can you do?"],
                tags=["chat"],
            )
        ],
    )


def create_app() -> FastAPI:
    configure_otel_providers(enable_sensitive_data=True)

    port = int(os.environ.get("PORT", "8080"))
    base_url = os.environ.get("A2A_AGENT_BASE_URL", f"http://localhost:{port}")
    agent_url = f"{base_url.rstrip('/')}/"
    agent_card = _get_agent_card(agent_url)
    handler = DefaultRequestHandler(
        agent_executor=AgentExecutor(),
        task_store=InMemoryTaskStore(),
        agent_card=agent_card,
    )

    app = FastAPI(
        routes=[
            *create_agent_card_routes(agent_card),
            *create_jsonrpc_routes(handler, "/"),
        ]
    )

    FastAPIInstrumentor().instrument_app(app)

    @app.get("/health")
    async def health() -> dict[str, str]:
        return {"status": "healthy"}

    return app


app = create_app()
