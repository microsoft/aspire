#!/usr/bin/env python3
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

"""Forward SDK execution and retain bounded failure output outside temporary HOME."""

import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile

MAX_OUTPUT = 1024 * 1024


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
        except OSError as error:
            # A diagnostic write must not replace the SDK's original failure.
            print(f"Could not retain SDK failure diagnostics: {error}", file=sys.stderr)
    return status if status >= 0 else 128 - status


if __name__ == "__main__":
    sys.exit(main())
