// Read-only Azure DevOps health provider for the Aspire Team App canvas.
//
// Pipeline configuration stores only normalized coordinates. Authentication remains
// owned by Azure CLI (`az login`) or AZURE_DEVOPS_EXT_PAT and is never persisted here.

import { Buffer } from "node:buffer";
import { execFile } from "node:child_process";
import { access } from "node:fs/promises";
import { win32 } from "node:path";
import { promisify } from "node:util";

import { dayMs } from "./constants.mjs";

const execFileAsync = promisify(execFile);
const DEFAULT_BRANCH = "refs/heads/main";
const RECENT_BUILD_LIMIT = 50;
const COMMAND_TIMEOUT_MS = 30_000;
const COMMAND_MAX_BUFFER = 16 * 1024 * 1024;
const DISCOVERY_CACHE_TTL_MS = 10 * 60 * 1000;
const DISCOVERY_DEFINITION_LIMIT = 100;
const DISCOVERY_CONCURRENCY = 6;
const PIPELINE_ID_MAX_LENGTH = 512;
const PIPELINE_REMOVAL_KEY_MAX_LENGTH = 2048;
const PIPELINE_REMOVAL_KEY_PREFIX = "azp1_";
const discoveryCache = new Map();
const OFFICIAL_PIPELINE_TARGETS = [{
  githubRepository: "microsoft/aspire",
  organization: "https://dev.azure.com/dnceng",
  organizationName: "dnceng",
  project: "internal",
  azureRepository: "microsoft-aspire",
  pipelineNames: [
    "microsoft-aspire-codeql",
    "microsoft-aspire-Release-To-NuGet",
    "microsoft-aspire",
  ],
}];

export class AzureDevOpsError extends Error {
  constructor(code, message) {
    super(message);
    this.name = "AzureDevOpsError";
    this.code = code;
  }
}

export function azurePipelineRemovalKey(value) {
  const id = normalizedPipelineId(value);
  if (!id) return null;
  // Pipeline IDs include provider-owned project text. Keep that text out of agent-facing
  // output while retaining a stable, versioned key that the removal action can decode.
  const key = `${PIPELINE_REMOVAL_KEY_PREFIX}${Buffer.from(id, "utf8").toString("base64url")}`;
  return key.length <= PIPELINE_REMOVAL_KEY_MAX_LENGTH ? key : null;
}

export function azurePipelineIdFromRemovalKey(value) {
  const key = String(value ?? "").trim();
  if (!key.startsWith(PIPELINE_REMOVAL_KEY_PREFIX)
      || key.length > PIPELINE_REMOVAL_KEY_MAX_LENGTH
      || !/^[A-Za-z0-9_-]+$/.test(key)) {
    return null;
  }
  try {
    const id = Buffer.from(key.slice(PIPELINE_REMOVAL_KEY_PREFIX.length), "base64url").toString("utf8");
    return azurePipelineRemovalKey(id) === key ? id : null;
  } catch {
    return null;
  }
}

// Parse the two URL shapes Azure DevOps presents in its UI:
//   https://dev.azure.com/dnceng/internal/_build?definitionId=1602
//   https://dev.azure.com/dnceng/internal/_build/results?buildId=3040201
// Legacy organizations can also use:
//   https://dnceng.visualstudio.com/internal/_build?definitionId=1602
//
// See https://learn.microsoft.com/rest/api/azure/devops/build/definitions/get and
// https://learn.microsoft.com/rest/api/azure/devops/build/builds/get.
export function parseAzureDevOpsPipelineUrl(value) {
  let url;
  try {
    url = new URL(String(value ?? "").trim());
  } catch {
    throw new AzureDevOpsError("invalid_pipeline_url", "Enter a valid Azure DevOps pipeline URL.");
  }

  if (url.protocol !== "https:" || url.username || url.password) {
    throw new AzureDevOpsError("invalid_pipeline_url", "Azure DevOps pipeline URLs must use HTTPS without embedded credentials.");
  }

  const host = url.hostname.toLowerCase();
  let segments;
  try {
    segments = url.pathname.split("/").filter(Boolean).map(decodeURIComponent);
  } catch {
    throw new AzureDevOpsError("invalid_pipeline_url", "The Azure DevOps pipeline URL contains invalid path encoding.");
  }

  let organizationName;
  let project;
  let buildIndex;
  if (host === "dev.azure.com") {
    organizationName = segments[0];
    project = segments[1];
    buildIndex = segments.indexOf("_build");
    if (!organizationName || !project || buildIndex < 2) {
      throw new AzureDevOpsError("invalid_pipeline_url", "The URL must include an Azure DevOps organization, project, and _build path.");
    }
  } else if (host.endsWith(".visualstudio.com") && host !== ".visualstudio.com") {
    organizationName = host.slice(0, -".visualstudio.com".length);
    project = segments[0];
    buildIndex = segments.indexOf("_build");
    if (!organizationName || !project || buildIndex < 1) {
      throw new AzureDevOpsError("invalid_pipeline_url", "The URL must include an Azure DevOps project and _build path.");
    }
  } else {
    throw new AzureDevOpsError("invalid_pipeline_host", "Only dev.azure.com and visualstudio.com pipeline URLs are supported.");
  }
  if (!/^[A-Za-z0-9][A-Za-z0-9-]{0,63}$/.test(organizationName)
      || /[\u0000-\u001f\u007f/\\]/.test(project)
      || project.length > 256) {
    throw new AzureDevOpsError("invalid_pipeline_url", "The Azure DevOps organization or project is invalid.");
  }

  const definitionId = positiveInteger(url.searchParams.get("definitionId"));
  const buildId = positiveInteger(url.searchParams.get("buildId"));
  if (!definitionId && !buildId) {
    throw new AzureDevOpsError("missing_pipeline_id", "The URL must contain definitionId or buildId.");
  }

  url.hash = "";
  const organization = `https://dev.azure.com/${encodeURIComponent(organizationName)}`;
  return {
    organization,
    organizationName,
    project,
    definitionId,
    buildId,
    inputUrl: url.toString(),
  };
}

