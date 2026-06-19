"""Compute user-facing signals for the pr-docs-check workflow.

This script is invoked by `.github/workflows/pr-docs-check.md` as a
pre-agent step. It writes a JSON document containing a fixed catalog of
boolean "user-facing change" signals derived from objective evidence:

  - Changed-file paths (Group A)
  - Diff hunk contents — added and/or removed lines (Group B)
  - PR body regexes (Group C)
  - PR labels (Group D)
  - An advisory "only_test_or_build_changes" flag (never gates)

The agent reads the resulting file verbatim and treats
`recommendation == "docs_required"` as a hard "draft a docs PR" gate.
Each triggered signal carries evidence (file path + matching diff
fragment or PR-body snippet) so the audit trail is reproducible.

Usage
-----

    python3 compute_signals.py <pr.json> <files.json> <out.json>

where `pr.json` is the body of `GET /repos/microsoft/aspire/pulls/{N}`
and `files.json` is the concatenated body of
`GET /repos/microsoft/aspire/pulls/{N}/files?per_page=100` (paginated
and JSON-arrayed).

The catalog is intentionally broad. The worst case for a false positive
is a drafted docs PR that a human closes (drafted PRs never auto-merge),
while a false negative ships an undocumented user-facing change. Favor
recall over precision.

Running the tests
-----------------

    python3 -m unittest discover -s .github/workflows/pr-docs-check -v
"""

from __future__ import annotations

import json
import re
import sys
from typing import Callable, Pattern


