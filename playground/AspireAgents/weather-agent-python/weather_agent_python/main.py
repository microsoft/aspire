"""A2A JSON-RPC agent served with FastAPI and the official A2A SDK."""

import asyncio
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
from fastapi import FastAPI
from opentelemetry.instrumentation.fastapi import FastAPIInstrumentor


logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)


class AspireAgentExecutor(AgentExecutor):
    """Small A2A SDK executor used by the Aspire playground."""

    def __init__(self) -> None:
        self._running_tasks: set[str] = set()

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
        """Execute a task with simple deterministic agent behavior."""
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
        response_text = self._answer(query)
        await asyncio.sleep(0)

        if task_id not in self._running_tasks:
            return

        await updater.add_artifact(
            parts=[Part(text=response_text)],
            name="response",
            last_chunk=True,
        )
        await updater.complete()
        self._running_tasks.discard(task_id)

    @staticmethod
    def _answer(query: str) -> str:
        if not query:
            return "Hello from the Aspire A2A playground agent."

        normalized = query.lower()
        if "weather" in normalized:
            return "The playground forecast is sunny with a chance of distributed traces."

        return f"Hello from the Aspire A2A playground agent. You said: {query}"


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
                description="Answers prompts with deterministic playground responses.",
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
