"""Tests for compute_signals.py.

Run from the repo root:

    python3 -m unittest discover -s .github/workflows/pr-docs-check -v

Or:

    python3 -m unittest .github.workflows.pr-docs-check.test_compute_signals -v
"""

from __future__ import annotations

import os
import sys
import unittest

# Allow `import compute_signals` when running this file directly.
_THIS_DIR = os.path.dirname(os.path.abspath(__file__))
if _THIS_DIR not in sys.path:
    sys.path.insert(0, _THIS_DIR)

import compute_signals  # noqa: E402


def _pr(body: str = "", labels: list[str] | None = None, number: int = 1) -> dict:
    return {
        "number": number,
        "body": body,
        "labels": [{"name": lab} for lab in (labels or [])],
    }


def _file(filename: str, status: str = "modified", patch: str | None = None) -> dict:
    """Build a single 'files' entry. `patch` of None means GitHub omitted it."""
    out: dict = {"filename": filename, "status": status}
    if patch is not None:
        out["patch"] = patch
    return out


class CatalogScenarioTests(unittest.TestCase):
    """End-to-end scenarios covering each gating signal category.

    These mirror the 11 scenarios used to validate the broadened catalog
    when it was first introduced. They are kept here so the script can
    be modified with confidence on its own.
    """

    def test_new_cli_command_with_option_and_resx(self) -> None:
        pr = _pr(body="Adds aspire logs --search flag.")
        files = [
            _file(
                "src/Aspire.Cli/Commands/LogsCommand.cs",
                status="added",
                patch=(
                    "@@ +0,0 @@\n"
                    '+    private static readonly Option<string?> s_searchOption '
                    '= new("--search");\n'
                ),
            ),
            _file(
                "src/Aspire.Cli/Resources/LogsCommandStrings.resx",
                status="modified",
                patch=(
                    "@@ +0,0 @@\n"
                    '+  <data name="SearchOptionDescription" xml:space="preserve">\n'
                ),
            ),
        ]
        result = compute_signals.compute_signals(pr, files)
        self.assertEqual(result["recommendation"], "docs_required")
        self.assertTrue(result["signals"]["cli_command_added"])
        self.assertTrue(result["signals"]["cli_command_file_changed"])
        self.assertTrue(result["signals"]["cli_resource_strings_changed"])
        self.assertTrue(result["signals"]["cli_option_added"])
        self.assertTrue(result["signals"]["pr_body_has_cli_flag_mention"])

    def test_container_image_tag_bump(self) -> None:
        pr = _pr(body="Bumps Redis image to 8.0.")
        files = [
            _file(
                "src/Aspire.Hosting.Redis/RedisContainerImageTags.cs",
                patch=(
                    "@@ +0,0 @@\n"
                    '-    public const string Tag = "7.4";\n'
                    '+    public const string Tag = "8.0";\n'
                ),
            ),
        ]
        result = compute_signals.compute_signals(pr, files)
        self.assertEqual(result["recommendation"], "docs_required")
        self.assertTrue(result["signals"]["container_image_tags_file_changed"])
        self.assertTrue(result["signals"]["container_image_version_changed"])

    def test_new_hosting_integration_package(self) -> None:
        pr = _pr(body="Adds Aspire.Hosting.Foo.")
        files = [
            _file(
                "src/Aspire.Hosting.Foo/Aspire.Hosting.Foo.csproj",
                status="added",
                patch="@@ +0,0 @@\n+<Project>\n",
            ),
            _file(
                "src/Aspire.Hosting.Foo/README.md",
                status="added",
                patch="@@ +0,0 @@\n+# Foo\n",
            ),
        ]
        result = compute_signals.compute_signals(pr, files)
        self.assertEqual(result["recommendation"], "docs_required")
        self.assertTrue(result["signals"]["new_package_added"])
        self.assertTrue(result["signals"]["new_hosting_integration_project"])
        self.assertTrue(result["signals"]["integration_readme_changed"])

    def test_public_api_change_with_obsolete_attribute(self) -> None:
        pr = _pr(body="Marks AddFoo overload as obsolete; users should use AddFoo2.")
        files = [
            _file(
                "src/Aspire.Hosting/api/Aspire.Hosting.cs",
                patch=(
                    "@@ +0,0 @@\n"
                    "-        public static IResourceBuilder<FooResource> AddFoo("
                    "this IDistributedApplicationBuilder builder) { throw null; }\n"
                    '+        [Obsolete("Use AddFoo2 instead.")]\n'
                    "+        public static IResourceBuilder<FooResource> AddFoo("
                    "this IDistributedApplicationBuilder builder) { throw null; }\n"
                ),
            ),
        ]
        result = compute_signals.compute_signals(pr, files)
        self.assertEqual(result["recommendation"], "docs_required")
        self.assertTrue(result["signals"]["public_api_surface_file_changed"])
        self.assertTrue(result["signals"]["obsolete_attribute_added"])
        self.assertTrue(result["signals"]["breaking_api_removal"])
        self.assertTrue(result["signals"]["pr_body_has_deprecation_marker"])

    def test_security_fix_with_security_label(self) -> None:
        pr = _pr(
            body="Fixes CVE-2026-12345 in dashboard auth.",
            labels=["security", "area-dashboard"],
        )
        files = [
            _file(
                "src/Aspire.Dashboard/Api/AuthEndpoints.cs",
                patch=(
                    "@@ +0,0 @@\n"
                    "+        if (Request.IsAuthenticated())\n"
                ),
            ),
        ]
        result = compute_signals.compute_signals(pr, files)
        self.assertEqual(result["recommendation"], "docs_required")
        self.assertTrue(result["signals"]["dashboard_api_endpoint_changed"])
        self.assertTrue(result["signals"]["pr_body_has_security_marker"])
        self.assertTrue(result["signals"]["pr_label_security"])

    def test_analyzer_and_diagnostics_catalog(self) -> None:
        pr = _pr(body="Adds new analyzer ASPIRE001 for invalid resource names.")
        files = [
            _file(
                "src/Aspire.Hosting.Analyzers/InvalidResourceNameAnalyzer.cs",
                status="added",
                patch=(
                    "@@ +0,0 @@\n"
                    "+public sealed class InvalidResourceNameAnalyzer : "
                    "DiagnosticAnalyzer { }\n"
                ),
            ),
            _file(
                "docs/list-of-diagnostics.md",
                patch="@@ +0,0 @@\n+| ASPIRE001 | Invalid resource name |\n",
            ),
        ]
        result = compute_signals.compute_signals(pr, files)
        self.assertEqual(result["recommendation"], "docs_required")
        self.assertTrue(result["signals"]["analyzer_source_changed"])
        self.assertTrue(result["signals"]["diagnostic_documentation_changed"])
        self.assertTrue(result["signals"]["new_public_type"])

    def test_test_only_change_is_optional(self) -> None:
        pr = _pr(body="Adds a flaky test fix.")
        files = [
            _file(
                "tests/Aspire.Hosting.Tests/Foo.cs",
                patch="@@ +0,0 @@\n+        Assert.True(true);\n",
            ),
        ]
        result = compute_signals.compute_signals(pr, files)
        self.assertEqual(result["recommendation"], "docs_optional")
        self.assertTrue(result["signals"]["only_test_or_build_changes"])

    def test_build_config_only_change_is_optional(self) -> None:
        pr = _pr(body="Updates SDK pin.")
        files = [
            _file(
                "global.json",
                patch=(
                    "@@ +0,0 @@\n"
                    '-    "version": "10.0.100-rc.1"\n'
                    '+    "version": "10.0.100-rc.2"\n'
                ),
            ),
        ]
        result = compute_signals.compute_signals(pr, files)
        self.assertEqual(result["recommendation"], "docs_optional")
        self.assertTrue(result["signals"]["only_test_or_build_changes"])

    def test_breaking_change_label_and_api_removal(self) -> None:
        pr = _pr(
            body="Removes deprecated AddFoo overload.",
            labels=["kind/breaking-change"],
        )
        files = [
            _file(
                "src/Aspire.Hosting/api/Aspire.Hosting.cs",
                patch=(
                    "@@ +0,0 @@\n"
                    "-        public static IResourceBuilder<FooResource> AddFoo("
                    "this IDistributedApplicationBuilder builder) { throw null; }\n"
                ),
            ),
        ]
        result = compute_signals.compute_signals(pr, files)
        self.assertEqual(result["recommendation"], "docs_required")
        self.assertTrue(result["signals"]["public_api_surface_file_changed"])
        self.assertTrue(result["signals"]["breaking_api_removal"])
        self.assertTrue(result["signals"]["pr_label_breaking_change"])

    def test_project_template_change(self) -> None:
        pr = _pr(body="Adds aspire-mcp template.")
        files = [
            _file(
                "src/Aspire.ProjectTemplates/templates/aspire-mcp/Program.cs",
                status="added",
                patch='@@ +0,0 @@\n+Console.WriteLine("hi");\n',
            ),
        ]
        result = compute_signals.compute_signals(pr, files)
        self.assertEqual(result["recommendation"], "docs_required")
        self.assertTrue(result["signals"]["project_template_changed"])

    def test_defaults_file_change(self) -> None:
        pr = _pr(body="Adjusts default startup timeout.")
        files = [
            _file(
                "src/Aspire.Hosting/ApplicationModel/KnownDefaults.cs",
                patch=(
                    "@@ +0,0 @@\n"
                    "-    public const int StartupTimeoutSeconds = 30;\n"
                    "+    public const int StartupTimeoutSeconds = 60;\n"
                ),
            ),
        ]
        result = compute_signals.compute_signals(pr, files)
        self.assertEqual(result["recommendation"], "docs_required")
        self.assertTrue(result["signals"]["defaults_or_constants_file_changed"])