export async function resolveAzureDevOpsPipeline(value, { branch, runAz = runAzureCli } = {}) {
  const parsed = typeof value === "string" ? parseAzureDevOpsPipelineUrl(value) : value;
  let definitionId = positiveInteger(parsed?.definitionId);

  if (!definitionId && parsed?.buildId) {
    const build = await invokeAz(runAz, [
      "pipelines", "build", "show",
      "--id", String(parsed.buildId),
      "--organization", parsed.organization,
      "--project", parsed.project,
    ]);
    definitionId = positiveInteger(build?.definition?.id);
  }

  if (!definitionId) {
    throw new AzureDevOpsError("missing_pipeline_id", "The Azure DevOps build does not identify a pipeline definition.");
  }

  const definition = await invokeAz(runAz, [
    "pipelines", "show",
    "--id", String(definitionId),
    "--organization", parsed.organization,
    "--project", parsed.project,
  ]);
  if (positiveInteger(definition?.id) !== definitionId) {
    throw new AzureDevOpsError("pipeline_not_found", `Azure DevOps pipeline ${definitionId} was not found.`);
  }

  return normalizePipelineDefinition(parsed, definition, { branch });
}

// Azure CLI's defaults output is INI-shaped even when JSON output is requested:
//   [defaults]
//   organization = https://dev.azure.com/dnceng
//   project = aspire-msft
export function parseAzureDevOpsDefaults(value) {
  const defaults = new Map();
  let section = "";
  for (const rawLine of String(value ?? "").split(/\r?\n/)) {
    const line = rawLine.trim();
    const heading = /^\[([^\]]+)\]$/.exec(line);
    if (heading) {
      section = heading[1].trim().toLowerCase();
      continue;
    }
    if (section !== "defaults") continue;
    const setting = /^([^=]+?)\s*=\s*(.*)$/.exec(line);
    if (setting) defaults.set(setting[1].trim().toLowerCase(), setting[2].trim());
  }

  const project = defaults.get("project");
  const organizationValue = defaults.get("organization");
  if (!project || !organizationValue || /[\u0000-\u001f\u007f/\\]/.test(project) || project.length > 256) {
    return null;
  }

  let organizationUrl;
  try {
    organizationUrl = new URL(organizationValue);
  } catch {
    return null;
  }
  if (organizationUrl.protocol !== "https:" || organizationUrl.username || organizationUrl.password) return null;

  const host = organizationUrl.hostname.toLowerCase();
  let organizationName;
  if (host === "dev.azure.com") {
    const segments = organizationUrl.pathname.split("/").filter(Boolean);
    if (segments.length !== 1) return null;
    try {
      organizationName = decodeURIComponent(segments[0]);
    } catch {
      return null;
    }
  } else if (host.endsWith(".visualstudio.com") && host !== ".visualstudio.com") {
    organizationName = host.slice(0, -".visualstudio.com".length);
  } else {
    return null;
  }
  if (!/^[A-Za-z0-9][A-Za-z0-9-]{0,63}$/.test(organizationName)) return null;

  return {
    organization: `https://dev.azure.com/${encodeURIComponent(organizationName)}`,
    organizationName,
    project,
  };
}

export async function discoverAzureDevOpsPipelines(
  { repositories = [] } = {},
  {
    runAz = runAzureCli,
    runAzText = runAzureCliText,
    cache = discoveryCache,
    nowMs = Date.now(),
    cacheTtlMs = DISCOVERY_CACHE_TTL_MS,
  } = {},
) {
  const watched = watchedRepositories(repositories);
  if (watched.length === 0) return { pipelines: [], warnings: [] };

  // A single scheduler owns every CLI discovery call so repository fan-out cannot
  // multiply the number of Azure CLI processes running at once.
  const schedule = createTaskScheduler(DISCOVERY_CONCURRENCY);
  const queryAz = (args) => schedule(() => invokeAz(runAz, args));
  const queryAzText = (args) => schedule(() => runAzText(args));
  const [officialResult, configuredDefaultResult] = await Promise.allSettled([
    discoverOfficialAzureDevOpsPipelines(watched, {
      queryAz,
      cache,
      nowMs,
      cacheTtlMs,
    }),
    discoverDefaultProjectAzureDevOpsPipelines(watched, {
      queryAz,
      queryAzText,
      cache,
      nowMs,
      cacheTtlMs,
    }),
  ]);
  const official = settledDiscoveryResult(
    officialResult,
    "Official Azure DevOps pipelines",
    optionalOfficialDiscoveryFailure,
  );
  const configuredDefault = settledDiscoveryResult(
    configuredDefaultResult,
    "Azure CLI default project pipelines",
    optionalDiscoveryFailure,
  );
  const pipelinesById = new Map();
  for (const pipeline of [...official.pipelines, ...configuredDefault.pipelines]) {
    if (pipeline?.id && !pipelinesById.has(pipeline.id)) pipelinesById.set(pipeline.id, pipeline);
  }
  return {
    pipelines: [...pipelinesById.values()],
    warnings: boundedDiscoveryWarnings([...official.warnings, ...configuredDefault.warnings]),
  };
}

