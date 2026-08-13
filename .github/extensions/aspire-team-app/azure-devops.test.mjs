import assert from "node:assert/strict";
import test from "node:test";

import {
  AzureDevOpsError,
  azurePipelineIdFromRemovalKey,
  azurePipelineRemovalKey,
  discoverAzureDevOpsPipelines,
  loadAzureDevOpsPipelineHealth,
  parseAzureDevOpsPipelineUrl,
  parseAzureDevOpsDefaults,
  resolveAzureCliCommand,
  resolveAzureDevOpsPipeline,
  unavailableAzureDevOpsHealth,
} from "./azure-devops.mjs";

test("Azure pipeline removal keys are opaque, stable, and project-specific", () => {
  const firstId = "azdo:dnceng/internal/1602";
  const secondId = "azdo:dnceng/other project/1602";
  const firstKey = azurePipelineRemovalKey(firstId);
  const secondKey = azurePipelineRemovalKey(secondId);

  assert.equal(azurePipelineIdFromRemovalKey(firstKey), firstId);
  assert.equal(azurePipelineRemovalKey(firstId), firstKey);
  assert.notEqual(firstKey, secondKey);
  assert.doesNotMatch(secondKey, /other project/i);
  assert.equal(azurePipelineIdFromRemovalKey(`${firstKey}x`), null);
  assert.equal(azurePipelineRemovalKey("not-an-azure-pipeline"), null);
});

test("parseAzureDevOpsPipelineUrl accepts definition, build, and legacy URLs", () => {
  assert.deepEqual(
    parseAzureDevOpsPipelineUrl("https://dev.azure.com/dnceng/internal/_build?definitionId=1602"),
    {
      organization: "https://dev.azure.com/dnceng",
      organizationName: "dnceng",
      project: "internal",
      definitionId: 1602,
      buildId: null,
      inputUrl: "https://dev.azure.com/dnceng/internal/_build?definitionId=1602",
    },
  );

  const build = parseAzureDevOpsPipelineUrl("https://dev.azure.com/dnceng/internal/_build/results?buildId=3040201&view=results");
  assert.equal(build.definitionId, null);
  assert.equal(build.buildId, 3040201);

  const legacy = parseAzureDevOpsPipelineUrl("https://dnceng.visualstudio.com/internal/_build?definitionId=1602");
  assert.equal(legacy.organization, "https://dev.azure.com/dnceng");
  assert.equal(legacy.project, "internal");
});

test("parseAzureDevOpsPipelineUrl rejects untrusted or incomplete URLs", () => {
  assert.throws(
    () => parseAzureDevOpsPipelineUrl("https://dev.azure.com.evil.example/dnceng/internal/_build?definitionId=1602"),
    (error) => error.code === "invalid_pipeline_host",
  );
  assert.throws(
    () => parseAzureDevOpsPipelineUrl("https://user:secret@dev.azure.com/dnceng/internal/_build?definitionId=1602"),
    (error) => error.code === "invalid_pipeline_url",
  );
  assert.throws(
    () => parseAzureDevOpsPipelineUrl("https://dev.azure.com/dnceng/internal/_build"),
    (error) => error.code === "missing_pipeline_id",
  );
});

test("parseAzureDevOpsDefaults reads the Azure CLI INI output conservatively", () => {
  assert.deepEqual(
    parseAzureDevOpsDefaults(`
[defaults]
organization = https://dev.azure.com/dnceng
project = aspire-msft

Use git alias = No
`),
    {
      organization: "https://dev.azure.com/dnceng",
      organizationName: "dnceng",
      project: "aspire-msft",
    },
  );
  assert.equal(parseAzureDevOpsDefaults("[defaults]\norganization = https://example.com/org\nproject = docs"), null);
});