# ============================================================
# Group A: Path-pattern triggers
# ============================================================
# Each entry: (signal_name, status_filter, path_regex)
# status_filter is one of:
#   "added" — file is brand new in this PR (status == "added")
#   "any"   — file was added, modified, or renamed
#
# Examples of paths these patterns must match:
#   src/Aspire.Cli/Commands/LogsCommand.cs                -> cli_command_*
#   src/Aspire.Cli/Mcp/Tools/ListConsoleLogsTool.cs       -> mcp_tool_*
#   src/Aspire.Hosting.Redis/Aspire.Hosting.Redis.csproj  -> new_hosting_integration_project
#   src/Components/Aspire.StackExchange.Redis/Aspire.StackExchange.Redis.csproj -> new_client_integration_project
#   src/Aspire.Hosting/api/Aspire.Hosting.cs              -> public_api_surface_file_changed
#   src/Aspire.Dashboard/Components/Pages/Resources.razor -> dashboard_user_facing_page_changed
#   src/Aspire.Hosting.Redis/README.md                    -> integration_readme_changed
#   src/Aspire.Hosting.Redis/RedisContainerImageTags.cs   -> container_image_tags_file_changed
#   src/Aspire.ProjectTemplates/templates/...             -> project_template_changed
#   src/Aspire.Hosting.Analyzers/AppHostAnalyzer.cs       -> analyzer_source_changed
#   docs/list-of-diagnostics.md                           -> diagnostic_documentation_changed
#   src/Aspire.Hosting/ApplicationModel/KnownDefaults.cs  -> defaults_or_constants_file_changed
PATH_TRIGGERS: list[tuple[str, str, str]] = [
    # New CLI subcommand file.
    ("cli_command_added", "added",
     r"^src/Aspire\.Cli/Commands/(?!BaseCommand\.cs$)(?!.*CommandBase\.cs$).+Command\.cs$"),
    # Any change to an existing CLI subcommand file (option set,
    # behavior, output format, confirmation prompts).
    ("cli_command_file_changed", "any",
     r"^src/Aspire\.Cli/Commands/(?!BaseCommand\.cs$)(?!.*CommandBase\.cs$).+Command\.cs$"),
    # Any change to a CLI resx. These hold help text, option
    # descriptions, prompts, and error messages the CLI prints.
    ("cli_resource_strings_changed", "any",
     r"^src/Aspire\.Cli/Resources/.+\.resx$"),
    # New MCP tool file.
    ("mcp_tool_added", "added",
     r"^src/Aspire\.Cli/Mcp/Tools/(?!CliMcpTool\.cs$).+Tool\.cs$"),
    # Any change to an existing MCP tool file (input schema,
    # output shape, semantics).
    ("mcp_tool_file_changed", "any",
     r"^src/Aspire\.Cli/Mcp/Tools/(?!CliMcpTool\.cs$).+\.cs$"),
    # Any new csproj under src/ — a brand-new shipping NuGet
    # package. Covers integrations, SDKs, analyzers, etc.
    ("new_package_added", "added",
     r"^src/.+/[^/]+\.csproj$"),
    # Subsets of new_package_added kept around so the prompt can
    # name the integration type directly in the audit summary.
    ("new_hosting_integration_project", "added",
     r"^src/Aspire\.Hosting\.[^/]+/[^/]+\.csproj$"),
    ("new_client_integration_project", "added",
     r"^src/Components/Aspire\.[^/]+/[^/]+\.csproj$"),
    # Package READMEs ship to nuget.org and are linked from
    # docs.aspire.dev integration pages.
    ("integration_readme_changed", "any",
     r"^src/(Aspire\.Hosting[^/]*|Components/Aspire\.[^/]+)/README\.md$"),
    # api/*.cs is the shipped public-API baseline. AGENTS.md says
    # these are regenerated only at release time, so any
    # committed diff is an explicit shipping-API change. New
    # entries = new APIs; removed entries = breaking removals
    # (caught separately as breaking_api_removal below).
    ("public_api_surface_file_changed", "any",
     r"^src/[^/]+/api/.+\.cs$"),
    # Any dashboard page (razor / razor.cs / .cs codebehind).
    ("dashboard_user_facing_page_changed", "any",
     r"^src/Aspire\.Dashboard/Components/Pages/.+\.(razor|razor\.cs|cs)$"),
    # Aspire integrations follow a `<Name>ContainerImageTags.cs`
    # convention for pinning container image name + tag. Any
    # change here typically means the image version moved.
    ("container_image_tags_file_changed", "any",
     r"^src/.+ContainerImageTags\.cs$"),
    # Aspire project templates ship via `dotnet new aspire-*` and
    # via `aspire init`. Any change is user-facing.
    ("project_template_changed", "any",
     r"^src/Aspire\.ProjectTemplates/.+$"),
    # The user-facing diagnostic catalog page.
    ("diagnostic_documentation_changed", "any",
     r"^docs/list-of-diagnostics\.md$"),
    # Roslyn analyzers — users see new build warnings/errors.
    ("analyzer_source_changed", "any",
     r"^src/Aspire\.(Hosting|AppHost)\.Analyzers/.+\.cs$"),
    # Files whose name ends in `Defaults.cs` or `Constants.cs`
    # typically hold shipping default values (timeouts, retry
    # counts, well-known property names, image tags, etc.).
    ("defaults_or_constants_file_changed", "any",
     r"^src/.+(Defaults|Constants)\.cs$"),
]