async function discoverOfficialAzureDevOpsPipelines(
  watched,
  {
    queryAz,
    cache,
    nowMs,
    cacheTtlMs,
  },
) {
  const targets = OFFICIAL_PIPELINE_TARGETS.filter((target) =>
    watched.some((repository) => repository.repository.toLowerCase() === target.githubRepository.toLowerCase()));
  const results = await Promise.all(targets.map(async (target) => {
    const projectKey = `${target.organizationName.toLowerCase()}/${target.project.toLowerCase()}`;
    const cacheKey = `official-definitions:${projectKey}/${target.azureRepository.toLowerCase()}`;
    const cached = cachedValue(cache, cacheKey, nowMs);
    if (cached !== undefined) return cached;

    let summaries;
    try {
      const definitionsRaw = await queryAz([
        "pipelines", "list",
        "--organization", target.organization,
        "--project", target.project,
        "--repository", target.azureRepository,
        "--repository-type", "tfsgit",
        "--query-order", "ModifiedDesc",
        "--top", String(DISCOVERY_DEFINITION_LIMIT),
      ]);
      const expectedNames = new Set(target.pipelineNames.map((name) => name.toLowerCase()));
      summaries = (Array.isArray(definitionsRaw) ? definitionsRaw : [])
        .filter((definition) => positiveInteger(definition?.id))
        .filter((definition) => String(definition?.queueStatus ?? "").toLowerCase() !== "disabled")
        .filter((definition) => expectedNames.has(String(definition?.name ?? "").toLowerCase()))
        .slice(0, DISCOVERY_DEFINITION_LIMIT);
    } catch (error) {
      const failure = asAzureDevOpsError(error);
      if (optionalOfficialDiscoveryFailure(failure)) {
        const result = { pipelines: [], warnings: [] };
        cacheValue(cache, cacheKey, result, nowMs, cacheTtlMs);
        return result;
      }
      return {
        pipelines: [],
        warnings: [`Official Azure DevOps pipelines could not be discovered: ${failure.message}`],
      };
    }

    const inspected = await Promise.all(summaries.map(async (summary) => {
      try {
        const detail = await queryAz([
          "pipelines", "show",
          "--id", String(summary.id),
          "--organization", target.organization,
          "--project", target.project,
        ]);
        return {
          definition: { ...summary, ...detail, queueStatus: detail?.queueStatus ?? summary.queueStatus },
          warning: null,
        };
      } catch (error) {
        const failure = asAzureDevOpsError(error);
        return {
          definition: null,
          warning: optionalOfficialDiscoveryFailure(failure)
            ? null
            : `Official pipeline ${summary.id} could not be inspected: ${failure.message}`,
        };
      }
    }));

    const inspectedDefinitions = inspected.map((entry) => entry.definition).filter(Boolean);
    const repository = {
      name: target.azureRepository,
      type: "TfsGit",
    };
    const definitions = inspectedDefinitions
      .filter((definition) => definitionUsesRepository(definition, repository));
    const listedNames = new Set(summaries.map((summary) => String(summary.name ?? "").toLowerCase()));
    const warnings = [
      ...inspected.map((entry) => entry.warning).filter(Boolean),
      ...inspectedDefinitions
        .filter((definition) => !definitionUsesRepository(definition, repository))
        .map((definition) => `Official pipeline ${definition.id} is not bound to ${target.azureRepository}.`),
      ...target.pipelineNames
        .filter((name) => !listedNames.has(name.toLowerCase()))
        .map((name) => `Official pipeline ${name} was not found in ${target.organizationName}/${target.project}.`),
    ];
    const pipelineOrder = new Map(target.pipelineNames.map((name, index) => [name.toLowerCase(), index]));
    const pipelines = definitions
      .sort((a, b) =>
        (pipelineOrder.get(String(a.name ?? "").toLowerCase()) ?? Number.MAX_SAFE_INTEGER)
        - (pipelineOrder.get(String(b.name ?? "").toLowerCase()) ?? Number.MAX_SAFE_INTEGER))
      .map((definition) => normalizePipelineDefinition(target, definition, {
        discovery: {
          kind: "official-default",
          repository: target.githubRepository,
          azureRepository: target.azureRepository,
          pipelineCandidates: definitions.length,
        },
      }));
    const result = { pipelines, warnings };
    cacheValue(cache, cacheKey, result, nowMs, cacheTtlMs);
    return result;
  }));

  return {
    pipelines: results.flatMap((result) => result.pipelines),
    warnings: results.flatMap((result) => result.warnings),
  };
}