test("resolveAzureDevOpsPipeline resolves a build URL to a normalized definition", async () => {
  const calls = [];
  const runAz = async (args) => {
    calls.push(args);
    if (args.includes("build") && args.includes("show")) {
      return { definition: { id: 1602 } };
    }
    return pipelineDefinition();
  };

  const pipeline = await resolveAzureDevOpsPipeline(
    "https://dev.azure.com/dnceng/internal/_build/results?buildId=3040201",
    { runAz },
  );

  assert.equal(pipeline.id, "azdo:dnceng/internal/1602");
  assert.equal(pipeline.name, "microsoft-aspire");
  assert.equal(pipeline.branch, "refs/heads/main");
  assert.equal(pipeline.url, "https://dev.azure.com/dnceng/internal/_build?definitionId=1602");
  assert.ok(calls[0].includes("3040201"));
  assert.ok(calls[1].includes("1602"));
});

test("Azure discovery selects and caches one production delivery pipeline for a unique repository match", async () => {
  const calls = [];
  let defaultsCalls = 0;
  const definitions = new Map([
    [1559, discoveredDefinition(1559, "aspireDev-MergeChangesFromPublic", "azure-pipelines.yml")],
    [1564, discoveredDefinition(1564, "Aspire.Dev-Build", "pipelines/build.yml")],
    [1573, discoveredDefinition(1573, "Aspire.Dev-Release-Test", "pipelines/1ES.Release.Test.yml")],
    [1576, discoveredDefinition(1576, "Aspire.Dev-Release-Production", "pipelines/1ES.Release.Production.yml")],
  ]);
  const runAz = async (args) => {
    calls.push(args);
    if (args[0] === "repos" && args[1] === "list") {
      return [
        { id: "repo-aspire-dev", name: "aspire.dev", defaultBranch: "refs/heads/main" },
        { id: "repo-other", name: "unrelated", defaultBranch: "refs/heads/main" },
      ];
    }
    if (args[0] === "pipelines" && args[1] === "list") {
      return [
        ...[...definitions.values()].map(({ id, name, path, queueStatus }) => ({ id, name, path, queueStatus })),
        { id: 1999, name: "Restricted pipeline", path: "\\aspire.dev", queueStatus: "enabled" },
      ];
    }
    if (args[0] === "pipelines" && args[1] === "show") {
      const id = Number(args[args.indexOf("--id") + 1]);
      if (id === 1999) throw new AzureDevOpsError("azdo_access_denied", "Access denied.");
      return definitions.get(id);
    }
    throw new Error(`Unexpected az call: ${args.join(" ")}`);
  };
  const options = {
    runAz,
    runAzText: async () => {
      defaultsCalls++;
      return "[defaults]\norganization = https://dev.azure.com/dnceng\nproject = aspire-msft\n";
    },
    cache: new Map(),
    nowMs: Date.parse("2026-08-06T15:00:00Z"),
  };

  const first = await discoverAzureDevOpsPipelines(
    { repositories: ["microsoft/aspire.dev"] },
    options,
  );
  const second = await discoverAzureDevOpsPipelines(
    { repositories: ["microsoft/aspire.dev"] },
    options,
  );

  assert.equal(first.pipelines.length, 1);
  assert.equal(first.pipelines[0].id, "azdo:dnceng/aspire-msft/1576");
  assert.equal(first.pipelines[0].name, "Aspire.Dev-Release-Production");
  assert.equal(first.pipelines[0].branch, "refs/heads/deploy");
  assert.equal(first.pipelines[0].repository.name, "aspire.dev");
  assert.equal(first.pipelines[0].discovered, true);
  assert.deepEqual(first.pipelines[0].discovery, {
    kind: "azure-cli-default",
    repository: "microsoft/aspire.dev",
    azureRepository: "aspire.dev",
    pipelineCandidates: 3,
  });
  assert.deepEqual(first.warnings, ["Pipeline 1999 could not be inspected: Access denied."]);
  assert.deepEqual(second, first);
  assert.equal(defaultsCalls, 1);
  assert.equal(calls.filter((args) => args[0] === "repos").length, 1);
  assert.equal(calls.filter((args) => args[0] === "pipelines" && args[1] === "list").length, 1);
  assert.ok(calls.find((args) => args[0] === "pipelines" && args[1] === "list").includes("--repository"));
  assert.equal(calls.filter((args) => args[0] === "pipelines" && args[1] === "show").length, 5);
});