# ============================================================
# Group B: Diff-content triggers
# ============================================================
# Each entry: (signal_name, path_regex, direction, line_regex)
# direction is one of:
#   "added"   — scan added lines (those starting with "+")
#   "removed" — scan removed lines (those starting with "-")
#   "any"    — scan both directions (for signals where a deletion is
#               equally user-facing — e.g. "this API surface changed",
#               "this image version changed", "this TFM changed")
# All directions skip the diff file headers "+++ b/..." and "--- a/...".
#
# Examples of lines these patterns must match:
#   `+    private static readonly Option<string?> s_searchOption = new("--search")`
#     -> cli_option_added (C# 9+ target-typed `new(...)`)
#   `+    [Obsolete("Use AddBar instead.")]`
#     -> obsolete_attribute_added
#   `+    [Experimental("ASPIREPREVIEW001")]`
#     -> experimental_attribute_added
#   `+public sealed class FooBuilder`
#     -> new_public_type
#   `-        public static IResourceBuilder<FooResource> AddFoo(...) { throw null; }`
#     (inside src/Aspire.Hosting.Foo/api/Aspire.Hosting.Foo.cs)
#     -> breaking_api_removal
#   `+    public const string Tag = "8.0";`  (inside *ContainerImageTags.cs)
#     -> container_image_version_changed
#   `-    public const string Tag = "8.0";`  (removal of a tag pin
#     without a replacement line — also gates dashboard / image / TFM)
#     -> container_image_version_changed
#   `+    <TargetFramework>net10.0</TargetFramework>`
#     -> target_framework_changed
DIFF_TRIGGERS: list[tuple[str, str, str, Pattern[str]]] = [
    # New CLI option declaration. Covers:
    #   1. Classic explicit         `new Option<T>(...)`
    #   2. Target-typed             `Option<T> x = new(...)`
    #   3. Expression-bodied        `Option<T> M() => new(...)`
    # The second branch requires `\s+\w+` after `Option<...>` so
    # generic nesting like `IEnumerable<Option<T>>` does not match.
    ("cli_option_added",
     r"^src/Aspire\.Cli/.+\.cs$", "added",
     re.compile(
         r"(?:\bnew\s+Option<[^>]+>\s*\("
         r"|Option<[^>]+>\s+\w+(?:\s*=|\s*\([^)]*\)\s*=>)\s*new\s*\()"
     )),
    # Any non-blank line added or removed in dashboard API surface
    # files. Endpoint removals are equally user-facing — they break
    # callers — so this trigger scans both directions.
    # (See https://github.com/microsoft/aspire/pull/16983 review.)
    ("dashboard_api_endpoint_changed",
     r"^src/Aspire\.Dashboard/(Api/.+\.cs|DashboardEndpointsBuilder\.cs)$",
     "any",
     re.compile(r"\S")),
    # [Obsolete(...)] addition anywhere in src/. Tolerates either
    # the shorthand attribute name or the *Attribute form.
    ("obsolete_attribute_added",
     r"^src/.+\.cs$", "added",
     re.compile(r"\[Obsolete(?:Attribute)?\s*[\(\]]")),
    # [Experimental(...)] addition — marks new preview /
    # experimental APIs that users opt into.
    ("experimental_attribute_added",
     r"^src/.+\.cs$", "added",
     re.compile(r"\[Experimental(?:Attribute)?\s*[\(\]]")),
    # New public type declaration in non-test source. Matches
    # class / interface / struct / record / record class /
    # record struct / enum / delegate. Excludes paths under
    # *.Tests/, *.UnitTests/, *.IntegrationTests/ so internal
    # test helpers don't trip the signal.
    ("new_public_type",
     r"^src/(?!.*\.Tests/)(?!.*\.UnitTests/)(?!.*\.IntegrationTests/).+\.cs$",
     "added",
     re.compile(
         r"^\s*public\s+"
         r"(?:static\s+|sealed\s+|abstract\s+|partial\s+|readonly\s+|ref\s+|unsafe\s+|new\s+)*"
         r"(?:class|interface|struct|record(?:\s+(?:class|struct))?|enum|delegate)\s+\w+"
     )),
    # Removed declaration line in an api/*.cs file. Because
    # api/*.cs is append-only between releases, a removed line
    # that declares a public/protected member is a strong
    # breaking-change indicator. A whitespace-only reformat can
    # also trip this — acceptable false positive under the
    # inverted default.
    ("breaking_api_removal",
     r"^src/[^/]+/api/.+\.cs$", "removed",
     re.compile(r"^\s*(?:public|protected)\s+")),
    # Tag / Image / Registry / Digest changes inside
    # *ContainerImageTags.cs files. Scanned in both directions so
    # that a pure removal of a pin (without an immediate added
    # replacement) still fires.
    # (See https://github.com/microsoft/aspire/pull/16983 review.)
    ("container_image_version_changed",
     r"^src/.+ContainerImageTags\.cs$", "any",
     re.compile(r"\b(?:Tag|Image|Registry|Digest)\s*=\s*\"")),
    # [DefaultValue(...)] anywhere in src/. Captures defaults
    # declared via attribute even when the file isn't named
    # *Defaults.cs / *Constants.cs.
    ("default_value_attribute_changed",
     r"^src/.+\.cs$", "added",
     re.compile(r"\[DefaultValue(?:Attribute)?\s*\(")),
    # TargetFramework / TargetFrameworks change in a src/
    # csproj. Moving an integration's TFM affects which
    # consumers can install it. Scanned in both directions so
    # that removing the element (or removing a framework from a
    # multi-target list) without an immediate added replacement
    # still fires.
    # (See https://github.com/microsoft/aspire/pull/16983 review.)
    ("target_framework_changed",
     r"^src/.+\.csproj$", "any",
     re.compile(r"<TargetFrameworks?>")),
]