async function discoverDefaultProjectAzureDevOpsPipelines(
  watched,
  {
    queryAz,
    queryAzText,
    cache,
    nowMs,
    cacheTtlMs,
  },
) {
  let defaults = cachedValue(cache, "defaults", nowMs);
  if (defaults === undefined) {
    try {
      defaults = parseAzureDevOpsDefaults(await queryAzText(["devops", "configure", "--list"]));
    } catch (error) {
      const failure = asAzureDevOpsError(error);
      if (optionalDiscoveryFailure(failure)) return { pipelines: [], warnings: [] };
      throw failure;
    }
    cacheValue(cache, "defaults", defaults, nowMs, cacheTtlMs);
  }
  if (!defaults) return { pipelines: [], warnings: [] };

  const projectKey = `${defaults.organizationName.toLowerCase()}/${defaults.project.toLowerCase()}`;
  const repositoriesKey = `repositories:${projectKey}`;
  let azureRepositories = cachedValue(cache, repositoriesKey, nowMs);
  if (azureRepositories === undefined) {
    const repositoriesRaw = await queryAz([
      "repos", "list",
      "--organization", defaults.organization,
      "--project", defaults.project,
    ]);
    azureRepositories = Array.isArray(repositoriesRaw) ? repositoriesRaw : [];
    cacheValue(cache, repositoriesKey, azureRepositories, nowMs, cacheTtlMs);
  }

  const associations = associateAzureRepositories(azureRepositories, watched);
  const definitionsByRepository = await Promise.all(associations.map(async (association) => {
    const repositoryIdentity = String(association.azureRepository.id || association.azureRepository.name);
    const definitionsKey = `definitions:${projectKey}/${repositoryIdentity.toLowerCase()}`;
    let result = cachedValue(cache, definitionsKey, nowMs);
    if (result === undefined) {
      const definitionsRaw = await queryAz([
        "pipelines", "list",
        "--organization", defaults.organization,
        "--project", defaults.project,
        "--repository", repositoryIdentity,
        "--repository-type", "tfsgit",
        "--query-order", "ModifiedDesc",
        "--top", String(DISCOVERY_DEFINITION_LIMIT),
      ]);
      const summaries = (Array.isArray(definitionsRaw) ? definitionsRaw : [])
        .filter((definition) => positiveInteger(definition?.id))
        .filter((definition) => String(definition?.queueStatus ?? "").toLowerCase() !== "disabled")
        .slice(0, DISCOVERY_DEFINITION_LIMIT);
      const inspected = await Promise.all(summaries.map(async (summary) => {
        try {
          const detail = await queryAz([
            "pipelines", "show",
            "--id", String(summary.id),
            "--organization", defaults.organization,
            "--project", defaults.project,
          ]);
          return {
            definition: { ...summary, ...detail, queueStatus: detail?.queueStatus ?? summary.queueStatus },
            warning: null,
          };
        } catch (error) {
          const failure = asAzureDevOpsError(error);
          return {
            definition: null,
            warning: `Pipeline ${summary.id} could not be inspected: ${failure.message}`,
          };
        }
      }));
      result = {
        definitions: inspected.map((entry) => entry.definition).filter(Boolean),
        warnings: inspected.map((entry) => entry.warning).filter(Boolean),
      };
      cacheValue(cache, definitionsKey, result, nowMs, cacheTtlMs);
    }
    return { association, ...result };
  }));

  const pipelines = [];
  const warnings = [];
  for (const entry of definitionsByRepository) {
    warnings.push(...entry.warnings);
    const candidates = entry.definitions
      .filter((definition) => definitionUsesRepository(definition, entry.association.azureRepository))
      .filter((definition) => !isAuxiliaryPipeline(definition))
      .sort(compareDeliveryPipelines);
    const definition = candidates[0];
    if (!definition) continue;

    pipelines.push(normalizePipelineDefinition(defaults, definition, {
      discovery: {
        kind: "azure-cli-default",
        repository: entry.association.githubRepository,
        azureRepository: String(entry.association.azureRepository.name ?? ""),
        pipelineCandidates: candidates.length,
      },
    }));
  }
  return { pipelines, warnings };
}

export async function loadAzureDevOpsPipelineHealth(rawConfig, { now = new Date(), runAz = runAzureCli } = {}) {
  const parsedConfig = rawConfig?.url ? parseAzureDevOpsPipelineUrl(rawConfig.url) : rawConfig;
  if (rawConfig?.definitionId) parsedConfig.definitionId = rawConfig.definitionId;
  const config = await resolveAzureDevOpsPipeline(parsedConfig, {
    branch: rawConfig?.branch,
    runAz,
  });
  if (rawConfig?.discovered) {
    config.discovered = true;
    config.discovery = normalizeDiscovery(rawConfig.discovery);
  }

  const recent = await invokeAz(runAz, [
    "pipelines", "build", "list",
    "--definition-ids", String(config.definitionId),
    "--organization", config.organization,
    "--project", config.project,
    "--branch", config.branch,
    "--top", String(RECENT_BUILD_LIMIT),
  ]);
  const builds = Array.isArray(recent) ? recent.slice().sort(compareBuilds) : [];
  const latest = builds[0] ?? null;

  let lastSuccess = builds.find((build) => normalizedResult(build) === "succeeded") ?? null;
  if (!lastSuccess) {
    const successes = await invokeAz(runAz, [
      "pipelines", "build", "list",
      "--definition-ids", String(config.definitionId),
      "--organization", config.organization,
      "--project", config.project,
      "--branch", config.branch,
      "--result", "succeeded",
      "--top", "1",
    ]);
    lastSuccess = Array.isArray(successes) ? successes.sort(compareBuilds)[0] ?? null : null;
  }

  const state = azureBuildState(latest);
  const failureStreak = consecutiveUnhealthyBuilds(builds);
  const failureStreakLowerBound = builds.length >= RECENT_BUILD_LIMIT
    && !builds.some((build) => normalizedResult(build) === "succeeded");
  let timelineRecords = [];
  let timelineError = null;
  if (latest?.id && (state === "failing" || state === "degraded")) {
    try {
      const timeline = await invokeAz(runAz, [
        "devops", "invoke",
        "--area", "build",
        "--resource", "Timeline",
        "--route-parameters", `project=${config.project}`, `buildId=${latest.id}`,
        "--org", config.organization,
      ]);
      timelineRecords = normalizeTimelineRecords(timeline?.records);
    } catch (error) {
      timelineError = asAzureDevOpsError(error);
    }
  }

  const lastSuccessAt = buildTime(lastSuccess);
  const failedRecords = timelineRecords.filter((record) => record.failed);
  const reasons = azureReasons({
    state,
    latest,
    failureStreak,
    failureStreakLowerBound,
    lastSuccessAt,
    failedRecords,
    timelineRecords,
    timelineError,
    now,
  });
  const mappedRepository = githubRepository(config.repository);

  return {
    ...config,
    state,
    mappedRepository,
    canOpenRepoSession: !!mappedRepository,
    latest: latest ? {
      id: positiveInteger(latest.id),
      number: String(latest.buildNumber ?? latest.id),
      at: buildTime(latest),
      status: String(latest.status ?? ""),
      result: String(latest.result ?? ""),
      url: buildUrl(config.organizationName, config.project, latest.id),
      sourceVersion: latest.sourceVersion ?? null,
      requestedFor: latest.requestedFor?.displayName ?? latest.requestedFor?.uniqueName ?? null,
    } : null,
    lastSuccessAt,
    daysSinceSuccess: daysSince(lastSuccessAt, now),
    failureStreak,
    failureStreakLowerBound,
    failedRecords,
    reasons,
    evidence: failedRecords.slice(0, 6).map((record) => ({
      label: `${record.type}: ${record.name}`,
      detail: record.errors[0] ?? record.result,
      url: latest?.id ? buildUrl(config.organizationName, config.project, latest.id) : config.url,
    })),
    diagnosticsError: timelineError ? timelineError.message : null,
  };
}

