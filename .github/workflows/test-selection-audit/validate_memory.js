"use strict";

const fs = require("node:fs");
const path = require("node:path");

function validateMemory(memoryRoot, requireProvenance = true) {
  // Validates agent-edited memory before saving.
  // Protected evidence controls findings; snapshots keep history.
  // Reject malformed, invented, deleted, or unsupported state.
  const allowedFiles = new Set(["processed-runs.jsonl", "watchlist.jsonl"]);
  // Match collector paths across the trust boundary.
  const pathRe = /^(?!\/|.*\/(?:$|\/)|.*(?:^|\/)\.{1,2}(?:\/|$)|.*[\p{Cc}\p{Cf}\p{Cs}\p{Zl}\p{Zp}"\\])\S(?:.*\S)?$/u;
  const targetPattern = /^(test|job):[A-Za-z0-9._-]+$/;
  const shaPattern = /^[0-9a-f]{40}$/;
  const titlePattern = /^\[test-selection-audit\] [A-Za-z0-9 .-]{1,77}$/;
  let auditDate = new Date().toISOString().slice(0, 10);
  // Validate closed schemas and bounded JSONL.
  const fail = message => { throw new Error(`Invalid test-selection audit memory: ${message}`); };
  const isObject = value => value !== null && typeof value === "object" && !Array.isArray(value);
  const requireKeys = (value, required, allowed, context) => {
    if (!isObject(value)) fail(`${context} must be an object`);
    for (const key of required) if (!(key in value)) fail(`${context} is missing ${key}`);
    for (const key of Object.keys(value)) if (!allowed.has(key)) fail(`${context} has unexpected field ${key}`);
  };
  const requireInteger = (value, context, minimum = 0) => {
    if (!Number.isSafeInteger(value) || value < minimum) fail(`${context} must be an integer >= ${minimum}`);
  };
  const requireString = (value, pattern, context, maxLength = 400) => {
    if (typeof value !== "string" || value.length === 0 || value.length > maxLength || !pattern.test(value))
      fail(`${context} is invalid`);
  };
  const pathOk = (value, context) => requireString(value, pathRe, context);
  const refOk = (value, context) => {
    if (typeof value !== "string" || !/@[0-9a-f]{7,40}$/.test(value)) fail(context);
    pathOk(value.replace(/@[0-9a-f]{7,40}$/, ""), context); };
  const requireUtcDate = (value, context) => {
    requireString(value, /^\d{4}-\d{2}-\d{2}$/, context, 10);
    const parsed = new Date(value + "T00:00:00Z");
    if (Number.isNaN(+parsed) || parsed.toISOString().slice(0, 10) !== value || value > auditDate)
      fail(`${context} must be a UTC date <= ${auditDate}`);
  };
  const unique = (values, validate, context) => {
    if (!Array.isArray(values)) fail(`${context} array`);
    const seen = new Set();
    for (const [index, value] of values.entries()) {
      validate(value, `${context}[${index}]`);
      if (seen.has(value)) fail(`${context} duplicate`); seen.add(value);
    }
    return seen;
  };
  const readJsonLines = fileName => {
    const fullPath = path.join(memoryRoot, fileName);
    if (!fs.existsSync(fullPath)) return [];
    const text = fs.readFileSync(fullPath, "utf8");
    if (text.length === 0) return [];
    if (!text.endsWith("\n")) fail(`${fileName} must end with a newline`);
    return text.trimEnd().split("\n").map((line, index) => {
      if (line.length > 16384) fail(`${fileName}:${index + 1} exceeds 16 KiB`);
      try { return JSON.parse(line); }
      catch { fail(`${fileName}:${index + 1} is not valid JSON`); }
    });
  };
  // Reject extra memory entries.
  for (const entry of fs.readdirSync(memoryRoot, { withFileTypes: true })) {
    if (entry.name === ".git") continue;
    if (!entry.isFile() || !allowedFiles.has(entry.name)) fail(`unexpected memory entry ${entry.name}`);
  }

  const processedAllowed = new Set(["pr", "sha", "run", "attempt", "all", "over_paths", "miss_edges", "seen"]);
  const edgeAllowed = new Set(["path", "target"]);
  const processed = readJsonLines("processed-runs.jsonl");
  const provenanceRoot = path.join(process.env.RUNNER_TEMP || fail("RUNNER_TEMP unavailable"), "gh-aw/safeoutputs/test-selection-audit");
  const [evidenceFile, processedBefore, watchBefore] =
    ["evidence.json", "processed-runs-before.jsonl", "watchlist-before.jsonl"]
      .map(fileName => path.join(provenanceRoot, fileName));
  const provenance = [evidenceFile, processedBefore, watchBefore].map(fs.existsSync);
  // gh-aw revalidates the downloaded memory on a fresh push runner. Only that
  // phase may omit all provenance; partial provenance is always an error.
  if ((requireProvenance || provenance.some(Boolean)) && !provenance.every(Boolean))
    fail("provenance files are incomplete");
  const hasProvenance = provenance[0];
  // Canonicalize before comparing baseline rows.
  const canonical = value => {
    if (Array.isArray(value)) return value.map(canonical);
    if (!isObject(value)) return value;
    return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])]));
  };
  const serialize = value => JSON.stringify(canonical(value));
  const same = (left, right) => Boolean(left) && serialize(left) === serialize(right);
  const parseBaseline = filePath => {
    const rows = new Map();
    for (const line of fs.readFileSync(filePath, "utf8").split("\n")) {
      if (!line) continue;
      const row = JSON.parse(line);
      rows.set(`${row.pr}:${row.sha}`, row);
    }
    return rows;
  };

  // Load evidence and baselines together.
  let baselineRows = new Map();
  const trustedSelections = new Map();
  const recordedSelections = new Map();
  if (hasProvenance) {
    const evidence = JSON.parse(fs.readFileSync(evidenceFile, "utf8"));
    requireUtcDate(evidence.auditDate, "evidence.auditDate");
    auditDate = evidence.auditDate;
    baselineRows = parseBaseline(processedBefore);
    for (const record of evidence.records) {
      const selection = record.selection;
      if (selection.status === "resolved" && selection.creditable === true) {
        trustedSelections.set(`${record.pr}:${record.headSha}`, selection);
      } else if (selection.status === "recorded") {
        recordedSelections.set(`${record.pr}:${record.headSha}`, selection);
      }
    }
  }

  // Derive counters from PR-head rows, never agent aggregates.
  const processedKeys = new Set();
  const affected = new Set();
  const overCounts = new Map();
  const missCounts = new Map();
  const overPrs = new Map();
  const missPrs = new Map();
  const credit = (counts, prs, key, pr) => {
    counts.set(key, (counts.get(key) || 0) + 1);
    if (!prs.has(key)) prs.set(key, new Set());
    prs.get(key).add(pr);
  };
  const markAffected = row => {
    for (const value of row?.over_paths || []) affected.add(`over\u0000${value}`);
    for (const edge of row?.miss_edges || []) affected.add(`miss\u0000${edge.path}\u0000${edge.target}`);
  };
  for (const [index, row] of processed.entries()) {
    const context = `processed-runs.jsonl:${index + 1}`;
    requireKeys(row, ["pr", "sha", "run", "attempt", "all", "over_paths", "miss_edges", "seen"],
      processedAllowed, context);
    requireInteger(row.pr, `${context}.pr`, 1);
    requireString(row.sha, shaPattern, `${context}.sha`, 40);
    requireInteger(row.run, `${context}.run`, 1);
    requireInteger(row.attempt, `${context}.attempt`, 1);
    if (typeof row.all !== "boolean") fail(`${context}.all must be boolean`);
    requireUtcDate(row.seen, `${context}.seen`);
    const identity = `${row.pr}:${row.sha}`;
    if (processedKeys.has(identity)) fail(`duplicate processed identity ${identity}`);
    processedKeys.add(identity);
    const baselineRow = baselineRows.get(identity);
    const selection = trustedSelections.get(identity);
    const recordedSelection = recordedSelections.get(identity);
    if (hasProvenance) {
      if (selection) {
        if (row.run !== selection.run ||
            row.attempt !== selection.attempt ||
            row.all !== selection.result.selectsAll) {
          fail(`${context} does not match trusted selection evidence`);
        }
        if (row.seen !== auditDate) fail(`${context}.seen must match the protected audit date`);
      } else if (recordedSelection) {
        if (!same(baselineRow, row) ||
            baselineRow.run !== recordedSelection.run ||
            baselineRow.attempt !== recordedSelection.attempt) {
          fail(`${context} does not match its recorded baseline`);
        }
      } else if (!same(baselineRow, row)) {
        fail(`${context} is not an unchanged baseline or trusted selection`);
      }
    }

    if (!row.all && row.over_paths.length > 0)
      fail(`${context}.over_paths must be empty for a narrow selection`);
    for (const pathValue of unique(row.over_paths, pathOk, `${context}.over_paths`)) {
      credit(overCounts, overPrs, pathValue, row.pr);
    }

    if (!Array.isArray(row.miss_edges)) fail(`${context}.miss_edges must be an array`);
    if (row.all && row.miss_edges.length > 0)
      fail(`${context}.miss_edges must be empty for an ALL selection`);
    const edgeKeys = new Set();
    for (const [edgeIndex, edge] of row.miss_edges.entries()) {
      const edgeContext = `${context}.miss_edges[${edgeIndex}]`;
      requireKeys(edge, ["path", "target"], edgeAllowed, edgeContext);
      pathOk(edge.path, `${edgeContext}.path`);
      requireString(edge.target, targetPattern, `${edgeContext}.target`);
      const edgeKey = `${edge.path}\u0000${edge.target}`;
      if (edgeKeys.has(edgeKey)) fail(`${context} contains duplicate missing edge`);
      edgeKeys.add(edgeKey);
      credit(missCounts, missPrs, edgeKey, row.pr);
    }

    // Prove findings against immutable selection evidence.
    if (selection) {
      const result = selection.result;
      const inputPaths = new Set([...result.changedFiles, ...result.excludedFiles, ...result.unattributedFiles]);
      const selectedTargets = new Set([...result.testProjects.map(name => `test:${name}`), ...result.jobs]);
      if (!result.sourceHasDiff && row.over_paths.length > 0)
        fail(`${context}.over_paths requires selection-time diff attribution`);
      for (const pathValue of row.over_paths) {
        if (!inputPaths.has(pathValue)) {
          fail(`${context}.over_paths contains a path absent from trusted evidence`);
        }
      }
      for (const edge of row.miss_edges) {
        if (!inputPaths.has(edge.path)) {
          fail(`${context}.miss_edges contains a path absent from trusted evidence`);
        }
        if (selectedTargets.has(edge.target)) {
          fail(`${context}.miss_edges contains a target selected by trusted evidence`);
        }
      }
      markAffected(baselineRow);
      markAffected(row);
    }
  }

  // Preserve observations; require new trusted selections.
  if (hasProvenance) {
    for (const identity of baselineRows.keys()) {
      if (!processedKeys.has(identity)) fail(`missing baseline processed row ${identity}`);
    }
    for (const identity of trustedSelections.keys()) {
      if (!processedKeys.has(identity)) fail(`missing processed row for trusted selection ${identity}`);
    }
  }

  // Allow lifecycle-only changes without new evidence.
  const watchIdentity = row => row.kind === "over-selection"
    ? `over\u0000${row.path}`
    : `miss\u0000${row.path}\u0000${row.target}`;
  const baselineWatchRows = new Map();
  if (hasProvenance) {
    for (const line of fs.readFileSync(watchBefore, "utf8").split("\n")) {
      if (!line) continue;
      const row = JSON.parse(line);
      baselineWatchRows.set(watchIdentity(row), row);
    }
  }
  const withoutLifecycle = ({ verdict, ref, note, ...value }) => serialize(value);
  const lifecycleTransitions = new Map([
    ["pending-filed", new Set(["filed", "watch"])], ["filed", new Set(["in-flight", "fixed", "watch"])],
    ["in-flight", new Set(["fixed", "watch"])], ["fixed", new Set(["watch"])],
    ["correct-by-design", new Set(["watch"])]]);

  const watchAllowed = new Set([
    "path", "rule", "rule_ref", "path_ref", "consumer_refs", "target",
    "kind", "verdict", "all_runs", "miss_runs", "first_seen", "last_seen",
    "example_prs", "note", "ref"
  ]);
  const verdicts = new Set(["watch", "correct-by-design", "pending-filed", "filed", "in-flight", "fixed"]);
  const watch = readJsonLines("watchlist.jsonl");
  const watchKeys = new Set();

  // Check watch rows against counts and lifecycle changes.
  for (const [index, row] of watch.entries()) {
    const context = `watchlist.jsonl:${index + 1}`;
    requireKeys(row, ["path", "rule", "rule_ref", "path_ref", "kind", "verdict",
      "first_seen", "last_seen", "example_prs", "ref"], watchAllowed, context);
    pathOk(row.path, `${context}.path`);
    if (row.rule !== null) pathOk(row.rule, `${context}.rule`);
    refOk(row.rule_ref, `${context}.rule_ref`);
    refOk(row.path_ref, `${context}.path_ref`);
    requireUtcDate(row.first_seen, `${context}.first_seen`);
    requireUtcDate(row.last_seen, `${context}.last_seen`);
    if (row.first_seen > row.last_seen) fail(`${context}.first_seen is after last_seen`);
    if (!verdicts.has(row.verdict)) fail(`${context}.verdict is invalid`);
    if (row.ref !== null) requireInteger(row.ref, `${context}.ref`, 1);
    if (!Array.isArray(row.example_prs) || row.example_prs.length > 3) {
      fail(`${context}.example_prs has more than three entries`);
    }
    const examples = unique(row.example_prs,
      (pr, itemContext) => requireInteger(pr, itemContext, 1), `${context}.example_prs`);

    let key;
    let expectedCount;
    let contributingPrs;
    if (row.kind === "over-selection") {
      if ("target" in row || "consumer_refs" in row || "miss_runs" in row) {
        fail(`${context} has under-selection fields`);
      }
      requireInteger(row.all_runs, `${context}.all_runs`);
      key = `over\u0000${row.path}`;
      expectedCount = overCounts.get(row.path) || 0;
      contributingPrs = overPrs.get(row.path) || new Set();
    } else if (row.kind === "under-selection") {
      if ("all_runs" in row) fail(`${context} has all_runs`);
      requireString(row.target, targetPattern, `${context}.target`);
      requireInteger(row.miss_runs, `${context}.miss_runs`);
      if (!Array.isArray(row.consumer_refs) || row.consumer_refs.length === 0) {
        fail(`${context}.consumer_refs is required`);
      }
      unique(row.consumer_refs, refOk, `${context}.consumer_refs`);
      const edgeKey = `${row.path}\u0000${row.target}`;
      key = `miss\u0000${edgeKey}`;
      expectedCount = missCounts.get(edgeKey) || 0;
      contributingPrs = missPrs.get(edgeKey) || new Set();
    } else {
      fail(`${context}.kind is invalid`);
    }
    if (watchKeys.has(key)) fail(`duplicate watchlist identity ${key}`);
    watchKeys.add(key);

    const actualCount = row.kind === "over-selection" ? row.all_runs : row.miss_runs;
    if (actualCount !== expectedCount) {
      fail(`${context} counter ${actualCount} does not match ${expectedCount} processed heads`);
    }
    if (hasProvenance) {
      const baseline = baselineWatchRows.get(key);
      if (!same(baseline, row) && !affected.has(key)) {
        const transitionAllowed = baseline &&
          withoutLifecycle(baseline) === withoutLifecycle(row) &&
          lifecycleTransitions.get(baseline.verdict)?.has(row.verdict);
        if (!transitionAllowed) {
          fail(`${context} changes a watch row without trusted current evidence`);
        }
      }
      if (!baseline && actualCount === 0) {
        fail(`${context} adds a zero-count watch row`);
      }
    }
    for (const pr of examples) {
      if (!contributingPrs.has(pr)) fail(`${context}.example_prs contains uncredited PR ${pr}`);
    }

    // Enforce issue metadata for each lifecycle state.
    if (row.verdict === "pending-filed") {
      requireString(row.note, titlePattern, `${context}.note`, 100);
      if (row.ref !== null) fail(`${context}.ref must be null while pending-filed`);
    } else if ("note" in row) {
      fail(`${context}.note is allowed only for pending-filed rows`);
    }
    if (["filed", "in-flight", "fixed"].includes(row.verdict) && row.ref === null) {
      fail(`${context}.ref is required for ${row.verdict}`);
    }
    if (["watch", "correct-by-design"].includes(row.verdict) && row.ref !== null) {
      fail(`${context}.ref must be null for ${row.verdict}`);
    }
  }

  // Preserve watch history and cover every derived contribution.
  if (hasProvenance) {
    for (const [key, baseline] of baselineWatchRows) {
      if (!watchKeys.has(key) && !(baseline.verdict === "watch" && affected.has(key))) {
        fail(`missing baseline watch row ${key}`);
      }
    }
  }
  for (const pathValue of overCounts.keys()) {
    if (!watchKeys.has(`over\u0000${pathValue}`)) fail(`missing watchlist row for ${pathValue}`);
  }
  for (const edgeKey of missCounts.keys()) {
    if (!watchKeys.has(`miss\u0000${edgeKey}`)) fail(`missing watchlist row for missing edge`);
  }
}

module.exports = { validateMemory };

if (require.main === module) {
  validateMemory(process.argv[2]);
}
