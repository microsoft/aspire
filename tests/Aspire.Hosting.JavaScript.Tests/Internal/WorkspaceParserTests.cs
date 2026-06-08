// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.JavaScript.Internal.Workspace;

namespace Aspire.Hosting.JavaScript.Tests.Internal;

public class WorkspaceParserTests
{
    [Fact]
    public void PackageJsonWorkspacesParserParsesArrayForm()
    {
        const string content = """
        {
            "name": "workspace-root",
            "private": true,
            "workspaces": ["packages/*", "apps/web"]
        }
        """;

        var patterns = PackageJsonWorkspacesParser.Parse(content);

        Assert.Equal(["packages/*", "apps/web"], patterns);
    }

    [Fact]
    public void PackageJsonWorkspacesParserParsesObjectForm()
    {
        const string content = """
        {
            "name": "workspace-root",
            "private": true,
            "workspaces": {
                "packages": ["packages/*", "apps/*"]
            }
        }
        """;

        var patterns = PackageJsonWorkspacesParser.Parse(content);

        Assert.Equal(["packages/*", "apps/*"], patterns);
    }

    [Fact]
    public void PackageJsonWorkspacesParserReturnsEmptyForInvalidJson()
    {
        var patterns = PackageJsonWorkspacesParser.Parse("{ not valid json");

        Assert.Empty(patterns);
    }

    [Fact]
    public void PackageJsonWorkspacesParserReturnsEmptyWhenNoWorkspacesField()
    {
        var patterns = PackageJsonWorkspacesParser.Parse("""{ "name": "root" }""");

        Assert.Empty(patterns);
    }

    [Fact]
    public void PackageJsonWorkspacesParserFiltersEmptyEntries()
    {
        const string content = """
        {
            "workspaces": ["packages/*", "", "apps/web"]
        }
        """;

        var patterns = PackageJsonWorkspacesParser.Parse(content);

        Assert.Equal(["packages/*", "apps/web"], patterns);
    }

    [Fact]
    public void PnpmWorkspaceYamlParserParsesBlockSequence()
    {
        const string content = """
        packages:
          - "packages/*"
          - "apps/web"
        """;

        var patterns = PnpmWorkspaceYamlParser.Parse(content);

        Assert.Equal(["packages/*", "apps/web"], patterns);
    }

    [Fact]
    public void PnpmWorkspaceYamlParserParsesFlowSequence()
    {
        const string content = """
        packages: ["packages/*", "apps/web"]
        """;

        var patterns = PnpmWorkspaceYamlParser.Parse(content);

        Assert.Equal(["packages/*", "apps/web"], patterns);
    }

    [Fact]
    public void PnpmWorkspaceYamlParserToleratesUnrelatedSettings()
    {
        const string content = """
        packages:
          - "packages/*"
        catalog:
          react: ^18.0.0
        onlyBuiltDependencies:
          - esbuild
        """;

        var patterns = PnpmWorkspaceYamlParser.Parse(content);

        Assert.Equal(["packages/*"], patterns);
    }

    [Fact]
    public void PnpmWorkspaceYamlParserReturnsEmptyWhenPackagesMissing()
    {
        var patterns = PnpmWorkspaceYamlParser.Parse("catalog:\n  react: ^18.0.0");

        Assert.Empty(patterns);
    }

    [Fact]
    public void PnpmWorkspaceYamlParserReturnsEmptyForEmptyContent()
    {
        var patterns = PnpmWorkspaceYamlParser.Parse(string.Empty);

        Assert.Empty(patterns);
    }

    [Fact]
    public void ParseInjectWorkspacePackagesReadsCamelCaseTrue()
    {
        var value = PnpmWorkspaceYamlParser.ParseInjectWorkspacePackages("injectWorkspacePackages: true");

        Assert.True(value);
    }

    [Fact]
    public void ParseInjectWorkspacePackagesReadsKebabCaseTrue()
    {
        var value = PnpmWorkspaceYamlParser.ParseInjectWorkspacePackages("inject-workspace-packages: true");

        Assert.True(value);
    }

    [Fact]
    public void ParseInjectWorkspacePackagesReadsFalse()
    {
        var value = PnpmWorkspaceYamlParser.ParseInjectWorkspacePackages("injectWorkspacePackages: false");

        Assert.False(value);
    }

    [Fact]
    public void ParseInjectWorkspacePackagesReturnsNullWhenAbsent()
    {
        var value = PnpmWorkspaceYamlParser.ParseInjectWorkspacePackages("packages:\n  - \"packages/*\"");

        Assert.Null(value);
    }

    [Theory]
    [InlineData("!apps/legacy")]
    [InlineData("packages/**")]
    [InlineData("apps/*-svc")]
    [InlineData("apps/api-*")]
    [InlineData("*/api")]
    public void WorkspacePatternValidatorThrowsForUnsupportedShapes(string pattern)
    {
        Assert.Throws<DistributedApplicationException>(() => WorkspacePatternValidator.Validate([pattern], "/root"));
    }

    [Theory]
    [InlineData("apps/web")]
    [InlineData("packages/utils")]
    [InlineData("packages/*")]
    [InlineData("*")]
    public void WorkspacePatternValidatorAllowsSupportedShapes(string pattern)
    {
        WorkspacePatternValidator.Validate([pattern], "/root");
    }

    [Fact]
    public void WorkspacePatternValidatorAllowsLiteralAndTrailingStarTogether()
    {
        WorkspacePatternValidator.Validate(["apps/web", "packages/*"], "/root");
    }

    [Theory]
    [InlineData("pnpm@10.4.1", 10)]
    [InlineData("pnpm@9.0.0", 9)]
    [InlineData("pnpm@10", 10)]
    [InlineData("pnpm@8.15.4+sha512.abc", 8)]
    [InlineData("pnpm@11.0.0-rc.1", 11)]
    public void PnpmPackageManagerVersionParsesPnpmMajor(string packageManager, int expected)
    {
        Assert.Equal(expected, PnpmPackageManagerVersion.TryParseMajorVersion(packageManager));
    }

    [Theory]
    [InlineData("yarn@4.0.0")]
    [InlineData("npm@10.2.0")]
    [InlineData("pnpm@notaversion")]
    public void PnpmPackageManagerVersionReturnsNullForNonPnpmOrUnparsable(string packageManager)
    {
        Assert.Null(PnpmPackageManagerVersion.TryParseMajorVersion(packageManager));
    }
}