export function unavailableAzureDevOpsHealth(rawConfig, error) {
  const failure = asAzureDevOpsError(error);
  let parsed = null;
  try {
    parsed = rawConfig?.url ? parseAzureDevOpsPipelineUrl(rawConfig.url) : rawConfig;
  } catch {
    // The original error already carries the validation detail that should be shown.
  }

  const name = rawConfig?.name || (parsed?.definitionId ? `Pipeline ${parsed.definitionId}` : "Azure DevOps pipeline");
  let branch = DEFAULT_BRANCH;
  try {
    branch = normalizeBranch(rawConfig?.branch || DEFAULT_BRANCH);
  } catch {
    // The primary provider error is more useful than a secondary malformed cached branch.
  }
  return {
    id: rawConfig?.id || `azdo-unavailable:${encodeURIComponent(String(rawConfig?.url || name))}`,
    provider: "azure-devops",
    name,
    url: rawConfig?.url ?? null,
    organization: parsed?.organization ?? null,
    organizationName: parsed?.organizationName ?? null,
    project: parsed?.project ?? null,
    definitionId: parsed?.definitionId ?? rawConfig?.definitionId ?? null,
    branch,
    repository: rawConfig?.repository ?? null,
    mappedRepository: null,
    canOpenRepoSession: false,
    state: "unavailable",
    latest: null,
    lastSuccessAt: null,
    daysSinceSuccess: null,
    failureStreak: 0,
    failureStreakLowerBound: false,
    failedRecords: [],
    evidence: [],
    reasons: [{ code: failure.code, tone: "muted", summary: failure.message }],
    diagnosticsError: failure.message,
    discovered: !!rawConfig?.discovered,
    discovery: normalizeDiscovery(rawConfig?.discovery),
  };
}

export async function runAzureCli(args) {
  const stdout = await executeAzureCli(args, ["--only-show-errors", "-o", "json"]);
  return stdout.trim() ? JSON.parse(stdout) : null;
}

export async function runAzureCliText(args) {
  return (await executeAzureCli(args, ["--only-show-errors"])).trim();
}

async function executeAzureCli(args, suffixArgs) {
  try {
    const command = await resolveAzureCliCommand();
    const commandArgs = [...command.prefixArgs, ...args, ...suffixArgs];
    const { stdout } = await execFileAsync(
      command.path,
      commandArgs,
      {
        timeout: COMMAND_TIMEOUT_MS,
        maxBuffer: COMMAND_MAX_BUFFER,
        windowsHide: true,
        encoding: "utf8",
      },
    );
    return stdout;
  } catch (error) {
    throw classifyCommandError(error);
  }
}

export async function resolveAzureCliCommand({
  platform = process.platform,
  pathValue = process.env.PATH,
  exists = pathExists,
} = {}) {
  if (platform !== "win32") return { path: "az", prefixArgs: [] };

  const entries = String(pathValue ?? "")
    .split(";")
    .map((entry) => entry.trim().replace(/^"(.*)"$/, "$1"))
    .filter(Boolean);
  let batchShimFound = false;
  for (const entry of entries) {
    for (const extension of [".exe", ".com"]) {
      const candidate = win32.join(entry, `az${extension}`);
      if (await exists(candidate)) return { path: candidate, prefixArgs: [] };
    }
    for (const extension of [".cmd", ".bat"]) {
      const shim = win32.join(entry, `az${extension}`);
      if (await exists(shim)) {
        batchShimFound = true;
        // The official Azure CLI MSI shim runs its bundled Python entry point with
        // `%*`, which expands `%NAME%` sequences in legal branch arguments. Invoke
        // that entry point directly so every argument reaches Azure CLI verbatim.
        const bundledPython = win32.normalize(win32.join(entry, "..", "python.exe"));
        if (await exists(bundledPython)) {
          return { path: bundledPython, prefixArgs: ["-IBm", "azure.cli"] };
        }
      }
    }
  }

  if (batchShimFound) {
    const error = new Error("Azure CLI was found only as an unsupported batch shim.");
    error.code = "AZ_CLI_BATCH_ONLY";
    throw error;
  }
  const error = new Error("Azure CLI is not available on PATH.");
  error.code = "ENOENT";
  throw error;
}

async function pathExists(path) {
  try {
    await access(path);
    return true;
  } catch {
    return false;
  }
}

async function invokeAz(runAz, args) {
  try {
    const result = await runAz(args);
    if (typeof result === "string") {
      return result.trim() ? JSON.parse(result) : null;
    }
    return result;
  } catch (error) {
    throw asAzureDevOpsError(error);
  }
}