test("Azure discovery bounds pipeline inspection concurrency across repositories", async () => {
  const repositoryCount = 8;
  const repositories = Array.from({ length: repositoryCount }, (_, index) => ({
    id: `repo-${index}`,
    name: `repo-${index}`,
    type: "TfsGit",
    defaultBranch: "refs/heads/main",
  }));
  const definitions = new Map(repositories.map((repository, index) => {
    const id = 2000 + index;
    return [id, {
      id,
      name: `Repo ${index} Release Production`,
      path: `\\repo-${index}`,
      queueStatus: "enabled",
      process: { type: 2, yamlFilename: "pipelines/release.yml" },
      repository,
    }];
  }));
  let activeShows = 0;
  let maxConcurrentShows = 0;
  let releaseShows;
  const showGate = new Promise((resolve) => { releaseShows = resolve; });
  let releaseScheduled = false;
  const runAz = async (args) => {
    if (args[0] === "repos" && args[1] === "list") return repositories;
    if (args[0] === "pipelines" && args[1] === "list") {
      const repositoryId = args[args.indexOf("--repository") + 1];
      const index = Number(repositoryId.replace("repo-", ""));
      const definition = definitions.get(2000 + index);
      return [{ id: definition.id, name: definition.name, path: definition.path, queueStatus: definition.queueStatus }];
    }
    if (args[0] === "pipelines" && args[1] === "show") {
      activeShows++;
      maxConcurrentShows = Math.max(maxConcurrentShows, activeShows);
      if (activeShows === 6 && !releaseScheduled) {
        releaseScheduled = true;
        setImmediate(releaseShows);
      }
      await showGate;
      activeShows--;
      return definitions.get(Number(args[args.indexOf("--id") + 1]));
    }
    throw new Error(`Unexpected az call: ${args.join(" ")}`);
  };

  const result = await discoverAzureDevOpsPipelines(
    { repositories: repositories.map((repository) => `microsoft/${repository.name}`) },
    {
      runAz,
      runAzText: async () => "[defaults]\norganization = https://dev.azure.com/dnceng\nproject = aspire-msft\n",
      cache: new Map(),
      nowMs: Date.parse("2026-08-06T15:00:00Z"),
    },
  );

  assert.equal(result.pipelines.length, repositoryCount);
  assert.equal(maxConcurrentShows, 6);
});

test("Azure discovery loads curated official Aspire pipelines without CLI defaults", async () => {
  const calls = [];
  let defaultsCalls = 0;
  const definitions = new Map([
    [1599, officialDefinition(1599, "microsoft-aspire-codeql", "eng/pipelines/azure-pipelines-codeql.yml")],
    [1600, officialDefinition(1600, "microsoft-aspire-Release-To-NuGet", "eng/pipelines/release-publish-nuget.yml")],
    [1601, officialDefinition(1601, "microsoft-aspire-unofficial", "eng/pipelines/azure-pipelines-unofficial.yml")],
    [1602, officialDefinition(1602, "microsoft-aspire", "eng/pipelines/azure-pipelines.yml")],
  ]);
  const runAz = async (args) => {
    calls.push(args);
    if (args[0] === "pipelines" && args[1] === "list") {
      return [...definitions.values()].map(({ id, name, path, queueStatus }) => ({ id, name, path, queueStatus }));
    }
    if (args[0] === "pipelines" && args[1] === "show") {
      return definitions.get(Number(args[args.indexOf("--id") + 1]));
    }
    throw new Error(`Unexpected az call: ${args.join(" ")}`);
  };
  const options = {
    runAz,
    runAzText: async () => {
      defaultsCalls++;
      return "[defaults]\n";
    },
    cache: new Map(),
    nowMs: Date.parse("2026-08-06T15:00:00Z"),
  };

  const first = await discoverAzureDevOpsPipelines({ repositories: ["microsoft/aspire"] }, options);
  const second = await discoverAzureDevOpsPipelines({ repositories: ["microsoft/aspire"] }, options);

  assert.deepEqual(first.pipelines.map((pipeline) => pipeline.definitionId), [1599, 1600, 1602]);
  assert.ok(first.pipelines.every((pipeline) => pipeline.discovered));
  assert.ok(first.pipelines.every((pipeline) => pipeline.discovery.kind === "official-default"));
  assert.ok(first.pipelines.every((pipeline) => pipeline.discovery.repository === "microsoft/aspire"));
  assert.equal(first.pipelines.some((pipeline) => pipeline.definitionId === 1601), false);
  assert.deepEqual(first.warnings, []);
  assert.deepEqual(second, first);
  assert.equal(defaultsCalls, 1);
  assert.equal(calls.filter((args) => args[0] === "pipelines" && args[1] === "list").length, 1);
  assert.equal(calls.filter((args) => args[0] === "pipelines" && args[1] === "show").length, 3);
  const listCall = calls.find((args) => args[0] === "pipelines" && args[1] === "list");
  assert.equal(listCall[listCall.indexOf("--organization") + 1], "https://dev.azure.com/dnceng");
  assert.equal(listCall[listCall.indexOf("--project") + 1], "internal");
  assert.equal(listCall[listCall.indexOf("--repository") + 1], "microsoft-aspire");
});

