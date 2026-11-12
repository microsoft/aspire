#!/usr/bin/env python3
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

"""Preserve the exact source run across the human-controlled release boundary."""

import argparse
import json
from pathlib import Path
import re
import sys

REPOSITORY = "https://dev.azure.com/dnceng/internal/_git/microsoft-aspire"


def create(release_run_id, release_commit, source_build_id, source_commit, version):
    if any(type(value) is not int or value <= 0 for value in (release_run_id, source_build_id)):
        raise ValueError("Exact positive release and source run IDs are required")
    if any(not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{40}", value)
           for value in (release_commit, source_commit)):
        raise ValueError("Exact release-pipeline and original source commits are required")
    if not isinstance(version, str) or not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ValueError("Homebrew handoff requires an explicit stable X.Y.Z version")
    return {
        "schema": 1, "release_run_id": release_run_id, "release_commit": release_commit,
        "source_build_id": source_build_id, "source_commit": source_commit, "version": version,
    }


def resolve(handoff, release, selected_release_run_id):
    expected = create(
        handoff["release_run_id"], handoff["release_commit"], handoff["source_build_id"],
        handoff["source_commit"], handoff["version"],
    )
    if type(handoff.get("schema")) is not int or handoff != expected:
        raise ValueError("Unsupported or ambiguous Homebrew handoff schema")
    if (release["id"] != selected_release_run_id or handoff["release_run_id"] != selected_release_run_id
            or release["sourceVersion"] != handoff["release_commit"]
            or release["definition"]["id"] != 1600 or release["project"]["name"] != "internal"
            or release["repository"]["url"] != REPOSITORY):
        raise ValueError("Handoff does not match the selected original release-pipeline run")
    if release["status"] != "completed" or release["result"] not in ("succeeded", "partiallySucceeded"):
        raise ValueError("Original release run must have completed successfully")
    return expected


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    write = commands.add_parser("create")
    write.add_argument("--release-run-id", type=int, required=True)
    write.add_argument("--release-commit", required=True)
    write.add_argument("--source-build-id", type=int, required=True)
    write.add_argument("--source-commit", required=True)
    write.add_argument("--version", required=True)
    write.add_argument("--output", type=Path, required=True)
    read = commands.add_parser("resolve")
    read.add_argument("--release-run-id", type=int, required=True)
    read.add_argument("--handoff", type=Path, required=True)
    read.add_argument("--release-metadata", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.command == "create":
            value = create(
                args.release_run_id, args.release_commit, args.source_build_id, args.source_commit, args.version
            )
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(json.dumps(value, indent=2) + "\n")
        else:
            value = resolve(
                json.loads(args.handoff.read_text()), json.loads(args.release_metadata.read_text())["build"],
                args.release_run_id,
            )
            for name, key in (
                ("HomebrewSourceBuildId", "source_build_id"), ("HomebrewSourceCommit", "source_commit"),
                ("HomebrewReleaseVersion", "version"),
            ):
                print(f"##vso[task.setvariable variable={name}]{value[key]}")
        print(json.dumps(value))
        return 0
    except (ValueError, KeyError, TypeError, OSError) as error:
        print(f"Homebrew handoff failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