function classifyCommandError(error) {
  if (error?.code === "ENOENT") {
    return new AzureDevOpsError("az_cli_missing", "Azure CLI is not installed or is not available on PATH.");
  }
  if (error?.code === "AZ_CLI_BATCH_ONLY") {
    return new AzureDevOpsError("az_cli_unsupported", "This Azure CLI installation does not expose a safe executable entry point.");
  }

  const detail = compactDetail([error?.stderr, error?.stdout, error?.message].filter(Boolean).join("\n"));
  if (/azure-devops extension|az extension add --name azure-devops|pipelines.*misspelled/i.test(detail)) {
    return new AzureDevOpsError("azdo_extension_missing", "The Azure CLI azure-devops extension is not installed.");
  }
  if (/az login|not logged|authentication|TF400813|401\b/i.test(detail)) {
    return new AzureDevOpsError("azdo_auth_required", "Azure DevOps authentication is required. Run az login or set AZURE_DEVOPS_EXT_PAT.");
  }
  if (/access denied|forbidden|TF401019|VS403|403\b/i.test(detail)) {
    return new AzureDevOpsError("azdo_access_denied", "The current Azure DevOps credential cannot access this pipeline.");
  }
  if (/timed out|ETIMEDOUT/i.test(detail)) {
    return new AzureDevOpsError("azdo_timeout", "The Azure DevOps query timed out.");
  }
  return new AzureDevOpsError("azdo_query_failed", detail || "The Azure DevOps query failed.");
}

function asAzureDevOpsError(error) {
  return error instanceof AzureDevOpsError
    ? error
    : new AzureDevOpsError(error?.code || "azdo_query_failed", compactDetail(error?.message) || "The Azure DevOps query failed.");
}

function normalizeTimelineRecords(records) {
  const supported = new Set(["Stage", "Job", "Phase", "Task"]);
  const seen = new Set();
  const out = [];
  for (const record of Array.isArray(records) ? records : []) {
    if (!supported.has(record?.type) || !record?.name) continue;
    const errors = (Array.isArray(record.issues) ? record.issues : [])
      .filter((issue) => String(issue?.type).toLowerCase() === "error" && issue?.message)
      .map((issue) => compactDetail(issue.message))
      .filter(Boolean);
    const result = String(record.result ?? "");
    const failed = result.toLowerCase() === "failed" || errors.length > 0;
    const key = `${record.type}\n${record.name}\n${result}\n${errors.join("\n")}`;
    if (seen.has(key)) continue;
    seen.add(key);
    out.push({
      type: record.type,
      name: String(record.name).trim(),
      state: String(record.state ?? ""),
      result,
      failed,
      errors,
      order: Number.isFinite(record.order) ? record.order : null,
    });
  }
  return out.sort((a, b) => (a.order ?? Number.MAX_SAFE_INTEGER) - (b.order ?? Number.MAX_SAFE_INTEGER));
}

function azureReasons({
  state,
  latest,
  failureStreak,
  failureStreakLowerBound,
  lastSuccessAt,
  failedRecords,
  timelineRecords,
  timelineError,
  now,
}) {
  const reasons = [];
  const latestResult = normalizedResult(latest);
  const failedStage = timelineRecords.find((record) => record.type === "Stage" && record.failed);
  const skippedDeployment = timelineRecords.find((record) =>
    record.type === "Stage"
    && String(record.result).toLowerCase() === "skipped"
    && /(deploy|publish|release)/i.test(record.name));
  if (failedStage && skippedDeployment) {
    reasons.push({
      code: "upstream_stage_blocked_deployment",
      tone: "danger",
      summary: `${skippedDeployment.name} was skipped while ${failedStage.name} failed; deployment is likely blocked upstream.`,
    });
  }

  const firstFailure = failedRecords.find((record) => record.type === "Task")
    ?? failedRecords.find((record) => record.type === "Job")
    ?? failedRecords[0];
  if (latestResult === "canceled" || latestResult === "cancelled") {
    reasons.push({
      code: "latest_build_canceled",
      tone: "danger",
      summary: `Latest build ${latest?.buildNumber ?? latest?.id ?? ""} was canceled before completion.`.trim(),
    });
  } else if (firstFailure) {
    const detail = firstFailure.errors[0] ? `: ${firstFailure.errors[0]}` : "";
    reasons.push({
      code: "failed_timeline_record",
      tone: "danger",
      summary: `${firstFailure.type} ${firstFailure.name} failed${detail}`,
    });
  } else if (state === "failing") {
    reasons.push({
      code: "latest_build_failed",
      tone: "danger",
      summary: `Latest build ${latest?.buildNumber ?? latest?.id ?? ""} failed.`.trim(),
    });
  } else if (state === "degraded") {
    reasons.push({
      code: "latest_build_degraded",
      tone: "warning",
      summary: `Latest build ${latest?.buildNumber ?? latest?.id ?? ""} partially succeeded.`.trim(),
    });
  } else if (state === "running") {
    reasons.push({ code: "build_running", tone: "warning", summary: "The latest build is still running." });
  } else if (state === "unknown") {
    reasons.push({ code: "no_builds", tone: "muted", summary: "No builds were found for the configured branch." });
  }

  if (failureStreak > 1) {
    reasons.push({
      code: "build_failure_streak",
      tone: "danger",
      summary: `${failureStreakLowerBound ? "At least " : ""}${failureStreak} consecutive completed builds have not succeeded.`,
    });
  }
  if (state !== "healthy" && lastSuccessAt) {
    reasons.push({
      code: "last_success_age",
      tone: "warning",
      summary: `Last successful build was ${daysSince(lastSuccessAt, now)} day${daysSince(lastSuccessAt, now) === 1 ? "" : "s"} ago.`,
    });
  } else if (state !== "healthy" && latest && !lastSuccessAt) {
    reasons.push({
      code: "no_success_found",
      tone: "warning",
      summary: "No successful build was found for the configured branch.",
    });
  }
  if (timelineError) {
    reasons.push({
      code: "timeline_unavailable",
      tone: "muted",
      summary: `Build timeline unavailable: ${timelineError.message}`,
    });
  }
  return reasons;
}