# ============================================================
# Group C: PR-body triggers
# ============================================================
# Author-supplied prose signals.
#
# Examples of bodies these match:
#   "## User-facing usage\n```bash\naspire logs --search ..."
#     -> pr_body_has_user_facing_section AND
#        pr_body_has_cli_flag_mention
#   "Breaking change: removes deprecated AddFoo overload."
#     -> pr_body_has_breaking_change_marker AND
#        pr_body_has_deprecation_marker
#   "Fixes CVE-2026-12345 in the dashboard API."
#     -> pr_body_has_security_marker
BODY_TRIGGERS: dict[str, Pattern[str]] = {
    # Common headers in PR bodies that signal user-facing intent.
    "pr_body_has_user_facing_section": re.compile(
        r"(?im)^\s{0,3}#{1,6}\s*(user[-_ ]?facing|usage|how\s+to\s+use|breaking\s+change)\b"
    ),
    # Any long-form CLI flag mention (e.g. --search, --dashboard-url).
    "pr_body_has_cli_flag_mention": re.compile(
        r"(?<![A-Za-z0-9])--[a-z][a-z0-9-]+\b"
    ),
    # The literal phrase "breaking change" anywhere in the body.
    "pr_body_has_breaking_change_marker": re.compile(
        r"(?i)\bbreaking[\s\-]?change\b"
    ),
    # Security advisories: CVE-YYYY-N, GHSA-xxxx-xxxx-xxxx, or
    # explicit "security fix" / "security advisory" /
    # "vulnerability" phrasing.
    "pr_body_has_security_marker": re.compile(
        r"(?i)("
        r"\bCVE-\d{4}-\d+\b"
        r"|\bGHSA(?:-[a-z0-9]{4,}){3}\b"
        r"|\bsecurity\s+(?:fix|advisory|patch|issue|update|vulnerab\w+)\b"
        r"|\bvulnerab\w+"
        r")"
    ),
    # Deprecation phrasing: `deprecat*`, `obsolet*`, or
    # "<api> has been removed/sunset/retired" patterns.
    "pr_body_has_deprecation_marker": re.compile(
        r"(?i)("
        r"\bdeprecat\w+"
        r"|\bobsolet\w+"
        r"|\b(?:api|method|class|property|option|flag|package|integration|attribute|extension)s?\s+(?:is|are|has\s+been|have\s+been|were|will\s+be|now)\s+(?:removed|sunset|retired)\b"
        r")"
    ),
}