class DirectionAwareDiffScanTests(unittest.TestCase):
    """Tests covering direction-aware diff scanning.

    These tests guard against regressions on the review feedback at
    https://github.com/microsoft/aspire/pull/16983 (Copilot review):
    diff-content signals that conceptually mean "something changed"
    must also fire on pure deletions, not just additions.
    """

    def test_dashboard_endpoint_removal_only_fires(self) -> None:
        # Pure deletion of a dashboard endpoint line should still
        # gate to docs_required.
        pr = _pr(body="Removes the deprecated /api/legacy endpoint.")
        files = [
            _file(
                "src/Aspire.Dashboard/DashboardEndpointsBuilder.cs",
                patch=(
                    "@@ +0,0 @@\n"
                    '-        app.MapGet("/api/legacy", ctx => ...);\n'
                ),
            ),
        ]
        result = compute_signals.compute_signals(pr, files)
        self.assertTrue(result["signals"]["dashboard_api_endpoint_changed"])
        self.assertEqual(result["recommendation"], "docs_required")
        # The evidence hint should mark this as a removal.
        hint = result["evidence"]["dashboard_api_endpoint_changed"][0]["hint"]
        self.assertTrue(hint.startswith("-"), msg=f"expected hint to start with '-', got {hint!r}")

    def test_container_image_tag_removal_only_fires(self) -> None:
        pr = _pr(body="Removes obsolete Redis tag pin.")
        files = [
            _file(
                "src/Aspire.Hosting.Redis/RedisContainerImageTags.cs",
                patch=(
                    "@@ +0,0 @@\n"
                    '-    public const string Tag = "7.4";\n'
                ),
            ),
        ]
        result = compute_signals.compute_signals(pr, files)
        self.assertTrue(result["signals"]["container_image_version_changed"])
        self.assertEqual(result["recommendation"], "docs_required")

    def test_target_framework_removal_only_fires(self) -> None:
        pr = _pr(body="Drops net9.0 from multi-target.")
        files = [
            _file(
                "src/Aspire.Hosting.Foo/Aspire.Hosting.Foo.csproj",
                patch=(
                    "@@ +0,0 @@\n"
                    "-    <TargetFrameworks>net9.0;net10.0</TargetFrameworks>\n"
                ),
            ),
        ]
        result = compute_signals.compute_signals(pr, files)
        self.assertTrue(result["signals"]["target_framework_changed"])
        self.assertEqual(result["recommendation"], "docs_required")


