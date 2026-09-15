#!/usr/bin/env python3

import pathlib
import stat
import sys
import zipfile

MAX_FILES = 200
MAX_FILE_BYTES = 50 * 1024 * 1024
MAX_TOTAL_BYTES = 500 * 1024 * 1024
CHUNK_BYTES = 1024 * 1024


class ExtractionLimitExceeded(Exception):
    pass


def _is_unsafe_entry(entry):
    relative_path = pathlib.PurePosixPath(entry.filename)
    unix_mode = entry.external_attr >> 16
    return (
        relative_path.is_absolute()
        or ".." in relative_path.parts
        or "\\" in entry.filename
        or stat.S_ISLNK(unix_mode)
    )


def _copy_bounded(source, output, max_file_bytes, max_total_bytes):
    written = 0
    while chunk := source.read(min(CHUNK_BYTES, max_file_bytes + 1)):
        written += len(chunk)
        if written > max_file_bytes or written > max_total_bytes:
            raise ExtractionLimitExceeded
        output.write(chunk)
    return written


def _format_size(byte_count):
    megabyte = 1024 * 1024
    if byte_count % megabyte == 0:
        return f"{byte_count // megabyte} MB"
    return f"{byte_count} bytes"


def extract_test_results(
    archive_path,
    destination_path,
    evidence_gaps_path,
    max_files=MAX_FILES,
    max_file_bytes=MAX_FILE_BYTES,
    max_total_bytes=MAX_TOTAL_BYTES,
):
    destination = pathlib.Path(destination_path).resolve()
    destination.mkdir(parents=True, exist_ok=True)
    total_bytes = 0

    with zipfile.ZipFile(archive_path) as archive:
        trx_entries = sorted(
            (
                entry
                for entry in archive.infolist()
                if not entry.is_dir() and entry.filename.lower().endswith(".trx")
            ),
            key=lambda entry: entry.filename,
        )

        with open(evidence_gaps_path, "a", encoding="utf-8") as evidence_gaps:
            if len(trx_entries) > max_files:
                evidence_gaps.write(
                    f"Test results artifact contained {len(trx_entries)} TRX files; "
                    f"processing only the first {max_files}\n"
                )

            for entry in trx_entries[:max_files]:
                relative_path = pathlib.PurePosixPath(entry.filename)
                if _is_unsafe_entry(entry):
                    evidence_gaps.write(f"Skipped unsafe test result path: {entry.filename}\n")
                    continue
                if entry.file_size > max_file_bytes:
                    evidence_gaps.write(
                        f"Skipped test result larger than {_format_size(max_file_bytes)}: {relative_path.name}\n"
                    )
                    continue
                if total_bytes + entry.file_size > max_total_bytes:
                    evidence_gaps.write(
                        "Stopped extracting test results after reaching the "
                        f"{_format_size(max_total_bytes)} aggregate limit\n"
                    )
                    break

                target = (destination / pathlib.Path(*relative_path.parts)).resolve()
                if destination not in target.parents:
                    evidence_gaps.write(f"Skipped unsafe test result path: {entry.filename}\n")
                    continue

                target.parent.mkdir(parents=True, exist_ok=True)
                try:
                    with archive.open(entry) as source, open(target, "wb") as output:
                        written = _copy_bounded(
                            source,
                            output,
                            max_file_bytes,
                            max_total_bytes - total_bytes,
                        )
                except ExtractionLimitExceeded:
                    target.unlink(missing_ok=True)
                    evidence_gaps.write(
                        "Stopped extracting test results after reaching an extraction size limit\n"
                    )
                    break
                total_bytes += written


def main():
    if len(sys.argv) != 4:
        raise SystemExit(
            "Usage: extract_test_results.py <archive.zip> <destination> <evidence-gaps.txt>"
        )

    extract_test_results(*sys.argv[1:])


if __name__ == "__main__":
    main()
