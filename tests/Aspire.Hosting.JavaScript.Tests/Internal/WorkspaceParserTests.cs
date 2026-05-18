// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Pure unit tests for the workspace parsing/validation/matching helpers.
//
// Test data is drawn (with attribution) from the canonical upstream package
// manager test suites and example fixtures, so the cases here exercise the
// real-world shapes that npm/yarn/pnpm/bun/Turborepo monorepos produce. We
// don't import the upstream sources — we mirror the input shape and assert
// our own behavior against it.

using Aspire.Hosting.JavaScript.Internal.Workspace;

namespace Aspire.Hosting.JavaScript.Tests.Internal;

public class WorkspaceParserTests
{
    public class PackageJsonWorkspacesParserTests
    {
        [Fact]
        public void Parse_ArrayForm_ReturnsPatterns()
        {
            // shape from npm/cli get-workspaces fixtures (npm 7+):
            //   https://github.com/npm/cli/blob/latest/test/lib/utils/get-workspaces.js
            var json = """{ "name": "root", "workspaces": ["packages/*", "apps/web"] }""";

            var patterns = PackageJsonWorkspacesParser.Parse(json);

            Assert.Equal(["packages/*", "apps/web"], patterns);
        }

        [Fact]
        public void Parse_ObjectFormWithPackages_ReturnsPatterns()
        {
            // shape from yarn classic / npm RFC 0026 (workspaces object form):
            //   https://github.com/npm/rfcs/blob/main/accepted/0026-workspaces.md
            //   https://classic.yarnpkg.com/blog/2018/02/15/nohoist
            var json = """
                {
                  "name": "root",
                  "private": true,
                  "workspaces": {
                    "packages": ["packages/*", "tools/*"],
                    "nohoist": ["**/react"]
                  }
                }
                """;

            var patterns = PackageJsonWorkspacesParser.Parse(json);

            Assert.Equal(["packages/*", "tools/*"], patterns);
        }

        [Fact]
        public void Parse_TurborepoBasicExample_ReturnsPatterns()
        {
            // shape from vercel/turborepo examples/basic/package.json:
            //   https://github.com/vercel/turborepo/tree/main/examples/basic
            var json = """
                {
                  "name": "basic",
                  "private": true,
                  "workspaces": ["apps/*", "packages/*"]
                }
                """;

            var patterns = PackageJsonWorkspacesParser.Parse(json);

            Assert.Equal(["apps/*", "packages/*"], patterns);
        }

        [Fact]
        public void Parse_NoWorkspacesField_ReturnsEmpty()
        {
            var json = """{ "name": "single-package" }""";

            Assert.Empty(PackageJsonWorkspacesParser.Parse(json));
        }

        [Fact]
        public void Parse_EmptyArray_ReturnsEmpty()
        {
            var json = """{ "workspaces": [] }""";

            Assert.Empty(PackageJsonWorkspacesParser.Parse(json));
        }

        [Fact]
        public void Parse_ObjectWithoutPackagesKey_ReturnsEmpty()
        {
            // yarn nohoist-only form (rare in practice; npm rejects it)
            var json = """{ "workspaces": { "nohoist": ["**/react"] } }""";

            Assert.Empty(PackageJsonWorkspacesParser.Parse(json));
        }

        [Fact]
        public void Parse_MalformedJson_ReturnsEmpty()
        {
            Assert.Empty(PackageJsonWorkspacesParser.Parse("{not json"));
        }

        [Fact]
        public void Parse_NonObjectRoot_ReturnsEmpty()
        {
            Assert.Empty(PackageJsonWorkspacesParser.Parse("[1, 2, 3]"));
        }

        [Fact]
        public void Parse_MixedTypeArrayEntries_ReturnsEmpty()
        {
            // npm's parser rejects non-string entries, and the DTO parser treats the
            // whole declaration as unsupported rather than partly accepting invalid data.
            var json = """{ "workspaces": ["apps/web", null, 42, "packages/*", { "x": 1 }] }""";

            Assert.Empty(PackageJsonWorkspacesParser.Parse(json));
        }

        [Fact]
        public void Parse_ScopedPackagePattern_IsPreserved()
        {
            // shape sometimes seen in npm orgs (e.g. shopify/cli, microsoft/rushstack):
            //   https://github.com/microsoft/rushstack
            var json = """{ "workspaces": ["packages/@scope/*"] }""";

            Assert.Equal(["packages/@scope/*"], PackageJsonWorkspacesParser.Parse(json));
        }
    }

