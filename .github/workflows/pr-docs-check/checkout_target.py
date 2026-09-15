"""Check out the resolved aspire.dev target before the documentation agent runs."""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import subprocess
import sys
from collections.abc import Callable, Sequence


RUNTIME_PATHS = (".agents", ".github", "AGENTS.md", ".mcp.json")
LOCAL_EXCLUDES = (
    "/.agents/",
    "/.github/",
    "/AGENTS.md",
    "/.mcp.json",
    "/.pr-docs-check/",
    "/_repos/",
)
EXCLUDE_MARKER = "# gh-aw trusted runtime configuration"


class CheckoutError(RuntimeError):
    """Raised when the target workspace cannot be prepared safely."""


def _git(
    workspace: Path,
    *arguments: str,
    input_bytes: bytes | None = None,
) -> subprocess.CompletedProcess[bytes]:
    return subprocess.run(
        ["git", "-C", str(workspace), *arguments],
        input=input_bytes,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )


def _git_output(workspace: Path, *arguments: str) -> str:
    result = _git(workspace, *arguments)
    if result.returncode != 0:
        detail = result.stderr.decode(errors="replace").strip()
        raise CheckoutError(detail or f"git {' '.join(arguments)} failed")

    return result.stdout.decode().strip()


def _verify_remote_ref(workspace: Path, remote_ref: str) -> bool:
    return _git(
        workspace,
        "rev-parse",
        "--verify",
        "--quiet",
        f"{remote_ref}^{{commit}}",
    ).returncode == 0


def _ensure_remote_ref(
    workspace: Path,
    effective_target: str,
    repository_url: str,
) -> str:
    # The generated PR checkout can populate origin/* from the Aspire source
    # repository. Fetch the aspire.dev target into an isolated namespace so a
    # source branch with the same name cannot be mistaken for the docs target.
    # gh-aw v0.85.4 later resolves the patch base through origin/<base>, so
    # overwrite that remote-tracking ref from the same explicit aspire.dev fetch.
    remote_ref = f"refs/remotes/gh-aw-target/{effective_target}"
    patch_base_ref = f"refs/remotes/origin/{effective_target}"

    depth_arguments: list[str] = []
    if _git_output(workspace, "rev-parse", "--is-shallow-repository") == "true":
        depth_arguments.append("--depth=1")

    fetch = _git(
        workspace,
        "-c",
        "credential.helper=",
        "-c",
        "credential.helper=!gh auth git-credential",
        "fetch",
        "--no-tags",
        *depth_arguments,
        repository_url,
        f"+refs/heads/{effective_target}:{remote_ref}",
        f"+refs/heads/{effective_target}:{patch_base_ref}",
    )
    if fetch.returncode != 0:
        detail = fetch.stderr.decode(errors="replace").strip()
        raise CheckoutError(
            f"Resolved target branch '{effective_target}' could not be fetched "
            f"from microsoft/aspire.dev: {detail}"
        )

    if not _verify_remote_ref(workspace, remote_ref):
        raise CheckoutError(
            f"Resolved target branch '{effective_target}' is missing from the "
            "local checkout after fetch."
        )

    if not _verify_remote_ref(workspace, patch_base_ref):
        raise CheckoutError(
            f"Resolved target branch '{effective_target}' is missing from the "
            "patch base after fetch."
        )

    target_commit = _git_output(workspace, "rev-parse", f"{remote_ref}^{{commit}}")
    patch_base_commit = _git_output(
        workspace,
        "rev-parse",
        f"{patch_base_ref}^{{commit}}",
    )
    if patch_base_commit != target_commit:
        raise CheckoutError(
            f"Resolved target branch '{effective_target}' does not match its "
            "patch base after fetch."
        )

    return remote_ref


def _check_out_work_branch(
    workspace: Path,
    remote_ref: str,
    docs_work_branch: str,
) -> None:
    branch_check = _git(
        workspace,
        "check-ref-format",
        "--branch",
        docs_work_branch,
    )
    if branch_check.returncode != 0:
        raise CheckoutError(f"Invalid documentation work branch '{docs_work_branch}'.")

    target_commit = _git_output(workspace, "rev-parse", f"{remote_ref}^{{commit}}")
    checkout = _git(
        workspace,
        "checkout",
        "--force",
        "-B",
        docs_work_branch,
        remote_ref,
    )
    if checkout.returncode != 0:
        detail = checkout.stderr.decode(errors="replace").strip()
        raise CheckoutError(
            f"Could not create '{docs_work_branch}' from '{remote_ref}': {detail}"
        )

    actual_branch = _git_output(workspace, "branch", "--show-current")
    actual_commit = _git_output(workspace, "rev-parse", "HEAD^{commit}")
    if actual_branch != docs_work_branch or actual_commit != target_commit:
        raise CheckoutError(
            f"Expected '{docs_work_branch}' at {target_commit}, got "
            f"'{actual_branch}' at {actual_commit}."
        )


