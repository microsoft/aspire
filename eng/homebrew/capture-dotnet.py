#!/usr/bin/env python3
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

"""Forward SDK execution and retain bounded failure output outside temporary HOME."""

import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import tempfile
from urllib.parse import urlsplit, urlunsplit

MAX_OUTPUT = 1024 * 1024


def source_identity(value):
    source = urlsplit(value)
    if source.scheme not in ("http", "https") or not source.hostname:
        return "[non-HTTP source omitted]"
    # Keep the feed endpoint, never userinfo, queries (including unknown signed
    # parameters), fragments, or local configuration/credential values.
    host = source.hostname
    if ":" in host:
        host = f"[{host}]"
    if source.port:
        host += f":{source.port}"
    return redact(urlunsplit((source.scheme, host, source.path, "", "")))


def assets_summary():
    # Released starter layout: SmokeApp/{AppHost,ApiService,...}/obj/project.assets.json.
    # The SDK runs in AppHost; inspect only this project and immediate siblings,
    # not HOME/caches, recursive dependency trees, or arbitrary NuGet.Config files.
    project = Path.cwd()
    candidates = sorted(set([project / "obj" / "project.assets.json",
                             *project.parent.glob("*/obj/project.assets.json")]))
    result = {"projects": []}
    for path in candidates[:16]:
        if not path.exists():
            continue
        entry = {"project": path.parent.parent.name}
        try:
            with os.fdopen(os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK), "rb") as stream:
                info = os.fstat(stream.fileno())
                if not stat.S_ISREG(info.st_mode) or info.st_size > 8 * MAX_OUTPUT:
                    raise ValueError("Assets input is not a regular file of at most 8 MiB")
                data = json.loads(stream.read(8 * MAX_OUTPUT + 1))
            sources = data.get("project", {}).get("restore", {}).get("sources", {})
            logs = data.get("logs", [])
            if not isinstance(sources, dict) or not isinstance(logs, list):
                raise ValueError("Unexpected NuGet assets sources/logs shape")
            identities = {source_identity(value) for value in list(sources)[:16]}
            entry["sources"] = sorted({value[:1024] for value in identities})
            if any(len(value) > 1024 for value in identities):
                entry["truncated"] = True
            entry["logs"] = []
            for item in logs[:32]:
                if not isinstance(item, dict):
                    raise ValueError("Unexpected NuGet assets log entry")
                message = {}
                for key in ("code", "level", "message"):
                    value = item.get(key, "")
                    if not isinstance(value, str):
                        raise ValueError("Unexpected NuGet assets log field")
                    # Preserve NuGet's message, e.g. NU1301, but strip all URL
                    # query/fragment data, not just known credential parameter names.
                    value = re.sub(r"(https?://[^\s?#]+)[?#][^\s]*", r"\1", redact(value))
                    if len(value) > 2048:
                        entry["truncated"] = True
                    message[key] = value[:2048]
                entry["logs"].append(message)
            if len(sources) > 16 or len(logs) > 32:
                entry["truncated"] = True
        except (OSError, ValueError, TypeError, AttributeError, RecursionError) as error:
            entry = {"project": entry["project"], "diagnostic_error": redact(str(error))[:2048]}
        result["projects"].append(entry)
        if len(json.dumps(result, ensure_ascii=False).encode("utf-8")) > MAX_OUTPUT - 1024:
            result["projects"].pop()
            result["truncated"] = True
            break
    if len(candidates) > 16:
        result["truncated"] = True
    return result


def redact(text):
    for key, value in sorted(os.environ.items(), key=lambda item: len(item[1]), reverse=True):
        if value and re.search(r"TOKEN|PASSWORD|SECRET|(?:^|_)PAT$|(?:^|_)KEY$", key, re.IGNORECASE):
            text = text.replace(value, "[REDACTED]")
    # SDK errors can contain credential-bearing feed URLs or signed query strings.
    text = re.sub(r"(https?://)[^/\s@]+@", r"\1[REDACTED]@", text)
    return re.sub(
        r"(?i)([?&](?:token|access_token|password|sig|signature|api_key)=)[^&\s]+",
        r"\1[REDACTED]", text,
    )


def main():
    executable, directory, *arguments = sys.argv[1:]
    tail = bytearray()
    truncated = False
    with subprocess.Popen([executable, *arguments], stdout=subprocess.PIPE, stderr=subprocess.STDOUT) as process:
        while chunk := process.stdout.read1(65536):
            # Preserve the original stream for the validator/CLI while retaining
            # only its tail: restore errors normally follow package progress.
            sys.stdout.buffer.write(chunk)
            sys.stdout.buffer.flush()
            tail.extend(chunk)
            if len(tail) > MAX_OUTPUT:
                del tail[:-MAX_OUTPUT]
                truncated = True
        status = process.wait()
    if status:
        try:
            directory = Path(directory)
            directory.mkdir(parents=True, exist_ok=True)
            operation = re.sub(r"[^a-zA-Z0-9_-]", "_", arguments[0] if arguments else "sdk")
            retained = redact(tail.decode("utf-8", errors="replace")).encode("utf-8")
            if len(retained) > MAX_OUTPUT:
                retained = retained[-MAX_OUTPUT:]
                truncated = True
            with tempfile.NamedTemporaryFile(
                mode="w", encoding="utf-8", prefix=f"dotnet-{operation}-",
                suffix=".log", dir=directory, delete=False,
            ) as log:
                log.write(f"dotnet {operation} failed (exit {status}).\n")
                if truncated:
                    log.write(f"Output truncated to the last {MAX_OUTPUT} bytes.\n")
                log.write(retained.decode("utf-8", errors="replace"))
            print(f"SDK failure diagnostics: {log.name}", file=sys.stderr)
            if operation == "restore":
                with tempfile.NamedTemporaryFile(
                    mode="w", encoding="utf-8", prefix="nuget-restore-",
                    suffix=".json", dir=directory, delete=False,
                ) as summary:
                    json.dump(assets_summary(), summary, ensure_ascii=False)
                    summary.write("\n")
                print(f"NuGet failure details: {summary.name}", file=sys.stderr)
        except OSError as error:
            # A diagnostic write must not replace the SDK's original failure.
            print(f"Could not retain SDK failure diagnostics: {error}", file=sys.stderr)
    return status if status >= 0 else 128 - status


if __name__ == "__main__":
    sys.exit(main())