# ============================================================
# Group D: PR-label triggers
# ============================================================
# Author/maintainer-curated labels are very signal-dense when set.
# Examples of label names these match:
#   "breaking-change" / "api: breaking" / "kind:breaking"
#     -> pr_label_breaking_change
#   "security" / "security/dashboard" / "kind: security"
#     -> pr_label_security
_SECURITY_LABEL_RE = re.compile(r"(?i)(^|[\s\-_/:])security(\b|[\s\-_/:])")
LABEL_TRIGGERS: dict[str, Callable[[list[str]], bool]] = {
    "pr_label_breaking_change": lambda labs: any(
        "breaking" in (lab or "").lower() for lab in labs
    ),
    "pr_label_security": lambda labs: any(
        bool(_SECURITY_LABEL_RE.search(lab or "")) for lab in labs
    ),
}


# ============================================================
# Advisory: only_test_or_build_changes
# ============================================================
# True iff EVERY changed file falls in test/build/CI/playground/
# docs/agent buckets. Used only by the prompt's Step 5 allowlist —
# it never forces docs_required and is deliberately excluded from
# `triggered_signals`.
_ONLY_TEST_OR_BUILD_RE = re.compile(
    r"^(tests/|eng/|playground/|docs/|\.github/|\.agents/|"
    r"\.editorconfig$|global\.json$|"
    r"Directory\.(Build|Packages)\.props$|Directory\.Build\.targets$)"
)


def _trim_hint(text: str, limit: int = 200) -> str:
    """Trim a single-line evidence hint so signals.json stays readable."""
    text = text.strip()
    if len(text) > limit:
        text = text[: limit - 3] + "..."
    return text


