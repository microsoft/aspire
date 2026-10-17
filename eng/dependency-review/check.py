#!/usr/bin/env python3
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

"""Offline constraint checks, not a dependency compatibility certification."""

import argparse
import json
from pathlib import Path
import re
import sys
from urllib.parse import unquote, urlsplit
import xml.etree.ElementTree as ET


def approved_source(value):
    url = urlsplit(value)
    # Azure URLs such as /dnceng/../other/ and /public/%2e%2e/private/
    # leave the approved namespace after decoding/normalization. Reject rather
    # than normalize traversal, backslashes, control bytes, or nested escapes.
    path = unquote(url.path, errors="strict")
    if (
        "\\" in path
        or "%" in path
        or any(ord(character) < 32 for character in path)
        or any(segment in (".", "..") for segment in path.split("/"))
    ):
        return False
    return (
        url.scheme == "https"
        and url.username is None
        and url.password is None
        and url.port in (None, 443)
        and (
            (url.hostname == "pkgs.dev.azure.com" and path.startswith("/dnceng/"))
            or (url.hostname == "dnceng.pkgs.visualstudio.com" and path.startswith("/public/"))
        )
    )


def check_packages(text, policy):
    root = ET.fromstring(text)
    failures = []
    for name, rule in policy["properties"].items():
        values = [element.text or "" for element in root.iter(name)]
        if len(values) != 1 or not re.fullmatch(rf'{rule["major"]}\.\d+\.\d+(?:[-+].+)?', values[0]):
            failures.append(f"{name}: expected one literal major {rule['major']} version; {rule['reason']}")
    for name, rule in policy["packages"].items():
        entries = [element for element in root.iter("PackageVersion") if element.get("Include") == name]
        if len(entries) != 1 or entries[0].get("Version") != rule["version"]:
            failures.append(f"{name}: protected version {rule['version']}; {rule['reason']}")
    return failures


def check_nuget(text):
    root = ET.fromstring(text)
    failures = []
    # Audit endpoints are deliberately separate from package download sources.
    for element in root.findall("./packageSources/add"):
        value = element.get("value", "")
        if not approved_source(value):
            failures.append(f"NuGet package source is not approved: {value}")
    return failures


def check_yarn(text):
    failures = []
    resolved_count = 0
    # Yarn classic entries contain e.g. resolved "https://.../pkg-1.2.3.tgz#hash".
    # Selectors (pkg@^1.2.0) are NOT the resolved versions used for advisory review.
    # This scans present download URLs only; frozen Yarn restore owns lockfile
    # completeness/coherence, including missing resolutions.
    for number, line in enumerate(text.splitlines(), 1):
        if line.lstrip().startswith("resolved "):
            resolved_count += 1
            match = re.fullmatch(r'\s*resolved "([^"]+)"\s*', line)
            if match is None or not approved_source(match[1]):
                failures.append(f"yarn.lock:{number}: malformed or unapproved resolved source")
    if resolved_count == 0:
        failures.append("extension/yarn.lock: no resolved download entries; source inspection is incomplete")
    return failures


def check_repository(root, policy):
    failures = check_packages((root / "Directory.Packages.props").read_text(), policy)
    failures += check_nuget((root / "NuGet.config").read_text())
    failures += check_yarn((root / "extension/yarn.lock").read_text())
    return failures


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    args = parser.parse_args()
    try:
        policy = json.loads((Path(__file__).parent / "constraints.json").read_text())
        failures = check_repository(args.root, policy)
    except (OSError, ET.ParseError, ValueError) as error:
        print(f"Dependency constraint check could not complete: {error}", file=sys.stderr)
        return 2
    for failure in failures:
        print(f"ERROR: {failure}", file=sys.stderr)
    if failures:
        return 1
    print("Dependency constraints checked: protected pins, coupled majors, package download sources.")
    print("Not assessed: compatibility, advisories, lockfile coherence, workflow generation, action policy, consumer execution.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