def _protect_runtime_configuration(workspace: Path) -> None:
    exclude_path = Path(_git_output(workspace, "rev-parse", "--git-path", "info/exclude"))
    if not exclude_path.is_absolute():
        exclude_path = workspace / exclude_path
    exclude_path.parent.mkdir(parents=True, exist_ok=True)

    existing = exclude_path.read_text(encoding="utf-8") if exclude_path.exists() else ""
    if EXCLUDE_MARKER not in existing:
        with exclude_path.open("a", encoding="utf-8", newline="\n") as exclude_file:
            if existing and not existing.endswith("\n"):
                exclude_file.write("\n")
            exclude_file.write(f"{EXCLUDE_MARKER}\n")
            exclude_file.writelines(f"{path}\n" for path in LOCAL_EXCLUDES)

    # The trusted snapshot intentionally replaces tracked aspire.dev configuration
    # while the agent runs. Skip-worktree hides those runtime-only replacements,
    # and the local excludes cover trusted files that don't exist on the docs branch.
    tracked = _git(workspace, "ls-files", "-z", "--", *RUNTIME_PATHS)
    if tracked.returncode != 0:
        detail = tracked.stderr.decode(errors="replace").strip()
        raise CheckoutError(detail or "Could not enumerate runtime configuration.")
    if tracked.stdout:
        update = _git(
            workspace,
            "update-index",
            "--skip-worktree",
            "-z",
            "--stdin",
            input_bytes=tracked.stdout,
        )
        if update.returncode != 0:
            detail = update.stderr.decode(errors="replace").strip()
            raise CheckoutError(detail or "Could not protect runtime configuration.")


def _restore_trusted_configuration(workspace: Path) -> None:
    runner_temp = os.environ.get("RUNNER_TEMP")
    if not runner_temp:
        raise CheckoutError("RUNNER_TEMP is required to restore trusted configuration.")

    actions_dir = Path(runner_temp) / "gh-aw" / "actions"
    # These script names and environment variables mirror gh-aw's generated
    # restore steps. Reverify this contract whenever the pinned compiler changes.
    scripts = (
        (
            actions_dir / "restore_base_github_folders.sh",
            {
                "GH_AW_AGENT_FOLDERS": ".agents .github",
                "GH_AW_AGENT_FILES": "AGENTS.md",
            },
        ),
        (
            actions_dir / "restore_inline_sub_agents.sh",
            {
                "GH_AW_SUB_AGENT_DIR": ".github/agents",
                "GH_AW_SUB_AGENT_EXT": ".agent.md",
            },
        ),
        (
            actions_dir / "restore_inline_skills.sh",
            {"GH_AW_SKILL_DIR": ".github/skills"},
        ),
    )

    for script, overrides in scripts:
        if not script.is_file():
            raise CheckoutError(f"Trusted restore script is missing: {script}")
        result = subprocess.run(
            ["bash", str(script)],
            cwd=workspace,
            env={**os.environ, **overrides},
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
        if result.returncode != 0:
            detail = result.stderr.decode(errors="replace").strip()
            raise CheckoutError(f"Trusted restore script failed ({script.name}): {detail}")


def _verify_runtime_configuration_is_patch_clean(workspace: Path) -> None:
    status = _git(
        workspace,
        "status",
        "--porcelain=v1",
        "--untracked-files=all",
        "--",
        *RUNTIME_PATHS,
        ".pr-docs-check",
        "_repos",
    )
    if status.returncode != 0:
        detail = status.stderr.decode(errors="replace").strip()
        raise CheckoutError(detail or "Could not verify runtime configuration status.")
    if status.stdout:
        dirty_paths = status.stdout.decode(errors="replace").strip()
        raise CheckoutError(
            "Trusted runtime configuration is visible to Git and could leak into "
            f"the documentation patch:\n{dirty_paths}"
        )


def prepare_workspace(
    workspace: Path,
    effective_target: str,
    docs_work_branch: str,
    repository_url: str,
    restore_configuration: Callable[[Path], None] = _restore_trusted_configuration,
) -> None:
    if _git(workspace, "rev-parse", "--is-inside-work-tree").returncode != 0:
        raise CheckoutError(f"Workspace is not a Git work tree: {workspace}")

    remote_ref = _ensure_remote_ref(workspace, effective_target, repository_url)
    _check_out_work_branch(workspace, remote_ref, docs_work_branch)
    _protect_runtime_configuration(workspace)
    restore_configuration(workspace)
    _verify_runtime_configuration_is_patch_clean(workspace)


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("effective_target")
    parser.add_argument("docs_work_branch")
    args = parser.parse_args(argv)

    workspace = Path(os.environ.get("GITHUB_WORKSPACE", os.getcwd())).resolve()
    server_url = os.environ.get("GITHUB_SERVER_URL", "https://github.com")
    repository_url = os.environ.get(
        "ASPIRE_DEV_REPOSITORY_URL",
        f"{server_url}/microsoft/aspire.dev.git",
    )

    try:
        prepare_workspace(
            workspace,
            args.effective_target,
            args.docs_work_branch,
            repository_url,
        )
    except CheckoutError as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1

    commit = _git_output(workspace, "rev-parse", "--short=12", "HEAD")
    print(f"Checked out   : {args.docs_work_branch} ({commit})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