class MissingPatchTests(unittest.TestCase):
    """Tests covering the diff_scan_skipped_due_to_missing_patch signal.

    Guards against the review feedback at
    https://github.com/microsoft/aspire/pull/16983 (Copilot review):
    files matched by a diff trigger whose patch is omitted by the
    GitHub API (large files) should no longer be silently skipped.
    """

    def test_missing_patch_for_diff_trigger_path_emits_signal(self) -> None:
        # A C# file under src/ would normally be scanned for several
        # diff triggers (obsolete_attribute_added, new_public_type,
        # default_value_attribute_changed). When the patch is missing
        # we cannot verify those, so we conservatively gate.
        pr = _pr(body="Big refactor; diff too large to render.")
        files = [
            _file(
                "src/Aspire.Hosting/SomeHugeFile.cs",
                # No patch — simulates GitHub omitting the field.
                patch=None,
            ),
        ]
        result = compute_signals.compute_signals(pr, files)
        self.assertTrue(result["signals"]["diff_scan_skipped_due_to_missing_patch"])
        self.assertEqual(result["recommendation"], "docs_required")
        self.assertIn(
            "diff_scan_skipped_due_to_missing_patch",
            result["evidence"],
        )
        # Evidence should name the file and the signal we would have scanned.
        ev = result["evidence"]["diff_scan_skipped_due_to_missing_patch"]
        self.assertTrue(any(e["file"] == "src/Aspire.Hosting/SomeHugeFile.cs" for e in ev))

    def test_missing_patch_for_non_matching_path_does_not_emit_signal(self) -> None:
        # A file under tests/ does not match any diff trigger path
        # regex, so a missing patch there should NOT raise the signal.
        pr = _pr(body="Renames a large test file.")
        files = [
            _file(
                "tests/Aspire.Hosting.Tests/HugeTest.cs",
                patch=None,
            ),
        ]
        result = compute_signals.compute_signals(pr, files)
        self.assertFalse(result["signals"]["diff_scan_skipped_due_to_missing_patch"])
        self.assertEqual(result["recommendation"], "docs_optional")


