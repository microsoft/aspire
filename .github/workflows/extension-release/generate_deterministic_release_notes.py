#!/usr/bin/env python3

from __future__ import annotations

import pathlib
import re
import sys


_CONVENTIONAL_PREFIX = re.compile(r"^[A-Za-z]+(\([^)]*\))?!?:\s*")
_PR_SUFFIX = re.compile(r"\s*\(#[0-9]+\)$")
_CONTROL_CHARS = re.compile(r"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]")
_MARKDOWN_IMAGES = re.compile(r"!\[[^\]]*\]\([^)]+\)")
_HTML_TAGS = re.compile(r"<[^>\n]*>")


def main() -> int:
    if len(sys.argv) != 3:
        raise SystemExit("Usage: generate_deterministic_release_notes.py <commits.txt> <release_notes.md>")

    commits_path = pathlib.Path(sys.argv[1])
    release_notes_path = pathlib.Path(sys.argv[2])

    accepted_messages: list[str] = []
    # pathlib.Path.read_text() enables universal-newline translation, which would turn
    # embedded '\r' controls into '\n' before we split and accidentally create extra
    # entries. Decode the raw bytes ourselves, then split only on LF so controls like
    # '\r' and '\v' stay attached to the same subject and are removed by sanitize_subject.
    for raw_line in commits_path.read_bytes().decode("utf-8").split("\n"):
        if not raw_line:
            continue

        message = sanitize_subject(extract_subject(raw_line))
        if is_filtered_noise(message):
            continue

        accepted_messages.append(message)

    if accepted_messages:
        rendered = "\n".join(
            [
                "### Changes (auto-generated from commits)",
                *[f"- {message}" for message in accepted_messages],
                "",
            ]
        )
    else:
        rendered = "### Maintenance\n- No user-facing extension changes were detected.\n"

    release_notes_path.write_text(rendered, encoding="utf-8")
    return 0


def extract_subject(raw_line: str) -> str:
    # `git log --format='%h%x09%s'` emits deterministic fallback input as:
    #   1a2b3c4<TAB>feat(tree): Show Azure resources in explorer (#12345)
    # Mirror `cut -f2-` from the workflow: if a delimiter is present, everything after the
    # first tab is the subject; otherwise keep the whole line so unexpected-but-readable input
    # still surfaces in the fallback instead of being silently dropped.
    _, delimiter, subject = raw_line.partition("\t")
    return subject if delimiter else raw_line


def sanitize_subject(subject: str) -> str:
    # Remove embedded controls before pattern-based cleanup so CRLF input and pasted
    # control characters cannot prevent conventional-commit or "(#123)" suffix stripping.
    subject = subject.replace("\r", "")
    subject = _CONTROL_CHARS.sub("", subject)
    subject = _CONVENTIONAL_PREFIX.sub("", subject)
    subject = _PR_SUFFIX.sub("", subject)
    subject = _MARKDOWN_IMAGES.sub("", subject)
    subject = _HTML_TAGS.sub("", subject)
    return subject


def is_filtered_noise(subject: str) -> bool:
    return subject == "" or subject.startswith(
        (
            "Merge ",
            "merge ",
            "Release ",
            "release ",
            "Bump ",
            "bump ",
            "Update package-lock",
            "Update yarn.lock",
        )
    )


if __name__ == "__main__":
    raise SystemExit(main())
