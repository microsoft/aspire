import io
import pathlib
import stat
import tempfile
import unittest
from unittest import mock
import zipfile

import analyze_ci_failure


class ExtractTestResultsTests(unittest.TestCase):
    def test_extracts_safe_trx_and_rejects_unsafe_entries(self):
        with tempfile.TemporaryDirectory() as temp_directory:
            root = pathlib.Path(temp_directory)
            archive_path = root / "results.zip"
            destination = root / "results"
            evidence_gaps = root / "evidence-gaps.txt"

            with zipfile.ZipFile(archive_path, "w") as archive:
                archive.writestr("safe/results.trx", "safe")
                archive.writestr("../escaped.trx", "escape")
                archive.writestr("/absolute.trx", "escape")
                symlink = zipfile.ZipInfo("linked.trx")
                symlink.create_system = 3
                symlink.external_attr = (stat.S_IFLNK | 0o777) << 16
                archive.writestr(symlink, "safe/results.trx")

            analyze_ci_failure.extract_test_results(archive_path, destination, evidence_gaps)

            self.assertEqual("safe", (destination / "safe" / "results.trx").read_text())
            self.assertFalse((root / "escaped.trx").exists())
            self.assertFalse((destination / "absolute.trx").exists())
            self.assertFalse((destination / "linked.trx").exists())
            gaps = evidence_gaps.read_text()
            self.assertIn("../escaped.trx", gaps)
            self.assertIn("/absolute.trx", gaps)
            self.assertIn("linked.trx", gaps)

    def test_rejects_windows_path_separator(self):
        entry = zipfile.ZipInfo("safe.trx")
        entry.filename = "windows\\escaped.trx"

        self.assertTrue(analyze_ci_failure._is_unsafe_entry(entry))

    def test_reports_file_count_and_stops_at_aggregate_limit(self):
        with tempfile.TemporaryDirectory() as temp_directory:
            root = pathlib.Path(temp_directory)
            archive_path = root / "results.zip"
            destination = root / "results"
            evidence_gaps = root / "evidence-gaps.txt"

            with zipfile.ZipFile(archive_path, "w") as archive:
                archive.writestr("1.trx", b"1234")
                archive.writestr("2.trx", b"5678")
                archive.writestr("3.trx", b"9012")
                archive.writestr("4.trx", b"not-processed")

            analyze_ci_failure.extract_test_results(
                archive_path,
                destination,
                evidence_gaps,
                max_files=3,
                max_file_bytes=10,
                max_total_bytes=6,
            )

            self.assertEqual(b"1234", (destination / "1.trx").read_bytes())
            self.assertFalse((destination / "2.trx").exists())
            self.assertFalse((destination / "3.trx").exists())
            self.assertFalse((destination / "4.trx").exists())
            gaps = evidence_gaps.read_text()
            self.assertIn("processing only the first 3", gaps)
            self.assertIn("6 bytes aggregate limit", gaps)

    def test_skips_entry_over_per_file_limit(self):
        with tempfile.TemporaryDirectory() as temp_directory:
            root = pathlib.Path(temp_directory)
            archive_path = root / "results.zip"
            destination = root / "results"
            evidence_gaps = root / "evidence-gaps.txt"

            with zipfile.ZipFile(archive_path, "w") as archive:
                archive.writestr("oversized.trx", b"123456")

            analyze_ci_failure.extract_test_results(
                archive_path,
                destination,
                evidence_gaps,
                max_file_bytes=5,
            )

            self.assertFalse((destination / "oversized.trx").exists())
            self.assertIn("larger than 5 bytes", evidence_gaps.read_text())

    def test_removes_partial_file_when_actual_data_exceeds_declared_size(self):
        class FakeEntry:
            filename = "mismatch.trx"
            external_attr = 0
            file_size = 1

            @staticmethod
            def is_dir():
                return False

        class FakeArchive:
            def __enter__(self):
                return self

            def __exit__(self, *_):
                return False

            @staticmethod
            def infolist():
                return [FakeEntry()]

            @staticmethod
            def open(_):
                return io.BytesIO(b"123456")

        with tempfile.TemporaryDirectory() as temp_directory:
            root = pathlib.Path(temp_directory)
            destination = root / "results"
            evidence_gaps = root / "evidence-gaps.txt"

            with mock.patch.object(analyze_ci_failure.zipfile, "ZipFile", return_value=FakeArchive()):
                analyze_ci_failure.extract_test_results(
                    root / "results.zip",
                    destination,
                    evidence_gaps,
                    max_file_bytes=5,
                    max_total_bytes=10,
                )

            self.assertFalse((destination / "mismatch.trx").exists())
            self.assertIn("extraction size limit", evidence_gaps.read_text())


if __name__ == "__main__":
    unittest.main()
