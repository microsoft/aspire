"""Validation at the boundary between untrusted model proposals and orchestration."""

from datetime import datetime, timezone
from pathlib import PurePosixPath
import re


REPOSITORY = "microsoft/aspire"
AGENT = "flaky-test-investigator.lock.yml"
WORKER = "reproduce-flaky-tests.yml"
LIMITS = {"models": 8, "workers": 5, "fixes": 2, "runner_iterations": 180}
TERMINAL = {
    "verified", "not-flaky", "ambiguous", "infrastructure", "not-reproduced",
    "budget-exhausted", "blocked", "cancelled",
}
OSES = {"ubuntu-latest", "windows-latest", "macos-latest"}
SHA = re.compile(r"[0-9a-f]{40}")
IDENTIFIER = re.compile(r"[A-Za-z_][A-Za-z_0-9]*")


class BudgetExceeded(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise ValueError(message)


def issue_number(url):
    require(isinstance(url, str), "An Aspire issue URL is required")
    match = re.fullmatch(r"https://github\.com/microsoft/aspire/issues/([1-9][0-9]{0,9})", url)
    require(match is not None, "Expected an exact microsoft/aspire issue URL")
    return int(match[1])


def text(value, maximum=8000):
    require(isinstance(value, str) and 0 < len(value.strip()) <= maximum,
            "Expected bounded nonempty text")
    require(not re.search(r"github_pat_|gh[pousr]_[A-Za-z0-9]{15}|[?&](?:sig|token)=", value),
            "Refusing credential-shaped output")
    return value


def integer(value, maximum, minimum=1):
    require(type(value) is int and minimum <= value <= maximum, "Invalid bounded integer")
    return value


def sha(value):
    require(isinstance(value, str) and SHA.fullmatch(value), "Expected a full immutable Git SHA")
    return value


def source_path(value):
    require(isinstance(value, str) and re.fullmatch(r"(src|tests)/[A-Za-z0-9_./-]+\.cs", value),
            "Only existing C# source under src/ or tests/ is eligible")
    path = PurePosixPath(value)
    require(".." not in path.parts and "/api/" not in value
            and not value.endswith((".Designer.cs", ".g.cs", ".generated.cs")),
            "Generated or traversal paths are forbidden")
    return value


def selection(proposal, issue, files):
    """files is the immutable source tree, never a model-supplied catalog."""
    require(isinstance(proposal, dict), "Missing test selection")
    source = source_path(proposal.get("source"))
    project = proposal.get("project")
    require(source.startswith("tests/") and source in files, "Test source does not exist")
    require(isinstance(project, str) and re.fullmatch(r"tests/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\.csproj", project)
            and project in files and source.startswith(str(PurePosixPath(project).parent) + "/"),
            "Test must belong to the selected existing project directory")
    method = proposal.get("method")
    class_name = proposal.get("class_name")
    require(isinstance(method, str) and IDENTIFIER.fullmatch(method), "Invalid exact method")
    require(isinstance(class_name, str)
            and re.fullmatch(r"[A-Za-z_][A-Za-z_0-9]*(?:\.[A-Za-z_][A-Za-z_0-9]*)+", class_name),
            "Invalid fully qualified test class")
    source_text = files[source]
    # Match ordinary C# declarations, not call sites. Unusual generated/nested tests
    # deliberately stop for a human rather than broadening an execution filter.
    declarations = list(re.finditer(
        rf"\bpublic\s+(?:(?:async|static|override|virtual)\s+)*(?:Task(?:<[^>]+>)?|ValueTask|void)\s+{re.escape(method)}\s*\(",
        source_text))
    require(len(declarations) == 1, "Test declaration is missing or ambiguous")
    namespace, _, short_class = class_name.rpartition(".")
    require(re.search(rf"\bnamespace\s+{re.escape(namespace)}\s*[;{{]", source_text)
            and re.search(rf"\bclass\s+{re.escape(short_class)}\b", source_text),
            "Class/namespace does not match source")
    attributes = source_text[max(0, declarations[0].start() - 1500):declarations[0].start()]
    attributes = attributes[attributes.rfind("}") + 1:]
    require(re.search(r"\[(?:Fact|Theory|QuarantinedTest)\b", attributes),
            "Selected declaration is not an ordinary xUnit test")
    issue_url = issue["html_url"]
    require(issue_url in attributes or method in (issue.get("title", "") + "\n" + (issue.get("body") or "")),
            "Issue does not identify this test")
    oses = proposal.get("oses")
    require(isinstance(oses, list) and 1 <= len(oses) <= 3
            and all(isinstance(item, str) and item in OSES for item in oses)
            and len(set(oses)) == len(oses),
            "Unknown or duplicated runner OS")
    signature = text(proposal.get("failure_signature"), 500)
    require(signature in (issue.get("body") or "") or signature in issue.get("title", ""),
            "Failure signature must be a literal excerpt from the issue")
    return {
        "source": source, "project": project, "class_name": class_name,
        "method": method, "oses": oses, "failure_signature": signature,
        "quarantined": issue_url in attributes and "QuarantinedTest" in attributes,
    }


def validate_candidate(files, originals):
    require(isinstance(files, dict) and 1 <= len(files) <= 5, "Candidate requires 1-5 source files")
    require(sum(len(value.encode("utf-8")) for value in files.values() if isinstance(value, str)) <= 250000,
            "Candidate file content exceeds budget")
    changed_lines = 0
    import difflib
    for path, proposed in files.items():
        source_path(path)
        require(path in originals and isinstance(proposed, str) and "\x00" not in proposed,
                "Candidate may only replace existing regular source files")
        text(proposed, 250000)
        original = originals[path]
        require(proposed != original, "Candidate includes an unchanged file")
        # Scheduling changes are not a repair mechanism. Preserve attributes
        # even when a repair is in a different test or shared test helper.
        attributes = lambda content: re.findall(
            r"\[[^\]]*\b(?:QuarantinedTest|ActiveIssue|OuterloopTest|Fact|Theory|InlineData|MemberData|ClassData)\b[^\]]*\]",
            content)
        require(attributes(proposed) == attributes(original), "Test scheduling/data attributes must remain unchanged")
        for line in difflib.ndiff(original.splitlines(), proposed.splitlines()):
            if line.startswith(("+ ", "- ")):
                changed_lines += 1
                require(not re.search(r"\b(?:Skip|SkipWhen|SkipUnless)\s*=", line),
                        "Candidate cannot introduce test skipping")
    require(0 < changed_lines <= 800, "Candidate diff exceeds the 800-line budget")
    return files


def repair_scope(project, projects):
    """Follow explicit repository project references without evaluating MSBuild."""
    import posixpath
    import xml.etree.ElementTree as ET
    pending = [project]
    visited = set()
    while pending:
        current = pending.pop()
        if current in visited:
            continue
        require(len(visited) < 150 and current in projects, "Unresolved source project reference")
        visited.add(current)
        directory = posixpath.dirname(current)
        root = ET.fromstring(projects[current])
        for reference in root.iter("ProjectReference"):
            include = reference.get("Include", "")
            # Dynamic/glob references are not permission to edit arbitrary source.
            # Their consumers can still be diagnosed, but need a human repair.
            if any(char in include for char in "$*;"):
                continue
            resolved = posixpath.normpath(posixpath.join(directory, include.replace("\\", "/")))
            if resolved in projects:
                pending.append(resolved)
    return sorted({posixpath.dirname(path) + "/" for path in visited})


def reserve(state, kind, amount=1):
    require(kind in LIMITS, "Unknown budget")
    used = state["used"][kind] + integer(amount, LIMITS[kind])
    if used > LIMITS[kind]:
        raise BudgetExceeded(f"{kind} budget exhausted")
    state["used"][kind] = used


def local_precheck(value):
    require(isinstance(value, dict) and isinstance(value.get("status"), str)
            and value["status"] in {"completed", "skipped"},
            "Local precheck result or explicit environment skip required")
    result = {"status": value["status"], "reason": text(value.get("reason"), 2000)}
    if result["status"] == "completed":
        commands = value.get("commands")
        require(isinstance(commands, list) and 1 <= len(commands) <= 5,
                "Completed local precheck must record commands")
        result.update(
            commands=[text(command, 1000) for command in commands],
            os=text(value.get("os"), 100),
            observed_output=text(value.get("observed_output"), 8000),
        )
    return result


def expired(state, now=None):
    now = now or datetime.now(timezone.utc)
    return (now - datetime.fromisoformat(state["created_at"])).total_seconds() >= 12 * 60 * 60


def validate_proposal(state, revision, proposal):
    require(isinstance(proposal, dict) and type(proposal.get("version")) is int and proposal["version"] == 1
            and proposal.get("issue_url") == state["issue_url"]
            and proposal.get("state_revision") == revision, "Stale or foreign proposal")
    require(isinstance(proposal.get("action"), str)
            and proposal["action"] in {"select", "reproduce", "repair", "finish"}, "Unknown proposal action")
    text(proposal.get("explanation"))
    evidence = proposal.get("evidence_read")
    require(isinstance(evidence, list) and 1 <= len(evidence) <= 30, "Evidence inspected must be recorded")
    for item in evidence:
        text(item, 1000)
    if "analysis" in proposal:
        require(proposal["action"] == "finish" and proposal.get("outcome") != "verified",
                "Analysis is only for terminal findings without a verified fix")
        terminal_analysis(proposal["analysis"])


def terminal_analysis(value):
    require(isinstance(value, dict) and set(value) == {"why_no_fix", "attempted", "next_action"},
            "Terminal analysis requires why_no_fix, attempted and next_action")
    def short_line(value, maximum):
        text(value, maximum)
        require(not any(character in value for character in "\r\n"), "Analysis must use short single-line fields")
        return value.strip()
    attempted = value["attempted"]
    require(isinstance(attempted, list) and 1 <= len(attempted) <= 3, "Analysis requires 1-3 attempted steps")
    return {
        "why_no_fix": short_line(value["why_no_fix"], 600),
        "attempted": [short_line(step, 240) for step in attempted],
        "next_action": short_line(value["next_action"], 300),
    }