def compute_signals(pr: dict, files: list[dict]) -> dict:
    """Compute the full signals.json document for a given PR payload.

    Args:
        pr: The body of `GET /repos/microsoft/aspire/pulls/{N}` as a dict.
        files: The concatenated body of `GET .../files?per_page=100`
            (already paginated and flattened into a list).

    Returns:
        A dict ready to be JSON-serialized as `.pr-docs-check/signals.json`.
    """
    pr_body = pr.get("body") or ""
    pr_labels = [(lab.get("name") or "") for lab in (pr.get("labels") or [])]

    signals: dict[str, bool] = {}
    evidence: dict[str, list[dict]] = {}

    def record(signal_name: str, file_path: str, hint: str) -> None:
        evidence.setdefault(signal_name, []).append({"file": file_path, "hint": hint})

    # ---- Group A: path triggers ----
    for signal_name, status_filter, path_regex in PATH_TRIGGERS:
        regex = re.compile(path_regex)
        signals.setdefault(signal_name, False)
        for f in files:
            filename = f.get("filename") or ""
            status = f.get("status") or ""
            if status_filter == "added" and status != "added":
                continue
            if regex.match(filename):
                signals[signal_name] = True
                record(signal_name, filename, f"path matched {path_regex}")

    # ---- Group B: diff-content triggers ----
    #
    # A note on "missing patch" handling: the GitHub API omits the `patch`
    # field for very large files. The previous implementation silently
    # skipped those files, which is a false-negative risk under the
    # "favor recall" policy: a path that matched a diff-trigger but
    # whose patch wasn't available would never gate, even when the
    # filename strongly suggests a user-facing change. We now record
    # those skips as the global `diff_scan_skipped_due_to_missing_patch`
    # signal so the agent treats them as conservative gating evidence.
    # (See https://github.com/microsoft/aspire/pull/16983 review.)
    signals.setdefault("diff_scan_skipped_due_to_missing_patch", False)

    for signal_name, path_regex, direction, line_regex in DIFF_TRIGGERS:
        path_re = re.compile(path_regex)
        signals.setdefault(signal_name, False)
        for f in files:
            filename = f.get("filename") or ""
            if not path_re.match(filename):
                continue
            patch = f.get("patch") or ""
            if not patch:
                # The file matches a diff-trigger path but GitHub did
                # not return a patch for it (typically because the
                # file is too large). Treat this as conservative
                # evidence that the diff probably contains a relevant
                # change we cannot verify here, and let the agent
                # gate on it.
                signals["diff_scan_skipped_due_to_missing_patch"] = True
                record(
                    "diff_scan_skipped_due_to_missing_patch",
                    filename,
                    f"would have scanned for {signal_name} (path matched {path_regex})",
                )
                continue
            for line in patch.splitlines():
                # Always skip the diff file headers "+++ b/path" and
                # "--- a/path" — they would otherwise match many
                # regexes spuriously, especially the "any non-blank
                # line" patterns.
                if line.startswith("+++") or line.startswith("---"):
                    continue
                if direction == "added":
                    if not line.startswith("+"):
                        continue
                elif direction == "removed":
                    if not line.startswith("-"):
                        continue
                elif direction == "any":
                    if not (line.startswith("+") or line.startswith("-")):
                        continue
                else:
                    # Defensive — a typo in the DIFF_TRIGGERS table
                    # should fail loudly during local testing rather
                    # than silently disable the signal.
                    raise ValueError(
                        f"Unknown direction {direction!r} for signal {signal_name!r}"
                    )
                content = line[1:]
                m = line_regex.search(content)
                if m:
                    signals[signal_name] = True
                    # Annotate the hint with the sign so a removed-line
                    # match is distinguishable from an added-line one
                    # when scanning the audit trail.
                    prefix = "+" if line.startswith("+") else "-"
                    record(signal_name, filename, _trim_hint(f"{prefix}{content}"))
                    break  # one match per file is enough for evidence

    # ---- Group C: PR-body triggers ----
    for signal_name, regex in BODY_TRIGGERS.items():
        m = regex.search(pr_body)
        signals[signal_name] = bool(m)
        if m:
            # Capture a small surrounding window so the evidence helps
            # a human auditor understand why the regex matched.
            start = max(0, m.start() - 20)
            end = min(len(pr_body), m.end() + 60)
            hint = pr_body[start:end].replace("\n", " ").replace("\r", " ")
            record(signal_name, "<pr-body>", _trim_hint(hint))

    # ---- Group D: PR-label triggers ----
    for signal_name, predicate in LABEL_TRIGGERS.items():
        matched = predicate(pr_labels)
        signals[signal_name] = bool(matched)
        if matched:
            # Record the matching label names so the audit trail shows
            # exactly which label fired the signal.
            matched_labels = [lab for lab in pr_labels if predicate([lab])]
            record(signal_name, "<pr-labels>", _trim_hint(", ".join(matched_labels)))

    # ---- Advisory: only_test_or_build_changes ----
    if files:
        signals["only_test_or_build_changes"] = all(
            _ONLY_TEST_OR_BUILD_RE.match((f.get("filename") or "")) for f in files
        )
    else:
        signals["only_test_or_build_changes"] = False

    # `only_test_or_build_changes` is advisory — exclude it from
    # triggered_signals so it can't accidentally force docs_required.
    gating_signals = [
        name for name, v in signals.items()
        if v and name != "only_test_or_build_changes"
    ]

    return {
        "source_pr_number": int(pr.get("number") or 0),
        "triggered_signals": sorted(gating_signals),
        "signal_count": len(gating_signals),
        "recommendation": "docs_required" if gating_signals else "docs_optional",
        "signals": signals,
        # Evidence is only emitted for triggered signals to keep the
        # file small and to avoid confusing the agent with empty arrays.
        "evidence": {k: v for k, v in evidence.items() if signals.get(k)},
    }


def main(argv: list[str]) -> int:
    if len(argv) != 4:
        print(
            "usage: compute_signals.py <pr.json> <files.json> <out.json>",
            file=sys.stderr,
        )
        return 2
    pr_json_path, files_json_path, out_path = argv[1], argv[2], argv[3]

    with open(pr_json_path, "r", encoding="utf-8") as f:
        pr = json.load(f)
    with open(files_json_path, "r", encoding="utf-8") as f:
        files = json.load(f)

    result = compute_signals(pr, files)

    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=2, sort_keys=True)
        f.write("\n")

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