test("default project discovery failure does not discard official Aspire pipelines", async (t) => {
  const definitions = new Map([
    [1599, officialDefinition(1599, "microsoft-aspire-codeql", "eng/pipelines/azure-pipelines-codeql.yml")],
    [1600, officialDefinition(1600, "microsoft-aspire-Release-To-NuGet", "eng/pipelines/release-publish-nuget.yml")],
    [1602, officialDefinition(1602, "microsoft-aspire", "eng/pipelines/azure-pipelines.yml")],
  ]);
  for (const failurePoint of ["repos list", "pipelines list"]) {
    await t.test(failurePoint, async () => {
      const result = await discoverAzureDevOpsPipelines(
        { repositories: ["microsoft/aspire"] },
        {
          runAz: async (args) => {
            if (args[0] === "repos" && args[1] === "list") {
              if (failurePoint === "repos list") {
                throw new AzureDevOpsError("azdo_query_failed", "Default project discovery failed.");
              }
              return [{ id: "repo-microsoft-aspire", name: "microsoft-aspire", type: "TfsGit" }];
            }
            if (args[0] === "pipelines" && args[1] === "list") {
              const project = args[args.indexOf("--project") + 1];
              if (project === "aspire-msft") {
                throw new AzureDevOpsError("azdo_query_failed", "Default project discovery failed.");
              }
              return [...definitions.values()].map(({ id, name, path, queueStatus }) => ({ id, name, path, queueStatus }));
            }
            if (args[0] === "pipelines" && args[1] === "show") {
              return definitions.get(Number(args[args.indexOf("--id") + 1]));
            }
            throw new Error(`Unexpected az call: ${args.join(" ")}`);
          },
          runAzText: async () => "[defaults]\norganization = https://dev.azure.com/dnceng\nproject = aspire-msft\n",
          cache: new Map(),
          nowMs: Date.parse("2026-08-06T15:00:00Z"),
        },
      );

      assert.deepEqual(result.pipelines.map((pipeline) => pipeline.definitionId), [1599, 1600, 1602]);
      assert.deepEqual(result.warnings, [
        "Azure CLI default project pipelines could not be discovered: Default project discovery failed.",
      ]);
    });
  }
});