    public class PnpmWorkspaceYamlParserTests
    {
        [Fact]
        public void Parse_BlockForm_ReturnsPatterns()
        {
            // canonical pnpm form from pnpm-workspace.yaml docs:
            //   https://pnpm.io/pnpm-workspace_yaml#packages
            var yaml = """
                packages:
                  - 'apps/*'
                  - 'packages/*'
                """;

            Assert.Equal(["apps/*", "packages/*"], PnpmWorkspaceYamlParser.Parse(yaml));
        }

        [Fact]
        public void Parse_FlowForm_ReturnsPatterns()
        {
            // YAML 1.2 flow-sequence form; this is what tripped the prior
            // hand-rolled scanner (it returned empty)
            var yaml = "packages: [apps/*, packages/*]\n";

            Assert.Equal(["apps/*", "packages/*"], PnpmWorkspaceYamlParser.Parse(yaml));
        }

        [Fact]
        public void Parse_WithComments_ReturnsPatterns()
        {
            // shape from pnpm/pnpm find-workspace-packages fixtures:
            //   https://github.com/pnpm/pnpm/tree/main/workspace/find-workspace-packages/test/fixtures
            var yaml = """
                # workspace declaration
                packages:
                  - 'apps/*'   # all apps
                  - 'packages/*' # all libs
                  # legacy services live elsewhere
                  - 'services/auth'
                """;

            Assert.Equal(["apps/*", "packages/*", "services/auth"], PnpmWorkspaceYamlParser.Parse(yaml));
        }

        [Fact]
        public void Parse_TurborepoWithPnpmExample_ReturnsPatterns()
        {
            // shape from vercel/turborepo examples/with-pnpm/pnpm-workspace.yaml:
            //   https://github.com/vercel/turborepo/tree/main/examples/with-pnpm
            var yaml = """
                packages:
                  - "apps/*"
                  - "packages/*"
                """;

            Assert.Equal(["apps/*", "packages/*"], PnpmWorkspaceYamlParser.Parse(yaml));
        }

        [Fact]
        public void Parse_QuotedAndUnquotedScalars_ReturnsPatterns()
        {
            var yaml = """
                packages:
                  - apps/web
                  - 'apps/api'
                  - "packages/*"
                """;

            Assert.Equal(["apps/web", "apps/api", "packages/*"], PnpmWorkspaceYamlParser.Parse(yaml));
        }

        [Fact]
        public void Parse_WithCatalogField_ReturnsPackagesOnly()
        {
            // pnpm 9+ catalog feature: catalogs sit alongside packages and must
            // be ignored. See https://pnpm.io/catalogs
            var yaml = """
                packages:
                  - 'packages/*'
                catalog:
                  react: ^18.2.0
                  react-dom: ^18.2.0
                """;

            Assert.Equal(["packages/*"], PnpmWorkspaceYamlParser.Parse(yaml));
        }

        [Fact]
        public void ParseInjectWorkspacePackages_WhenTrue_ReturnsTrue()
        {
            // pnpm 10 non-legacy deploy requires this workspace-level setting:
            //   https://pnpm.io/cli/deploy
            var yaml = """
                packages:
                  - 'packages/*'
                injectWorkspacePackages: true
                """;

            Assert.True(PnpmWorkspaceYamlParser.ParseInjectWorkspacePackages(yaml));
        }

        [Fact]
        public void ParseInjectWorkspacePackages_WhenMissing_ReturnsNull()
        {
            var yaml = """
                packages:
                  - 'packages/*'
                """;

            Assert.Null(PnpmWorkspaceYamlParser.ParseInjectWorkspacePackages(yaml));
        }

        [Fact]
        public void ParseInjectWorkspacePackages_WhenFalse_ReturnsFalse()
        {
            var yaml = """
                packages:
                  - 'packages/*'
                injectWorkspacePackages: false
                """;

            Assert.False(PnpmWorkspaceYamlParser.ParseInjectWorkspacePackages(yaml));
        }

        [Fact]
        public void Parse_MissingPackagesKey_ReturnsEmpty()
        {
            var yaml = """
                catalog:
                  react: ^18.2.0
                """;

            Assert.Empty(PnpmWorkspaceYamlParser.Parse(yaml));
        }

        [Fact]
        public void Parse_NonMappingRoot_ReturnsEmpty()
        {
            Assert.Empty(PnpmWorkspaceYamlParser.Parse("- 'apps/*'\n"));
        }

        [Fact]
        public void Parse_Empty_ReturnsEmpty()
        {
            Assert.Empty(PnpmWorkspaceYamlParser.Parse(string.Empty));
            Assert.Empty(PnpmWorkspaceYamlParser.Parse("\n"));
        }

        [Fact]
        public void Parse_MalformedYaml_ReturnsEmpty()
        {
            // unbalanced quote — must not throw
            Assert.Empty(PnpmWorkspaceYamlParser.Parse("packages:\n  - 'apps/*\n"));
        }