class PathTriggerHygieneTests(unittest.TestCase):
    """Spot-checks that path triggers don't match obviously-unrelated paths."""

    def test_base_command_excluded_from_cli_command_added(self) -> None:
        pr = _pr()
        files = [
            _file("src/Aspire.Cli/Commands/BaseCommand.cs", status="added"),
        ]
        result = compute_signals.compute_signals(pr, files)
        self.assertFalse(result["signals"]["cli_command_added"])

    def test_test_csproj_does_not_trigger_new_package_added(self) -> None:
        # We deliberately scope `new_package_added` to `^src/.+\.csproj$`,
        # not `^tests/`. A new test project must not gate to docs_required.
        pr = _pr()
        files = [
            _file(
                "tests/Aspire.Foo.Tests/Aspire.Foo.Tests.csproj",
                status="added",
            ),
        ]
        result = compute_signals.compute_signals(pr, files)
        self.assertFalse(result["signals"]["new_package_added"])
        self.assertEqual(result["recommendation"], "docs_optional")


class JsonSerializationTests(unittest.TestCase):
    """Ensure compute_signals produces a JSON-serializable, well-shaped dict."""

    def test_empty_input_is_docs_optional(self) -> None:
        result = compute_signals.compute_signals(_pr(), [])
        self.assertEqual(result["recommendation"], "docs_optional")
        self.assertEqual(result["signal_count"], 0)
        self.assertEqual(result["triggered_signals"], [])
        # only_test_or_build_changes is False when there are no files
        # (vacuously-true is not what we want here).
        self.assertFalse(result["signals"]["only_test_or_build_changes"])

    def test_triggered_signals_are_sorted_and_exclude_advisory(self) -> None:
        pr = _pr(
            body="Breaking change: removes the legacy /api/legacy endpoint.",
            labels=["breaking-change"],
        )
        files = [
            _file(
                "src/Aspire.Dashboard/DashboardEndpointsBuilder.cs",
                patch="@@ +0,0 @@\n-        app.MapGet(\"/api/legacy\", ...);\n",
            ),
        ]
        result = compute_signals.compute_signals(pr, files)
        # triggered_signals is sorted.
        self.assertEqual(result["triggered_signals"], sorted(result["triggered_signals"]))
        # only_test_or_build_changes is never in triggered_signals.
        self.assertNotIn("only_test_or_build_changes", result["triggered_signals"])
        # signal_count equals len(triggered_signals).
        self.assertEqual(result["signal_count"], len(result["triggered_signals"]))


if __name__ == "__main__":
    unittest.main()
