import assert from "node:assert/strict";
import fs from "node:fs/promises";

const script = await fs.readFile(process.argv[2], "utf8");
const execute = new Function("github", "context", `return (async () => {${script}})();`);
const listWorkflowRuns = Symbol("listWorkflowRuns");

await verifySuccessfulCleanup();
await verifyPaginationFailureDoesNotDeleteRuns();

async function verifySuccessfulCleanup() {
  const calls = createCalls();
  let paginationComplete = false;
  const github = createGitHub(calls, async (method, args, map) => {
    verifyListRequest(method, args);

    const runIds = [
      ...map({ data: [{ id: 10 }, { id: 11 }] }),
      ...map({ data: [{ id: 11 }, { id: 12 }] }),
    ];
    paginationComplete = true;
    return runIds;
  }, () => paginationComplete);

  await execute(github, createContext());

  assert.deepEqual(calls.deleteWorkflowRun, [
    { owner: "microsoft", repo: "aspire", run_id: 10 },
    { owner: "microsoft", repo: "aspire", run_id: 11 },
    { owner: "microsoft", repo: "aspire", run_id: 12 },
  ]);
}

async function verifyPaginationFailureDoesNotDeleteRuns() {
  const calls = createCalls();
  const github = createGitHub(calls, async (method, args, map) => {
    verifyListRequest(method, args);
    map({ data: [{ id: 20 }] });
    throw new Error("Failed to fetch the next page");
  }, () => false);

  await assert.rejects(execute(github, createContext()), /Failed to fetch the next page/);
  assert.deepEqual(calls.deleteWorkflowRun, []);
}

function createCalls() {
  return {
    getWorkflowRun: [],
    deleteWorkflowRun: [],
  };
}

function createContext() {
  return {
    repo: {
      owner: "microsoft",
      repo: "aspire",
    },
    runId: 1234,
  };
}

function createGitHub(calls, paginate, isPaginationComplete) {
  return {
    rest: {
      actions: {
        async getWorkflowRun(args) {
          calls.getWorkflowRun.push(args);
          return { data: { workflow_id: 5678 } };
        },
        listWorkflowRuns,
        async deleteWorkflowRun(args) {
          assert.equal(isPaginationComplete(), true, "Run deletion started before pagination completed");
          calls.deleteWorkflowRun.push(args);
        },
      },
    },
    paginate,
  };
}

function verifyListRequest(method, args) {
  assert.equal(method, listWorkflowRuns);
  assert.deepEqual(args, {
    owner: "microsoft",
    repo: "aspire",
    workflow_id: 5678,
    status: "completed",
    per_page: 100,
  });
}