test("official Azure discovery quietly skips missing CLI, authentication, and internal access", async (t) => {
  for (const code of ["az_cli_missing", "azdo_auth_required", "azdo_access_denied"]) {
    await t.test(code, async () => {
      let runAzCalls = 0;
      const options = {
        runAz: async () => {
          runAzCalls++;
          throw new AzureDevOpsError(code, "Expected unavailable credential.");
        },
        runAzText: async () => "[defaults]\n",
        cache: new Map(),
        nowMs: Date.parse("2026-08-06T15:00:00Z"),
      };
      const first = await discoverAzureDevOpsPipelines({ repositories: ["microsoft/aspire"] }, options);
      const second = await discoverAzureDevOpsPipelines({ repositories: ["microsoft/aspire"] }, options);

      assert.deepEqual(first, { pipelines: [], warnings: [] });
      assert.deepEqual(second, first);
      assert.equal(runAzCalls, 1);
    });
  }
});

test("official Azure discovery does not report an inspected pipeline as missing after a transient failure", async () => {
  const definitions = new Map([
    [1599, officialDefinition(1599, "microsoft-aspire-codeql", "eng/pipelines/azure-pipelines-codeql.yml")],
    [1600, officialDefinition(1600, "microsoft-aspire-Release-To-NuGet", "eng/pipelines/release-publish-nuget.yml")],
    [1602, officialDefinition(1602, "microsoft-aspire", "eng/pipelines/azure-pipelines.yml")],
  ]);
  const result = await discoverAzureDevOpsPipelines(
    { repositories: ["microsoft/aspire"] },
    {
      runAz: async (args) => {
        if (args[0] === "pipelines" && args[1] === "list") {
          return [...definitions.values()].map(({ id, name, path, queueStatus }) => ({ id, name, path, queueStatus }));
        }
        const id = Number(args[args.indexOf("--id") + 1]);
        if (id === 1602) throw new AzureDevOpsError("azdo_timeout", "The Azure DevOps query timed out.");
        return definitions.get(id);
      },
      runAzText: async () => "[defaults]\n",
      cache: new Map(),
      nowMs: Date.parse("2026-08-06T15:00:00Z"),
    },
  );

  assert.deepEqual(result.pipelines.map((pipeline) => pipeline.definitionId), [1599, 1600]);
  assert.deepEqual(result.warnings, [
    "Official pipeline 1602 could not be inspected: The Azure DevOps query timed out.",
  ]);
});

test("loadAzureDevOpsPipelineHealth reports failures, last success, and blocked deployment evidence", async () => {
  const runAz = async (args) => {
    if (args[0] === "pipelines" && args[1] === "show") return pipelineDefinition();
    if (args[0] === "pipelines" && args[1] === "build" && args[2] === "list") {
      return [
        build({ id: 30, result: "failed", finishTime: "2026-08-06T12:00:00Z" }),
        build({ id: 29, result: "partiallySucceeded", finishTime: "2026-08-05T12:00:00Z" }),
        build({ id: 28, result: "succeeded", finishTime: "2026-08-01T12:00:00Z" }),
      ];
    }
    if (args[0] === "devops" && args[1] === "invoke") {
      return {
        records: [
          { type: "Stage", name: "Build", state: "completed", result: "failed", order: 1, issues: [] },
          { type: "Task", name: "Publish Artifacts", state: "completed", result: "failed", order: 2,
            issues: [{ type: "error", message: "Not found PathToPublish: artifacts/packages" }] },
          { type: "Stage", name: "Deploy production", state: "completed", result: "skipped", order: 3, issues: [] },
        ],
      };
    }
    throw new Error(`Unexpected az call: ${args.join(" ")}`);
  };

  const health = await loadAzureDevOpsPipelineHealth(
    { url: "https://dev.azure.com/dnceng/internal/_build?definitionId=1602" },
    { runAz, now: new Date("2026-08-06T15:00:00Z") },
  );

  assert.equal(health.state, "failing");
  assert.equal(health.daysSinceSuccess, 5);
  assert.equal(health.failureStreak, 2);
  assert.equal(health.failedRecords[0].name, "Build");
  assert.ok(health.reasons.some((reason) => reason.code === "upstream_stage_blocked_deployment"));
  assert.ok(health.reasons.some((reason) => reason.summary.includes("Publish Artifacts")));
  assert.equal(health.canOpenRepoSession, false);
});