function azureBuildState(build) {
  if (!build) return "unknown";
  const status = String(build.status ?? "").toLowerCase();
  if (status && status !== "completed") return "running";
  switch (normalizedResult(build)) {
    case "succeeded": return "healthy";
    case "partiallysucceeded": return "degraded";
    case "failed": return "failing";
    case "canceled":
    case "cancelled":
      return "failing";
    case "none": return "running";
    default: return "unknown";
  }
}

function consecutiveUnhealthyBuilds(builds) {
  let count = 0;
  for (const build of builds) {
    if (String(build?.status ?? "").toLowerCase() !== "completed") continue;
    if (normalizedResult(build) === "succeeded") break;
    count++;
  }
  return count;
}

function normalizedResult(build) {
  return String(build?.result ?? "none").replaceAll("_", "").toLowerCase();
}

function compareBuilds(a, b) {
  const aQueued = Date.parse(a?.queueTime ?? "");
  const bQueued = Date.parse(b?.queueTime ?? "");
  if (Number.isFinite(aQueued) && Number.isFinite(bQueued) && aQueued !== bQueued) {
    return bQueued - aQueued;
  }

  const aId = positiveInteger(a?.id) ?? 0;
  const bId = positiveInteger(b?.id) ?? 0;
  if (aId !== bId) return bId - aId;

  return new Date(buildTime(b) || 0) - new Date(buildTime(a) || 0);
}

function buildTime(build) {
  return build?.finishTime || build?.startTime || build?.queueTime || null;
}

function normalizeRepository(repository) {
  if (!repository) return null;
  return {
    id: repository.id ?? null,
    name: repository.name ?? null,
    type: repository.type ?? null,
    url: repository.url ?? null,
    defaultBranch: repository.defaultBranch ?? null,
  };
}

function normalizePipelineDefinition(parsed, definition, { branch, discovery } = {}) {
  const definitionId = positiveInteger(definition?.id);
  if (!definitionId) {
    throw new AzureDevOpsError("pipeline_not_found", "The Azure DevOps pipeline definition is invalid.");
  }
  const normalizedBranch = normalizeBranch(branch || definition?.repository?.defaultBranch || DEFAULT_BRANCH);
  const normalizedDiscovery = normalizeDiscovery(discovery);
  return {
    id: azurePipelineId(parsed.organizationName, parsed.project, definitionId),
    provider: "azure-devops",
    url: pipelineUrl(parsed.organizationName, parsed.project, definitionId),
    organization: parsed.organization,
    organizationName: parsed.organizationName,
    project: parsed.project,
    definitionId,
    name: String(definition?.name || `Pipeline ${definitionId}`),
    branch: normalizedBranch,
    repository: normalizeRepository(definition?.repository),
    discovered: !!normalizedDiscovery,
    discovery: normalizedDiscovery,
  };
}

function normalizeDiscovery(value) {
  const kind = String(value?.kind ?? "");
  if (!["azure-cli-default", "official-default"].includes(kind)) return null;
  const repository = String(value.repository ?? "").trim();
  const azureRepository = String(value.azureRepository ?? "").trim();
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository)
      || !azureRepository
      || azureRepository.length > 256
      || /[\u0000-\u001f\u007f/\\]/.test(azureRepository)) {
    return null;
  }
  return {
    kind,
    repository,
    azureRepository,
    pipelineCandidates: Math.max(1, positiveInteger(value.pipelineCandidates) ?? 1),
  };
}

function watchedRepositories(values) {
  const seen = new Set();
  const out = [];
  for (const value of Array.isArray(values) ? values : []) {
    const repository = String(value ?? "").trim();
    const match = /^([^/\s]+)\/([^/\s]+)$/.exec(repository);
    const key = repository.toLowerCase();
    if (!match || seen.has(key)) continue;
    seen.add(key);
    out.push({
      repository,
      fullKey: repositoryMatchKey(repository),
      shortKey: repositoryMatchKey(match[2]),
    });
  }
  return out;
}

function associateAzureRepositories(repositories, watched) {
  const matches = [];
  for (const azureRepository of Array.isArray(repositories) ? repositories : []) {
    if (!azureRepository?.name || !/^(TfsGit|AzureReposGit)$/i.test(String(azureRepository.type ?? "TfsGit"))) continue;
    const key = repositoryMatchKey(azureRepository.name);
    const fullMatches = uniqueWatched(watched.filter((candidate) => candidate.fullKey === key));
    const candidates = fullMatches.length > 0
      ? fullMatches
      : uniqueWatched(watched.filter((candidate) => candidate.shortKey === key));
    if (candidates.length === 1) {
      matches.push({
        githubRepository: candidates[0].repository,
        azureRepository,
      });
    }
  }

  const byGitHubRepository = new Map();
  for (const match of matches) {
    const key = match.githubRepository.toLowerCase();
    const values = byGitHubRepository.get(key) ?? [];
    values.push(match);
    byGitHubRepository.set(key, values);
  }
  return [...byGitHubRepository.values()]
    .filter((values) => values.length === 1)
    .map((values) => values[0]);
}

function uniqueWatched(values) {
  return [...new Map(values.map((value) => [value.repository.toLowerCase(), value])).values()];
}

function definitionUsesRepository(definition, repository) {
  const source = definition?.repository;
  if (!source || !/^(TfsGit|AzureReposGit)$/i.test(String(source.type ?? ""))) return false;
  if (source.id && repository?.id) return String(source.id).toLowerCase() === String(repository.id).toLowerCase();
  return repositoryMatchKey(source.name) === repositoryMatchKey(repository?.name);
}

