import asyncio
import unittest

from a2a.server.agent_execution.context import RequestContext
from a2a.server.context import ServerCallContext
from a2a.server.events.event_queue import Event, EventQueue
from a2a.types import Message, Part, SendMessageRequest, TaskArtifactUpdateEvent, TaskState, TaskStatusUpdateEvent
from a2a.utils.errors import TaskNotCancelableError

from weather_agent_python.main import AspireAgentExecutor, LangChainAgent


class ExecutorTests(unittest.IsolatedAsyncioTestCase):
    async def test_cancellation_stops_each_awaiting_stage(self) -> None:
        for stage in ("answer", "artifact", "complete"):
            with self.subTest(stage=stage):
                agent = TestAgent(block=stage == "answer")
                executor = AspireAgentExecutor()
                executor._agent = agent
                queue = TestEventQueue(block_stage=stage)
                context = create_context()
                execution = asyncio.create_task(executor.execute(context, queue))
                self.addAsyncCleanup(cancel_if_running, execution)

                async with asyncio.timeout(5):
                    await (agent.started if stage == "answer" else queue.blocked).wait()
                    await executor.cancel(context, queue)

                self.assertTrue(execution.cancelled())
                self.assertEqual({}, executor._running_tasks)
                states = [event.status.state for event in queue.events if isinstance(event, TaskStatusUpdateEvent)]
                self.assertEqual([TaskState.TASK_STATE_WORKING, TaskState.TASK_STATE_CANCELED], states)
                artifacts = [event for event in queue.events if isinstance(event, TaskArtifactUpdateEvent)]
                self.assertEqual(1 if stage == "complete" else 0, len(artifacts))
                self.assertEqual(TaskState.TASK_STATE_CANCELED, queue.events[-1].status.state)

    async def test_completed_task_cannot_be_canceled(self) -> None:
        executor = AspireAgentExecutor()
        executor._agent = TestAgent()
        queue = TestEventQueue()
        context = create_context()
        await asyncio.create_task(executor.execute(context, queue))

        with self.assertRaises(TaskNotCancelableError):
            await executor.cancel(context, queue)

        self.assertEqual({}, executor._running_tasks)
        states = [event.status.state for event in queue.events if isinstance(event, TaskStatusUpdateEvent)]
        self.assertEqual([TaskState.TASK_STATE_WORKING, TaskState.TASK_STATE_COMPLETED], states)
        artifacts = [event for event in queue.events if isinstance(event, TaskArtifactUpdateEvent)]
        self.assertEqual(["hello"], [event.artifact.parts[0].text for event in artifacts])

    async def test_failed_task_is_removed(self) -> None:
        executor = AspireAgentExecutor()
        executor._agent = TestAgent(fail=True)
        with self.assertRaisesRegex(RuntimeError, "Model failed"):
            await asyncio.create_task(executor.execute(create_context(), TestEventQueue()))
        self.assertEqual({}, executor._running_tasks)


def create_context() -> RequestContext:
    return RequestContext(
        call_context=ServerCallContext(),
        task_id="task-id",
        context_id="context-id",
        request=SendMessageRequest(message=Message(message_id="message-id", parts=[Part(text="hello")])),
    )


async def cancel_if_running(task: asyncio.Task[None]) -> None:
    if not task.done():
        task.cancel()
        try:
            await task
        except asyncio.CancelledError:
            pass


class TestAgent(LangChainAgent):
    def __init__(self, block: bool = False, fail: bool = False) -> None:
        self.started = asyncio.Event()
        self.block = block
        self.fail = fail

    async def answer(self, question: str) -> str:
        self.started.set()
        if self.block:
            await asyncio.Event().wait()
        if self.fail:
            raise RuntimeError("Model failed")
        return question


class TestEventQueue(EventQueue):
    def __init__(self, block_stage: str | None = None) -> None:
        self.events: list[Event] = []
        self.blocked = asyncio.Event()
        self.block_stage = block_stage

    async def enqueue_event(self, event: Event) -> None:
        is_artifact = isinstance(event, TaskArtifactUpdateEvent)
        is_completed = isinstance(event, TaskStatusUpdateEvent) and event.status.state == TaskState.TASK_STATE_COMPLETED
        if (self.block_stage == "artifact" and is_artifact) or (self.block_stage == "complete" and is_completed):
            self.blocked.set()
            await asyncio.Event().wait()
        self.events.append(event)