test("Azure builds are ordered by queue time and build id, not completion time", async () => {
  const runAz = async (args) => {
    if (args[0] === "pipelines" && args[1] === "show") return pipelineDefinition();
    if (args[0] === "pipelines" && args[1] === "build" && args[2] === "list") {
      return [
        build({
          id: 40,
          result: "failed",
          queueTime: "2026-08-06T10:00:00Z",
          finishTime: "2026-08-06T13:00:00Z",
        }),
        build({
          id: 41,
          result: "succeeded",
          queueTime: "2026-08-06T11:00:00Z",
          finishTime: "2026-08-06T12:00:00Z",
        }),
      ];
    }
    throw new Error(`Unexpected az call: ${args.join(" ")}`);
  };

  const health = await loadAzureDevOpsPipelineHealth(
    { url: "https://dev.azure.com/dnceng/internal/_build?definitionId=1602" },
    { runAz, now: new Date("2026-08-06T15:00:00Z") },
  );

  assert.equal(health.latest.id, 41);
  assert.equal(health.state, "healthy");
});

test("canceled Azure builds are failing with cancellation-specific reasoning", async () => {
  const runAz = async (args) => {
    if (args[0] === "pipelines" && args[1] === "show") return pipelineDefinition();
    if (args[0] === "pipelines" && args[1] === "build" && args[2] === "list") {
      if (args.includes("--result")) return [];
      return [build({
        id: 42,
        result: "canceled",
        queueTime: "2026-08-06T11:00:00Z",
        finishTime: "2026-08-06T12:00:00Z",
      })];
    }
    if (args[0] === "devops" && args[1] === "invoke") return { records: [] };
    throw new Error(`Unexpected az call: ${args.join(" ")}`);
  };

  const health = await loadAzureDevOpsPipelineHealth(
    { url: "https://dev.azure.com/dnceng/internal/_build?definitionId=1602" },
    { runAz, now: new Date("2026-08-06T15:00:00Z") },
  );

  assert.equal(health.state, "failing");
  assert.equal(health.failureStreak, 1);
  assert.ok(health.reasons.some((reason) => reason.code === "latest_build_canceled"));
  assert.ok(health.reasons.some((reason) => /canceled before completion/i.test(reason.summary)));
  assert.ok(!health.reasons.some((reason) => /partially succeeded/i.test(reason.summary)));
});

test("bounded Azure build history reports a failure streak as a lower bound", async () => {
  const runAz = async (args) => {
    if (args[0] === "pipelines" && args[1] === "show") return pipelineDefinition();
    if (args[0] === "pipelines" && args[1] === "build" && args[2] === "list") {
      if (args.includes("--result")) return [];
      return Array.from({ length: 50 }, (_, index) => build({
        id: 100 - index,
        result: "failed",
        queueTime: new Date(Date.UTC(2026, 7, 6 - index)).toISOString(),
        finishTime: new Date(Date.UTC(2026, 7, 6 - index, 1)).toISOString(),
      }));
    }
    if (args[0] === "devops" && args[1] === "invoke") return { records: [] };
    throw new Error(`Unexpected az call: ${args.join(" ")}`);
  };

  const health = await loadAzureDevOpsPipelineHealth(
    { url: "https://dev.azure.com/dnceng/internal/_build?definitionId=1602" },
    { runAz, now: new Date("2026-08-06T15:00:00Z") },
  );

  assert.equal(health.failureStreak, 50);
  assert.equal(health.failureStreakLowerBound, true);
  assert.ok(health.reasons.some((reason) => reason.summary === "At least 50 consecutive completed builds have not succeeded."));
});

