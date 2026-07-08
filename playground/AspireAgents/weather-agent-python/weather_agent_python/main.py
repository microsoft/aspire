"""A2A JSON-RPC agent served with FastAPI, LangChain, and the official A2A SDK."""

import logging
import os

from a2a.server.agent_execution.agent_executor import AgentExecutor
from a2a.server.agent_execution.context import RequestContext
from a2a.server.events.event_queue import EventQueue
from a2a.server.request_handlers import DefaultRequestHandler
from a2a.server.routes import create_agent_card_routes, create_jsonrpc_routes
from a2a.server.tasks import InMemoryTaskStore
from a2a.server.tasks.task_updater import TaskUpdater
from a2a.types import (
    AgentCapabilities,
    AgentCard,
    AgentInterface,
    AgentProvider,
    AgentSkill,
    Part,
    Task,
    TaskState,
    TaskStatus,
)
from azure.identity import DefaultAzureCredential
from fastapi import FastAPI
from langchain.agents import create_agent
from langchain_azure_ai.chat_models import AzureAIOpenAIApiChatModel
from langchain_core.messages import BaseMessage
from langchain_core.tools import tool
from opentelemetry.instrumentation.fastapi import FastAPIInstrumentor


logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)


def _read_foundry_connections() -> tuple[str, str]:
    project_values = _parse_connection_string(os.environ.get("ConnectionStrings__agentsproject", ""))
    chat_values = _parse_connection_string(os.environ.get("ConnectionStrings__chat", ""))

    endpoint = project_values.get("Endpoint")
    deployment = chat_values.get("Deployment")
    if not endpoint or not deployment:
        raise RuntimeError("ConnectionStrings__agentsproject must contain Endpoint and ConnectionStrings__chat must contain Deployment.")

    return _normalize_foundry_model_endpoint(endpoint), deployment


def _parse_connection_string(connection_string: str) -> dict[str, str]:
    values: dict[str, str] = {}
    for part in connection_string.split(";"):
        key, separator, value = part.partition("=")
        if separator:
            values[key.strip()] = value.strip()

    return values


def _normalize_foundry_model_endpoint(endpoint: str) -> str:
    endpoint = endpoint.rstrip("/")
    project_separator = "/api/projects/"
    if project_separator in endpoint:
        endpoint = endpoint.split(project_separator, maxsplit=1)[0]

    return endpoint if endpoint.endswith("/openai/v1") else f"{endpoint}/openai/v1"


@tool
def get_playground_weather(location: str) -> str:
    """Get a deterministic playground weather report for a location."""
    return f"The playground forecast for {location} is sunny with a chance of distributed traces."


class LangChainAgent:
    """LangChain agent used by the Aspire A2A playground."""

    def __init__(self) -> None:
        endpoint, deployment = _read_foundry_connections()
        model = AzureAIOpenAIApiChatModel(
            endpoint=endpoint,
            credential=DefaultAzureCredential(),
            model=deployment,
            temperature=0,
        )
        self._agent = create_agent(
            model=model,
            tools=[get_playground_weather],
            system_prompt="You are a concise assistant exposed through the Agent2Agent JSON-RPC protocol.",
        )

    async def answer(self, question: str) -> str:
        """Answer a user question using the LangChain agent."""
        result = await self._agent.ainvoke(
            {"messages": [{"role": "user", "content": question or "Hello"}]}
        )
        messages = result.get("messages", [])
        if not messages:
            return "The LangChain agent did not return a response."

        return _message_content_to_text(messages[-1])


def _message_content_to_text(message: BaseMessage) -> str:
    content = message.content
    if isinstance(content, str):
        return content

    if isinstance(content, list):
        parts: list[str] = []
        for block in content:
            if isinstance(block, str):
                parts.append(block)
            elif isinstance(block, dict):
                text = block.get("text") or block.get("content")
                if isinstance(text, str):
                    parts.append(text)

        if parts:
            return "\n".join(parts)

    return str(content)


class AspireAgentExecutor(AgentExecutor):
    """Small A2A SDK executor used by the Aspire playground."""

    def __init__(self) -> None:
        self._running_tasks: set[str] = set()
        self._agent: LangChainAgent | None = None

    async def cancel(self, context: RequestContext, event_queue: EventQueue) -> None:
        """Cancel the running task if it is still active."""
        task_id = context.task_id
        if task_id is not None:
            self._running_tasks.discard(task_id)

        updater = TaskUpdater(
            event_queue=event_queue,
            task_id=task_id or "",
            context_id=context.context_id or "",
        )
        await updater.cancel()

    async def execute(self, context: RequestContext, event_queue: EventQueue) -> None:
        """Execute a task by passing the user message to the LangChain agent."""
        user_message = context.message
        task_id = context.task_id
        context_id = context.context_id

        if user_message is None or task_id is None or context_id is None:
            return

        self._running_tasks.add(task_id)
        logger.info("Processing A2A message %s for task %s.", user_message.message_id, task_id)

        await event_queue.enqueue_event(
            Task(
                id=task_id,
                context_id=context_id,
                status=TaskStatus(state=TaskState.TASK_STATE_SUBMITTED),
                history=[user_message],
            )
        )

        updater = TaskUpdater(event_queue=event_queue, task_id=task_id, context_id=context_id)
        await updater.start_work(
            message=updater.new_agent_message(parts=[Part(text="Processing your message...")])
        )

        query = context.get_user_input()
        agent = self._get_agent()
        response_text = await agent.answer(query)
        if task_id not in self._running_tasks:
            return

        await updater.add_artifact(
            parts=[Part(text=response_text)],
            name="response",
            last_chunk=True,
        )
        await updater.complete()
        self._running_tasks.discard(task_id)

    def _get_agent(self) -> LangChainAgent:
        if self._agent is None:
            self._agent = LangChainAgent()

        return self._agent


def _get_agent_card(agent_url: str) -> AgentCard:
    return AgentCard(
        name="a2a-jsonrpc-agent",
        description="Basic agent exposed through the Agent2Agent JSON-RPC protocol.",
        provider=AgentProvider(organization="Aspire", url="https://github.com/microsoft/aspire"),
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
                description="Answers prompts through a LangChain-backed playground agent.",
                examples=["Hello, what can you do?"],
                tags=["chat"],
            )
        ],
    )


def create_app() -> FastAPI:
    port = int(os.environ.get("PORT", "8080"))
    base_url = os.environ.get("A2A_AGENT_BASE_URL", f"http://localhost:{port}")
    agent_url = f"{base_url.rstrip('/')}/"
    agent_card = _get_agent_card(agent_url)
    handler = DefaultRequestHandler(
        agent_executor=AspireAgentExecutor(),
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