        [Fact]
        public void Parse_PreservesNegationPrefix()
        {
            // negation is not supported by us (Validator throws), but the
            // parser must surface the raw pattern so the validator can report
            // it with attribution. Don't silently drop it here.
            var yaml = """
                packages:
                  - 'apps/*'
                  - '!apps/legacy'
                """;

            Assert.Equal(["apps/*", "!apps/legacy"], PnpmWorkspaceYamlParser.Parse(yaml));
        }
    }

    public class PnpmPackageManagerVersionTests
    {
        [Theory]
        [InlineData("pnpm@10.33.4", 10)]
        [InlineData("pnpm@9.15.9+sha224.deadbeef", 9)]
        [InlineData("pnpm@8.15.9", 8)]
        [InlineData("pnpm@10.0.0-beta.1", 10)]
        public void TryParseMajorVersion_PnpmVersion_ReturnsMajor(string packageManager, int expected)
        {
            Assert.Equal(expected, PnpmPackageManagerVersion.TryParseMajorVersion(packageManager));
        }

        [Theory]
        [InlineData("npm@10.9.0")]
        [InlineData("pnpm")]
        [InlineData("pnpm@latest")]
        public void TryParseMajorVersion_NonVersionedOrNonPnpm_ReturnsNull(string packageManager)
        {
            Assert.Null(PnpmPackageManagerVersion.TryParseMajorVersion(packageManager));
        }

        [Fact]
        public void TryReadMajorVersion_PackageManagerField_ReturnsMajor()
        {
            using var tempDir = new TestTempDirectory();
            File.WriteAllText(Path.Combine(tempDir.Path, "package.json"),
                """{"packageManager":"pnpm@10.33.4+sha224.deadbeef"}""");

            Assert.Equal(10, PnpmPackageManagerVersion.TryReadMajorVersion(tempDir.Path));
        }

        [Fact]
        public void TryReadMajorVersion_DevEnginesPackageManagerFallback_ReturnsMajor()
        {
            using var tempDir = new TestTempDirectory();
            File.WriteAllText(Path.Combine(tempDir.Path, "package.json"),
                """
                {
                  "devEngines": {
                    "packageManager": {
                      "name": "pnpm",
                      "version": "11.0.8+sha224.deadbeef"
                    }
                  }
                }
                """);

            Assert.Equal(11, PnpmPackageManagerVersion.TryReadMajorVersion(tempDir.Path));
        }

        [Fact]
        public void TryReadMajorVersion_DevEnginesPackageManagerRange_ReturnsNull()
        {
            using var tempDir = new TestTempDirectory();
            File.WriteAllText(Path.Combine(tempDir.Path, "package.json"),
                """
                {
                  "devEngines": {
                    "packageManager": {
                      "name": "pnpm",
                      "version": ">=11.0.0"
                    }
                  }
                }
                """);

            Assert.Null(PnpmPackageManagerVersion.TryReadMajorVersion(tempDir.Path));
        }
    }

    public class WorkspacePatternValidatorTests
    {
        [Theory]
        [InlineData("apps/web")]
        [InlineData("packages/utils")]
        [InlineData("packages/*")]
        [InlineData("apps/*")]
        [InlineData("services/auth")]
        // Turborepo basic / with-pnpm examples
        [InlineData("packages/@scope/utils")]
        public void Validate_SupportedShape_DoesNotThrow(string pattern)
        {
            WorkspacePatternValidator.Validate([pattern], "/root");
        }

        [Theory]
        // negation — pnpm-workspace.yaml supports this; we don't.
        [InlineData("!apps/legacy", "Negated workspace pattern")]
        // recursive — minimatch & pnpm matcher both support; we don't.
        [InlineData("packages/**", "Recursive workspace pattern")]
        [InlineData("**/internal", "Recursive workspace pattern")]
        // non-trailing star — supported by minimatch; we don't because the
        // matcher would silently drop them.
        [InlineData("apps/*-svc", "unsupported glob shape")]
        [InlineData("apps/api-*", "unsupported glob shape")]
        [InlineData("*/api", "unsupported glob shape")]
        public void Validate_UnsupportedShape_Throws(string pattern, string fragment)
        {
            var ex = Assert.Throws<DistributedApplicationException>(() =>
                WorkspacePatternValidator.Validate([pattern], "/root"));
            Assert.Contains(fragment, ex.Message);
        }

        [Fact]
        public void Validate_EmptyAndNullPatternsTolerated()
        {
            // empty entries are no-ops (some YAML/JSON files emit them);
            // matcher / discovery later resolves to no members.
            WorkspacePatternValidator.Validate([string.Empty], "/root");
        }