function isAuxiliaryPipeline(definition) {
  const text = pipelineSearchText(definition);
  return /(merge\s*changes|mergechanges|sync|mirror|cleanup|provision|generated|\bunofficial\b|\bold\b)/i.test(text);
}

function compareDeliveryPipelines(a, b) {
  const score = deliveryPipelineScore(b) - deliveryPipelineScore(a);
  if (score !== 0) return score;
  const name = String(a?.name ?? "").localeCompare(String(b?.name ?? ""), undefined, { sensitivity: "base" });
  if (name !== 0) return name;
  return (positiveInteger(a?.id) ?? 0) - (positiveInteger(b?.id) ?? 0);
}

function deliveryPipelineScore(definition) {
  const text = pipelineSearchText(definition);
  let score = 0;
  if (/\b(prod|production)\b/i.test(text)) score += 400;
  if (/\b(release|deploy|deployment|publish)\b/i.test(text)) score += 200;
  if (/\b(build|ci|validation)\b/i.test(text)) score += 100;
  if (/\b(test|staging)\b/i.test(text)) score -= 20;
  return score;
}

function pipelineSearchText(definition) {
  return [
    definition?.name,
    definition?.path,
    definition?.process?.yamlFilename,
  ].filter(Boolean).join(" ");
}

function repositoryMatchKey(value) {
  return String(value ?? "")
    .trim()
    .replace(/\.git$/i, "")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "");
}

function createTaskScheduler(limit) {
  const queue = [];
  let active = 0;
  const dispatch = () => {
    while (active < limit && queue.length > 0) {
      const entry = queue.shift();
      active++;
      Promise.resolve()
        .then(entry.task)
        .then(entry.resolve, entry.reject)
        .finally(() => {
          active--;
          dispatch();
        });
    }
  };
  return (task) => new Promise((resolve, reject) => {
    queue.push({ task, resolve, reject });
    dispatch();
  });
}

function cachedValue(cache, key, nowMs) {
  const entry = cache?.get?.(key);
  if (!entry || entry.expiresAt <= nowMs) return undefined;
  return entry.value;
}

function cacheValue(cache, key, value, nowMs, ttlMs) {
  cache?.set?.(key, {
    value,
    expiresAt: nowMs + Math.max(0, Number(ttlMs) || 0),
  });
}

function optionalDiscoveryFailure(error) {
  return new Set([
    "ENOENT",
    "AZ_CLI_BATCH_ONLY",
    "az_cli_missing",
    "az_cli_unsupported",
    "azdo_extension_missing",
  ]).has(error?.code);
}

function optionalOfficialDiscoveryFailure(error) {
  return optionalDiscoveryFailure(error)
    || new Set(["azdo_auth_required", "azdo_access_denied"]).has(error?.code);
}

function settledDiscoveryResult(result, description, optionalFailure) {
  if (result.status === "fulfilled") return result.value;
  const failure = asAzureDevOpsError(result.reason);
  return {
    pipelines: [],
    warnings: optionalFailure(failure) ? [] : [`${description} could not be discovered: ${failure.message}`],
  };
}

function boundedDiscoveryWarnings(warnings) {
  const unique = [...new Set(warnings)];
  const visible = unique.slice(0, 3);
  if (unique.length > visible.length) {
    visible.push(`${unique.length - visible.length} additional pipeline definitions could not be inspected.`);
  }
  return visible;
}

function githubRepository(repository) {
  if (!repository || !/github/i.test(String(repository.type ?? ""))) return null;
  const name = String(repository.name ?? "").trim();
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(name)) return null;

  let url;
  try {
    url = new URL(String(repository.url ?? ""));
  } catch {
    return null;
  }
  if (url.protocol !== "https:" || url.hostname.toLowerCase() !== "github.com" || url.username || url.password) {
    return null;
  }
  const path = url.pathname.replace(/\.git$/i, "").replace(/^\/+|\/+$/g, "");
  return path.toLowerCase() === name.toLowerCase() ? name : null;
}

function normalizeBranch(value) {
  const branch = String(value ?? "").trim();
  if (!branch) return DEFAULT_BRANCH;
  if (/[\u0000-\u001f\u007f]/.test(branch) || branch.length > 512) {
    throw new AzureDevOpsError("invalid_branch", "The Azure DevOps branch is invalid.");
  }
  return branch.startsWith("refs/") ? branch : `refs/heads/${branch}`;
}

function normalizedPipelineId(value) {
  const id = String(value ?? "").trim();
  return id.startsWith("azdo:")
      && id.length <= PIPELINE_ID_MAX_LENGTH
      && !/[\u0000-\u001f\u007f]/.test(id)
    ? id
    : null;
}

function azurePipelineId(organization, project, definitionId) {
  return `azdo:${String(organization).toLowerCase()}/${String(project).toLowerCase()}/${definitionId}`;
}

function pipelineUrl(organization, project, definitionId) {
  return `https://dev.azure.com/${encodeURIComponent(organization)}/${encodeURIComponent(project)}/_build?definitionId=${definitionId}`;
}

function buildUrl(organization, project, buildId) {
  return `https://dev.azure.com/${encodeURIComponent(organization)}/${encodeURIComponent(project)}/_build/results?buildId=${buildId}`;
}

function positiveInteger(value) {
  const number = Number(value);
  return Number.isInteger(number) && number > 0 ? number : null;
}

function daysSince(value, now) {
  if (!value) return null;
  const elapsed = new Date(now).getTime() - new Date(value).getTime();
  return Number.isFinite(elapsed) ? Math.max(0, Math.floor(elapsed / dayMs)) : null;
}

function compactDetail(value) {
  return String(value ?? "").replace(/\s+/g, " ").trim().slice(0, 500);
}
