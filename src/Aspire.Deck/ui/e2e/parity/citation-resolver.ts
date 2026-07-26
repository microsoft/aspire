import { readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

/**
 * Resolves the coverage citations recorded in the parity ledger against the tests that are actually
 * present in the repository.
 *
 * The ledger is a claim, not evidence. Before this resolver existed the only assertion on
 * `currentCoverage` was that it was a non-empty string, so a citation could name a test that had
 * been renamed, deleted, or never written and the ledger would still report full parity. Every
 * citation now has to resolve to something real, and the failure message says which kind of thing
 * was expected.
 */

const uiRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const repoRoot = resolve(uiRoot, "../../..");

export type CitationKind =
  | "playwright-feature"
  | "dotnet-test"
  | "snapshot-file"
  | "commit";

export interface CitationResolution {
  citation: string;
  kind: CitationKind | null;
  resolved: boolean;
  detail: string;
}

/** Playwright feature IDs look like `APP-SHELL-001`. */
const featureIdPattern = /^[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-\d+$/;
/** Git commit citations are used for "this was deliberately removed upstream" entries. */
const commitPattern = /^[0-9a-f]{7,40}$/;
/** Snapshot or fixture files are cited by name, e.g. `toolkit.aria.yml`. */
const filePattern = /^[\w.-]+\.(?:yml|yaml|md|json|txt)$/;
/** .NET citations are either `ClassTests` or `ClassTests.MethodName`. */
const dotnetPattern = /^[A-Za-z_]\w*Tests(?:\.[A-Za-z_]\w*)?$/;

function walk(directory: string, accumulator: string[] = []): string[] {
  for (const entry of readdirSync(directory)) {
    const path = join(directory, entry);
    if (statSync(path).isDirectory()) {
      walk(path, accumulator);
    } else {
      accumulator.push(path);
    }
  }
  return accumulator;
}

let cache: {
  declaredFeatures: Set<string>;
  referencedFeatures: Set<string>;
  snapshotFiles: Set<string>;
  dotnetSymbols: Set<string>;
} | null = null;

function index(): NonNullable<typeof cache> {
  if (cache !== null) {
    return cache;
  }

  const e2eFiles = walk(join(uiRoot, "e2e"));

  // Feature IDs are declared as the keys of the `*-features.ts` registries.
  const declaredFeatures = new Set<string>();
  for (const file of e2eFiles.filter((f) => f.endsWith("-features.ts"))) {
    for (const match of readFileSync(file, "utf8").matchAll(/^\s*"([A-Z][A-Z0-9-]*-\d+)"\s*:/gm)) {
      declaredFeatures.add(match[1]!);
    }
  }

  // A declaration alone proves nothing; the ID must also be attached to a test. Specs reference IDs
  // either through the `features(...)` helper or as a `[ID]` prefix in the test title.
  const referencedFeatures = new Set<string>();
  for (const file of e2eFiles.filter((f) => f.endsWith(".spec.ts"))) {
    const contents = readFileSync(file, "utf8");
    for (const match of contents.matchAll(/"([A-Z][A-Z0-9-]*-\d+)"/g)) {
      referencedFeatures.add(match[1]!);
    }
    for (const match of contents.matchAll(/\[([A-Z][A-Z0-9-]*-\d+)\]/g)) {
      referencedFeatures.add(match[1]!);
    }
  }

  const snapshotFiles = new Set(e2eFiles.map((file) => file.slice(file.lastIndexOf("/") + 1)));

  // Type and method names from the .NET test projects. Matching on declarations rather than any
  // occurrence keeps a citation from being satisfied by an unrelated mention in a comment.
  const dotnetSymbols = new Set<string>();
  for (const file of walk(join(repoRoot, "tests")).filter((f) => f.endsWith(".cs"))) {
    const contents = readFileSync(file, "utf8");
    for (const match of contents.matchAll(/\b(?:class|record)\s+(\w*Tests)\b/g)) {
      dotnetSymbols.add(match[1]!);
    }
    for (const match of contents.matchAll(/\b(?:async\s+)?(?:Task|ValueTask|void)\s+(\w+)\s*\(/g)) {
      dotnetSymbols.add(match[1]!);
    }
  }

  cache = { declaredFeatures, referencedFeatures, snapshotFiles, dotnetSymbols };
  return cache;
}

/** Splits a ledger `currentCoverage` value into its individual citations. */
export function parseCitations(currentCoverage: string | null): string[] {
  if (currentCoverage === null) {
    return [];
  }

  return currentCoverage
    .split(";")
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0);
}

export function resolveCitation(citation: string): CitationResolution {
  const { declaredFeatures, referencedFeatures, snapshotFiles, dotnetSymbols } = index();

  if (featureIdPattern.test(citation)) {
    // Being attached to a test is the evidence that matters. The `*-features.ts` registries are
    // documentation layered on top: most specs pull IDs from them via `features(...)`, but some
    // declare the ID inline as a `[ID]` title prefix, and both are equally real coverage.
    if (referencedFeatures.has(citation)) {
      return { citation, kind: "playwright-feature", resolved: true, detail: "" };
    }
    if (declaredFeatures.has(citation)) {
      return {
        citation,
        kind: "playwright-feature",
        resolved: false,
        detail: "declared in a *-features.ts registry but no test in e2e/**/*.spec.ts references it"
      };
    }
    return {
      citation,
      kind: "playwright-feature",
      resolved: false,
      detail: "not referenced by any test in e2e/**/*.spec.ts"
    };
  }

  if (filePattern.test(citation)) {
    return snapshotFiles.has(citation)
      ? { citation, kind: "snapshot-file", resolved: true, detail: "" }
      : { citation, kind: "snapshot-file", resolved: false, detail: "no such file under e2e/" };
  }

  if (dotnetPattern.test(citation)) {
    const symbol = citation.includes(".") ? citation.slice(citation.lastIndexOf(".") + 1) : citation;
    return dotnetSymbols.has(symbol)
      ? { citation, kind: "dotnet-test", resolved: true, detail: "" }
      : { citation, kind: "dotnet-test", resolved: false, detail: `no test type or method named '${symbol}' under tests/` };
  }

  if (commitPattern.test(citation)) {
    // Commit citations document a deliberate upstream removal. They are not test coverage, so they
    // are only accepted on entries the ledger itself marks as removed; that check lives in the spec.
    return { citation, kind: "commit", resolved: true, detail: "" };
  }

  return {
    citation,
    kind: null,
    resolved: false,
    detail: "does not look like a feature ID, .NET test, snapshot file, or commit SHA"
  };
}