        [Fact]
        public void Validate_FirstFailure_PointsAtPattern()
        {
            var ex = Assert.Throws<DistributedApplicationException>(() =>
                WorkspacePatternValidator.Validate(["apps/web", "!apps/legacy", "packages/*"], "/some/root"));
            Assert.Contains("'!apps/legacy'", ex.Message);
            Assert.Contains("/some/root", ex.Message);
        }
    }

    public class WorkspacePatternMatcherTests
    {
        [Theory]
        // literal exact match
        [InlineData("apps/web", "apps/web", true)]
        [InlineData("apps/web", "apps/api", false)]
        [InlineData("apps/web", "apps/web/sub", false)]
        // trailing star matches direct children only (one segment deep)
        [InlineData("packages/*", "packages/utils", true)]
        [InlineData("packages/*", "packages/utils/sub", false)]
        [InlineData("packages/*", "apps/web", false)]
        // dotted dirs are excluded by trailing star (mirrors minimatch default)
        [InlineData("packages/*", "packages/.git", false)]
        [InlineData("packages/*", "packages/.cache", false)]
        // backslash candidate paths normalize to forward slash
        [InlineData("packages/*", "packages\\utils", true)]
        // leading "./" is stripped from both pattern and candidate
        [InlineData("./apps/web", "apps/web", true)]
        [InlineData("apps/web", "./apps/web", true)]
        // trailing "/" is stripped
        [InlineData("apps/web/", "apps/web", true)]
        // unsupported shape (validator should reject upstream); matcher is
        // lenient and returns false
        [InlineData("apps/*-svc", "apps/auth-svc", false)]
        public void IsMatch_BehavesAsDocumented(string pattern, string candidate, bool expected)
        {
            Assert.Equal(expected, WorkspacePatternMatcher.IsMatch(pattern, candidate));
        }

        [Fact]
        public void IsMatch_TopLevelTrailingStar_MatchesFirstLevelDirs()
        {
            // "*" alone matches any first-level directory
            Assert.True(WorkspacePatternMatcher.IsMatch("*", "apps"));
            Assert.False(WorkspacePatternMatcher.IsMatch("*", "apps/web"));
            Assert.False(WorkspacePatternMatcher.IsMatch("*", ".git"));
        }
    }

    public class WorkspacePatternExpanderTests
    {
        [Fact]
        public void Expand_TrailingStar_ReturnsPackageDirectoriesSorted()
        {
            // Filesystem shape mirrors the WorkspacePatternExpander comment and the
            // common npm/yarn/pnpm/Turborepo "packages/*" monorepo layout:
            //
            //   root/
            //     packages/
            //       web/package.json
            //       api/package.json
            //       docs/README.md
            using var tempDir = new TestTempDirectory();
            WritePackageJson(tempDir.Path, "packages/web");
            WritePackageJson(tempDir.Path, "packages/api");
            Directory.CreateDirectory(Path.Combine(tempDir.Path, "packages", "docs"));
            File.WriteAllText(Path.Combine(tempDir.Path, "packages", "docs", "README.md"), "# docs");

            var result = WorkspacePatternExpander.Expand(tempDir.Path, ["packages/*"]);

            Assert.Equal(["packages/api", "packages/web"], result);
        }

        [Fact]
        public void Expand_LiteralPath_ReturnsPackageDirectory()
        {
            using var tempDir = new TestTempDirectory();
            WritePackageJson(tempDir.Path, "apps/web");
            Directory.CreateDirectory(Path.Combine(tempDir.Path, "apps", "docs"));

            var result = WorkspacePatternExpander.Expand(tempDir.Path, ["apps/web", "apps/docs"]);

            Assert.Equal(["apps/web"], result);
        }

        [Fact]
        public void Expand_SkipsDottedDirectoriesAndUnsupportedShapes()
        {
            using var tempDir = new TestTempDirectory();
            WritePackageJson(tempDir.Path, "packages/web");
            WritePackageJson(tempDir.Path, "packages/.cache");
            WritePackageJson(tempDir.Path, "apps/auth-svc");

            var result = WorkspacePatternExpander.Expand(tempDir.Path, ["packages/*", "apps/*-svc", "!apps/legacy"]);

            Assert.Equal(["packages/web"], result);
        }

        [Fact]
        public void Expand_NormalizesLeadingDotBackslashAndTrailingSlash()
        {
            using var tempDir = new TestTempDirectory();
            WritePackageJson(tempDir.Path, "apps/web");

            var result = WorkspacePatternExpander.Expand(tempDir.Path, [".\\apps\\web\\"]);

            Assert.Equal(["apps/web"], result);
        }

        private static void WritePackageJson(string rootPath, string relativePath)
        {
            var packageDir = Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(packageDir);
            File.WriteAllText(Path.Combine(packageDir, "package.json"), """{ "name": "fixture" }""");
        }
    }
}