test("Azure GitHub mappings require a matching github.com repository URL", async () => {
  const load = (repository) => loadAzureDevOpsPipelineHealth(
    { url: "https://dev.azure.com/dnceng/internal/_build?definitionId=1602" },
    {
      runAz: async (args) => {
        if (args[0] === "pipelines" && args[1] === "show") return pipelineDefinition(repository);
        if (args[0] === "pipelines" && args[1] === "build" && args[2] === "list") {
          return [build({
            id: 43,
            result: "succeeded",
            queueTime: "2026-08-06T11:00:00Z",
            finishTime: "2026-08-06T12:00:00Z",
          })];
        }
        throw new Error(`Unexpected az call: ${args.join(" ")}`);
      },
      now: new Date("2026-08-06T15:00:00Z"),
    },
  );

  const dotcom = await load({
    id: "repo-id",
    name: "microsoft/aspire",
    type: "GitHub",
    url: "https://github.com/microsoft/aspire.git",
    defaultBranch: "refs/heads/main",
  });
  const enterprise = await load({
    id: "repo-id",
    name: "microsoft/aspire",
    type: "GitHubEnterprise",
    url: "https://github.contoso.example/microsoft/aspire.git",
    defaultBranch: "refs/heads/main",
  });

  assert.equal(dotcom.mappedRepository, "microsoft/aspire");
  assert.equal(dotcom.canOpenRepoSession, true);
  assert.equal(enterprise.mappedRepository, null);
  assert.equal(enterprise.canOpenRepoSession, false);
});

test("resolveAzureCliCommand finds the standard Windows az.cmd shim", async () => {
  const shim = "C:\\Program Files\\Microsoft SDKs\\Azure\\CLI2\\wbin\\az.cmd";
  const python = "C:\\Program Files\\Microsoft SDKs\\Azure\\CLI2\\python.exe";
  const command = await resolveAzureCliCommand({
    platform: "win32",
    pathValue: '"C:\\Tools";C:\\Program Files\\Microsoft SDKs\\Azure\\CLI2\\wbin',
    exists: async (candidate) => candidate === shim || candidate === python,
  });

  assert.deepEqual(command, { path: python, prefixArgs: ["-IBm", "azure.cli"] });
});

test("unavailableAzureDevOpsHealth preserves an actionable provider error", () => {
  const item = unavailableAzureDevOpsHealth(
    { url: "https://dev.azure.com/dnceng/internal/_build?definitionId=1602" },
    new AzureDevOpsError("azdo_auth_required", "Run az login."),
  );

  assert.equal(item.state, "unavailable");
  assert.equal(item.definitionId, 1602);
  assert.deepEqual(item.reasons, [{ code: "azdo_auth_required", tone: "muted", summary: "Run az login." }]);
});

function pipelineDefinition(repository = {
  id: "repo-id",
  name: "microsoft-aspire",
  type: "TfsGit",
  url: "https://dev.azure.com/dnceng/internal/_git/microsoft-aspire",
  defaultBranch: "refs/heads/main",
}) {
  return {
    id: 1602,
    name: "microsoft-aspire",
    repository,
  };
}

function discoveredDefinition(id, name, yamlFilename) {
  return {
    id,
    name,
    path: "\\aspire.dev",
    queueStatus: "enabled",
    process: { type: 2, yamlFilename },
    repository: {
      id: "repo-aspire-dev",
      name: "aspire.dev",
      type: "TfsGit",
      url: "https://dev.azure.com/dnceng/aspire-msft/_git/aspire.dev",
      defaultBranch: "refs/heads/deploy",
    },
  };
}

function officialDefinition(id, name, yamlFilename) {
  return {
    id,
    name,
    path: "\\microsoft-aspire",
    queueStatus: "enabled",
    process: { type: 2, yamlFilename },
    repository: {
      id: "repo-microsoft-aspire",
      name: "microsoft-aspire",
      type: "TfsGit",
      url: "https://dev.azure.com/dnceng/internal/_git/microsoft-aspire",
      defaultBranch: "refs/heads/main",
    },
  };
}

function build({ id, result, finishTime, queueTime = finishTime }) {
  return {
    id,
    buildNumber: `20260806.${id}`,
    status: "completed",
    result,
    queueTime,
    finishTime,
    sourceVersion: `sha-${id}`,
    requestedFor: { displayName: "Build Service" },
  };
}
