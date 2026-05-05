// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

[Collection(CliInstallEnvironmentCollection.Name)]
public class CliInstallStrategyTests
{
    [Fact]
    public void GetPullRequestInstallArgs_ReturnsPrNumber()
    {
        Assert.Equal("123", AspireCliShellCommandHelpers.GetPullRequestInstallArgs(123));
    }

    [Fact]
    public void GetLocalArchiveInstallCommand_FormatsCorrectly()
    {
        var command = AspireCliShellCommandHelpers.GetLocalArchiveInstallCommand("/tmp/cli-archives", "/opt/aspire-scripts/get-aspire-cli-pr.sh");
        Assert.Equal("/opt/aspire-scripts/get-aspire-cli-pr.sh --local-dir '/tmp/cli-archives'", command);
    }

    [Fact]
    public void GetRecordAspireCliVersionCommand_IsBestEffort()
    {
        var strategy = CliInstallStrategy.FromDotnetTool(includePrerelease: true);

        var command = CliE2EAutomatorHelpers.GetRecordAspireCliVersionCommand(strategy, "VER", "BASE_VER");

        Assert.Contains("if mkdir -p \"$ASPIRE_E2E_CLI_VERSION_OUTPUT_DIR\" && ", command);
        Assert.Contains("} > \"$CLI_VERSION_RECORD\"; then echo \"CLI_VERSION_RECORDED:$CLI_VERSION_RECORD\"; else echo \"CLI_VERSION_RECORD_FAILED:$ASPIRE_E2E_CLI_VERSION_OUTPUT_DIR\"; fi; fi", command);
    }

    [Fact]
    public void Detect_ReturnsLocalArchive_WhenArchiveDirIsSet()
    {
        var tempDir = Directory.CreateTempSubdirectory("cli-archives-test");
        try
        {
            using var environment = new EnvironmentVariableScope(
                ("ASPIRE_E2E_ARCHIVE", null),
                ("ASPIRE_E2E_QUALITY", null),
                ("ASPIRE_E2E_VERSION", null),
                ("ASPIRE_E2E_PREINSTALLED", null),
                ("GITHUB_PR_NUMBER", null),
                ("GITHUB_PR_HEAD_SHA", null),
                (CliE2ETestHelpers.CliArchiveDirEnvironmentVariableName, tempDir.FullName),
                ("CI", null),
                ("GITHUB_ACTIONS", null));

            var strategy = CliInstallStrategy.Detect();

            Assert.Equal(CliInstallMode.LocalArchive, strategy.Mode);
            Assert.Equal(tempDir.FullName, strategy.ArchiveDir);
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    [Fact]
    public void Detect_ReturnsLocalArchive_WhenBothPrMetadataAndArchiveDirAreSet()
    {
        var tempDir = Directory.CreateTempSubdirectory("cli-archives-test");
        try
        {
            using var environment = new EnvironmentVariableScope(
                ("ASPIRE_E2E_ARCHIVE", null),
                ("ASPIRE_E2E_QUALITY", null),
                ("ASPIRE_E2E_VERSION", null),
                ("ASPIRE_E2E_PREINSTALLED", null),
                ("GITHUB_PR_NUMBER", "16131"),
                ("GITHUB_PR_HEAD_SHA", "52669a7cac3d4f10c6269909fc38e77124ed177c"),
                (CliE2ETestHelpers.CliArchiveDirEnvironmentVariableName, tempDir.FullName),
                ("CI", null),
                ("GITHUB_ACTIONS", null));

            var strategy = CliInstallStrategy.Detect();

            Assert.Equal(CliInstallMode.LocalArchive, strategy.Mode);
            Assert.Equal(tempDir.FullName, strategy.ArchiveDir);
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    [Fact]
    public void Detect_FallsBackToDevQuality_WhenNoArchiveContextInCI()
    {
        using var environment = new EnvironmentVariableScope(
            ("ASPIRE_E2E_ARCHIVE", null),
            ("ASPIRE_E2E_QUALITY", null),
            ("ASPIRE_E2E_VERSION", null),
            ("ASPIRE_E2E_PREINSTALLED", null),
            ("GITHUB_PR_NUMBER", null),
            ("GITHUB_PR_HEAD_SHA", null),
            (CliE2ETestHelpers.CliArchiveDirEnvironmentVariableName, null),
            ("CI", null),
            ("GITHUB_ACTIONS", "true"));

        var strategy = CliInstallStrategy.Detect();

        Assert.Equal(CliInstallMode.InstallScript, strategy.Mode);
    }

    [Fact]
    public void ConfigureContainer_MountsArchiveDirForLocalArchive()
    {
        var tempDir = Directory.CreateTempSubdirectory("cli-archives-test");
        try
        {
            using var environment = new EnvironmentVariableScope(
                ("ASPIRE_E2E_ARCHIVE", null),
                ("ASPIRE_E2E_QUALITY", null),
                ("ASPIRE_E2E_VERSION", null),
                ("ASPIRE_E2E_PREINSTALLED", null),
                ("GITHUB_PR_NUMBER", null),
                ("GITHUB_PR_HEAD_SHA", null),
                (CliE2ETestHelpers.CliArchiveDirEnvironmentVariableName, tempDir.FullName));

            var strategy = CliInstallStrategy.Detect();
            var options = new DockerContainerOptions();

            strategy.ConfigureContainer(options);

            Assert.Contains($"{tempDir.FullName}:/tmp/aspire-cli-archives:ro", options.Volumes);
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    [Fact]
    public void ConfigureContainer_AddsPrMetadataForPullRequest()
    {
        using var environment = new EnvironmentVariableScope(
            ("ASPIRE_E2E_ARCHIVE", null),
            ("ASPIRE_E2E_DOTNET_TOOL_SOURCE", null),
            ("ASPIRE_E2E_DOTNET_TOOL", null),
            ("ASPIRE_E2E_QUALITY", null),
            ("ASPIRE_E2E_VERSION", null),
            ("GITHUB_PR_NUMBER", "16131"),
            ("GITHUB_PR_HEAD_SHA", "52669a7cac3d4f10c6269909fc38e77124ed177c"),
            (CliE2ETestHelpers.CliArchiveDirEnvironmentVariableName, null));

        var strategy = CliInstallStrategy.Detect();
        var options = new DockerContainerOptions();

        strategy.ConfigureContainer(options);

        Assert.Equal("16131", options.Environment["GITHUB_PR_NUMBER"]);
        Assert.Equal("52669a7cac3d4f10c6269909fc38e77124ed177c", options.Environment["GITHUB_PR_HEAD_SHA"]);
    }

    [Fact]
    public void ConfigureContainer_AddsUbuntuAptMirrorBuildArgWhenEnvironmentVariableIsSet()
    {
        using var environment = new EnvironmentVariableScope(
            (CliInstallStrategy.UbuntuAptMirrorEnvironmentVariableName, "http://azure.archive.ubuntu.com/ubuntu/"));

        var strategy = CliInstallStrategy.LatestGa();
        var options = new DockerContainerOptions();

        strategy.ConfigureContainer(options);

        Assert.Equal("http://azure.archive.ubuntu.com/ubuntu/", options.BuildArgs[CliInstallStrategy.UbuntuAptMirrorBuildArgName]);
    }

    [Fact]
    public void ConfigureContainer_DoesNotAddUbuntuAptMirrorBuildArgWhenEnvironmentVariableIsEmpty()
    {
        using var environment = new EnvironmentVariableScope(
            (CliInstallStrategy.UbuntuAptMirrorEnvironmentVariableName, null));

        var strategy = CliInstallStrategy.LatestGa();
        var options = new DockerContainerOptions();

        strategy.ConfigureContainer(options);

        Assert.DoesNotContain(CliInstallStrategy.UbuntuAptMirrorBuildArgName, options.BuildArgs.Keys);
    }

    [Fact]
    public void ConfigureDockerContainerSource_UsesDotNetImageWhenEnvironmentVariableIsSet()
    {
        using var environment = new EnvironmentVariableScope(
            (CliE2ETestHelpers.DotNetImageEnvironmentVariableName, "aspire-cli-e2e-dotnet:prebuilt"),
            (CliE2ETestHelpers.RequireDotNetImageEnvironmentVariableName, "true"),
            (CliE2ETestHelpers.PolyglotImageEnvironmentVariableName, null),
            (CliE2ETestHelpers.RequirePolyglotImageEnvironmentVariableName, null),
            ("GITHUB_ACTIONS", "true"));
        var options = new DockerContainerOptions();

        CliE2ETestHelpers.ConfigureDockerContainerSource(options, "/repo", CliE2ETestHelpers.DockerfileVariant.DotNet);

        Assert.Equal("aspire-cli-e2e-dotnet:prebuilt", options.Image);
        Assert.True(string.IsNullOrEmpty(options.DockerfilePath));
        Assert.True(string.IsNullOrEmpty(options.BuildContext));
    }

    [Fact]
    public void ConfigureContainer_BuildArgsCanBeClearedForPrebuiltImage()
    {
        using var environment = new EnvironmentVariableScope(
            (CliE2ETestHelpers.DotNetImageEnvironmentVariableName, "aspire-cli-e2e-dotnet:prebuilt"),
            (CliE2ETestHelpers.RequireDotNetImageEnvironmentVariableName, "true"),
            (CliE2ETestHelpers.PolyglotImageEnvironmentVariableName, null),
            (CliE2ETestHelpers.RequirePolyglotImageEnvironmentVariableName, null),
            (CliE2ETestHelpers.PolyglotJavaImageEnvironmentVariableName, null),
            (CliE2ETestHelpers.RequirePolyglotJavaImageEnvironmentVariableName, null),
            (CliInstallStrategy.UbuntuAptMirrorEnvironmentVariableName, "http://azure.archive.ubuntu.com/ubuntu/"));
        var strategy = CliInstallStrategy.LatestGa();
        var options = new DockerContainerOptions();

        CliE2ETestHelpers.ConfigureDockerContainerSource(options, "/repo", CliE2ETestHelpers.DockerfileVariant.DotNet);
        CliE2ETestHelpers.ConfigureDockerContainerStrategy(options, strategy);

        Assert.Equal("aspire-cli-e2e-dotnet:prebuilt", options.Image);
        Assert.Empty(options.BuildArgs);
    }

    [Fact]
    public void ConfigureContainer_PreservesBuildArgsForDockerfileVariant()
    {
        using var environment = new EnvironmentVariableScope(
            (CliE2ETestHelpers.DotNetImageEnvironmentVariableName, "aspire-cli-e2e-dotnet:prebuilt"),
            (CliE2ETestHelpers.RequireDotNetImageEnvironmentVariableName, "true"),
            (CliE2ETestHelpers.PolyglotImageEnvironmentVariableName, null),
            (CliE2ETestHelpers.RequirePolyglotImageEnvironmentVariableName, null),
            (CliInstallStrategy.UbuntuAptMirrorEnvironmentVariableName, "http://azure.archive.ubuntu.com/ubuntu/"));
        var strategy = CliInstallStrategy.LatestGa();
        var options = new DockerContainerOptions();

        CliE2ETestHelpers.ConfigureDockerContainerSource(options, "/repo", CliE2ETestHelpers.DockerfileVariant.Polyglot);
        CliE2ETestHelpers.ConfigureDockerContainerStrategy(options, strategy);

        Assert.Equal(Path.Combine("/repo", "tests", "Shared", "Docker", "Dockerfile.e2e-polyglot-base"), options.DockerfilePath);
        Assert.Equal("true", options.BuildArgs["SKIP_SOURCE_BUILD"]);
        Assert.Equal("http://azure.archive.ubuntu.com/ubuntu/", options.BuildArgs[CliInstallStrategy.UbuntuAptMirrorBuildArgName]);
    }

    [Fact]
    public void ConfigureDockerContainerSource_UsesPolyglotImageWhenEnvironmentVariableIsSet()
    {
        using var environment = new EnvironmentVariableScope(
            (CliE2ETestHelpers.PolyglotImageEnvironmentVariableName, "aspire-cli-e2e-polyglot:prebuilt"),
            (CliE2ETestHelpers.RequirePolyglotImageEnvironmentVariableName, "true"),
            ("GITHUB_ACTIONS", "true"));
        var options = new DockerContainerOptions();

        CliE2ETestHelpers.ConfigureDockerContainerSource(options, "/repo", CliE2ETestHelpers.DockerfileVariant.Polyglot);

        Assert.Equal("aspire-cli-e2e-polyglot:prebuilt", options.Image);
        Assert.True(string.IsNullOrEmpty(options.DockerfilePath));
        Assert.True(string.IsNullOrEmpty(options.BuildContext));
    }

    [Fact]
    public void ConfigureDockerContainerSource_RequiresPolyglotImageWhenConfigured()
    {
        using var environment = new EnvironmentVariableScope(
            (CliE2ETestHelpers.PolyglotImageEnvironmentVariableName, null),
            (CliE2ETestHelpers.RequirePolyglotImageEnvironmentVariableName, "true"),
            ("GITHUB_ACTIONS", "true"));
        var options = new DockerContainerOptions();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CliE2ETestHelpers.ConfigureDockerContainerSource(options, "/repo", CliE2ETestHelpers.DockerfileVariant.Polyglot));

        Assert.Contains(CliE2ETestHelpers.PolyglotImageEnvironmentVariableName, exception.Message);
    }

    [Fact]
    public void ConfigureDockerContainerSource_UsesPolyglotJavaImageWhenEnvironmentVariableIsSet()
    {
        using var environment = new EnvironmentVariableScope(
            (CliE2ETestHelpers.PolyglotJavaImageEnvironmentVariableName, "aspire-cli-e2e-polyglot-java:prebuilt"),
            (CliE2ETestHelpers.RequirePolyglotJavaImageEnvironmentVariableName, "true"),
            (CliE2ETestHelpers.PolyglotImageEnvironmentVariableName, null),
            (CliE2ETestHelpers.RequirePolyglotImageEnvironmentVariableName, null),
            ("GITHUB_ACTIONS", "true"));
        var options = new DockerContainerOptions();

        CliE2ETestHelpers.ConfigureDockerContainerSource(options, "/repo", CliE2ETestHelpers.DockerfileVariant.PolyglotJava);

        Assert.Equal("aspire-cli-e2e-polyglot-java:prebuilt", options.Image);
        Assert.True(string.IsNullOrEmpty(options.DockerfilePath));
        Assert.True(string.IsNullOrEmpty(options.BuildContext));
    }

    [Fact]
    public void ConfigureDockerContainerSource_RequiresPolyglotJavaImageWhenConfigured()
    {
        using var environment = new EnvironmentVariableScope(
            (CliE2ETestHelpers.PolyglotJavaImageEnvironmentVariableName, null),
            (CliE2ETestHelpers.RequirePolyglotJavaImageEnvironmentVariableName, "true"),
            ("GITHUB_ACTIONS", "true"));
        var options = new DockerContainerOptions();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CliE2ETestHelpers.ConfigureDockerContainerSource(options, "/repo", CliE2ETestHelpers.DockerfileVariant.PolyglotJava));

        Assert.Contains(CliE2ETestHelpers.PolyglotJavaImageEnvironmentVariableName, exception.Message);
    }

    [Fact]
    public void ConfigureDockerContainerSource_IgnoresPolyglotJavaImageForPolyglotVariant()
    {
        using var environment = new EnvironmentVariableScope(
            (CliE2ETestHelpers.PolyglotJavaImageEnvironmentVariableName, "aspire-cli-e2e-polyglot-java:prebuilt"),
            (CliE2ETestHelpers.RequirePolyglotJavaImageEnvironmentVariableName, "true"),
            (CliE2ETestHelpers.PolyglotImageEnvironmentVariableName, null),
            (CliE2ETestHelpers.RequirePolyglotImageEnvironmentVariableName, null),
            ("GITHUB_ACTIONS", "true"));
        var options = new DockerContainerOptions();

        CliE2ETestHelpers.ConfigureDockerContainerSource(options, "/repo", CliE2ETestHelpers.DockerfileVariant.Polyglot);

        Assert.Equal(Path.Combine("/repo", "tests", "Shared", "Docker", "Dockerfile.e2e-polyglot-base"), options.DockerfilePath);
        Assert.Equal("/repo", options.BuildContext);
    }

    [Fact]
    public void ConfigureDockerContainerSource_FallsBackToDockerfileOutsideCI()
    {
        using var environment = new EnvironmentVariableScope(
            (CliE2ETestHelpers.DotNetImageEnvironmentVariableName, null),
            (CliE2ETestHelpers.RequireDotNetImageEnvironmentVariableName, null),
            ("GITHUB_ACTIONS", null));
        var options = new DockerContainerOptions();

        CliE2ETestHelpers.ConfigureDockerContainerSource(options, "/repo", CliE2ETestHelpers.DockerfileVariant.DotNet);

        Assert.Equal(Path.Combine("/repo", "tests", "Shared", "Docker", "Dockerfile.e2e"), options.DockerfilePath);
        Assert.Equal("/repo", options.BuildContext);
    }

    [Fact]
    public void ConfigureDockerContainerSource_RequiresDotNetImageWhenConfigured()
    {
        using var environment = new EnvironmentVariableScope(
            (CliE2ETestHelpers.DotNetImageEnvironmentVariableName, null),
            (CliE2ETestHelpers.RequireDotNetImageEnvironmentVariableName, "true"),
            ("GITHUB_ACTIONS", "true"));
        var options = new DockerContainerOptions();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CliE2ETestHelpers.ConfigureDockerContainerSource(options, "/repo", CliE2ETestHelpers.DockerfileVariant.DotNet));

        Assert.Contains(CliE2ETestHelpers.DotNetImageEnvironmentVariableName, exception.Message);
    }

    [Fact]
    public void ConfigureDockerContainerSource_FallsBackToDockerfileInCIWhenDotNetImageIsNotRequired()
    {
        using var environment = new EnvironmentVariableScope(
            (CliE2ETestHelpers.DotNetImageEnvironmentVariableName, null),
            (CliE2ETestHelpers.RequireDotNetImageEnvironmentVariableName, null),
            ("GITHUB_ACTIONS", "true"));
        var options = new DockerContainerOptions();

        CliE2ETestHelpers.ConfigureDockerContainerSource(options, "/repo", CliE2ETestHelpers.DockerfileVariant.DotNet);

        Assert.Equal(Path.Combine("/repo", "tests", "Shared", "Docker", "Dockerfile.e2e"), options.DockerfilePath);
        Assert.Equal("/repo", options.BuildContext);
    }

    [Fact]
    public void ConfigureDockerContainerSource_IgnoresDotNetImageForPolyglotVariant()
    {
        using var environment = new EnvironmentVariableScope(
            (CliE2ETestHelpers.DotNetImageEnvironmentVariableName, "aspire-cli-e2e-dotnet:prebuilt"),
            (CliE2ETestHelpers.RequireDotNetImageEnvironmentVariableName, "true"),
            (CliE2ETestHelpers.PolyglotImageEnvironmentVariableName, null),
            (CliE2ETestHelpers.RequirePolyglotImageEnvironmentVariableName, null),
            ("GITHUB_ACTIONS", "true"));
        var options = new DockerContainerOptions();

        CliE2ETestHelpers.ConfigureDockerContainerSource(options, "/repo", CliE2ETestHelpers.DockerfileVariant.Polyglot);

        Assert.Equal(Path.Combine("/repo", "tests", "Shared", "Docker", "Dockerfile.e2e-polyglot-base"), options.DockerfilePath);
        Assert.Equal("/repo", options.BuildContext);
    }

    [Fact]
    public void Detect_DotnetTool_WhenEnvironmentVariableIsSet()
    {
        using var environment = new EnvironmentVariableScope(
            ("ASPIRE_E2E_ARCHIVE", null),
            ("ASPIRE_E2E_DOTNET_TOOL_SOURCE", null),
            ("ASPIRE_E2E_DOTNET_TOOL", "true"),
            ("ASPIRE_E2E_QUALITY", null),
            ("ASPIRE_E2E_VERSION", null),
            ("ASPIRE_E2E_PREINSTALLED", null),
            ("GITHUB_PR_NUMBER", null),
            ("GITHUB_PR_HEAD_SHA", null),
            ("CI", null),
            ("GITHUB_ACTIONS", null));

        var strategy = CliInstallStrategy.Detect();

        Assert.Equal(CliInstallMode.DotnetTool, strategy.Mode);
        Assert.Null(strategy.Version);
        Assert.Null(strategy.NupkgSourcePath);
        Assert.False(strategy.IncludePrerelease);
    }

    [Fact]
    public void Detect_DotnetTool_WithVersion()
    {
        using var environment = new EnvironmentVariableScope(
            ("ASPIRE_E2E_ARCHIVE", null),
            ("ASPIRE_E2E_DOTNET_TOOL_SOURCE", null),
            ("ASPIRE_E2E_DOTNET_TOOL", "true"),
            ("ASPIRE_E2E_QUALITY", null),
            ("ASPIRE_E2E_VERSION", "9.5.0"),
            ("ASPIRE_E2E_PREINSTALLED", null),
            ("GITHUB_PR_NUMBER", null),
            ("GITHUB_PR_HEAD_SHA", null),
            ("CI", null),
            ("GITHUB_ACTIONS", null));

        var strategy = CliInstallStrategy.Detect();

        Assert.Equal(CliInstallMode.DotnetTool, strategy.Mode);
        Assert.Equal("9.5.0", strategy.Version);
        Assert.Null(strategy.NupkgSourcePath);
        Assert.False(strategy.IncludePrerelease);
    }

    [Fact]
    public void Detect_DotnetToolLocalSource_WithVersionAndPath()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-test-nupkg-");

        try
        {
            using var environment = new EnvironmentVariableScope(
                ("ASPIRE_E2E_ARCHIVE", null),
                ("ASPIRE_E2E_DOTNET_TOOL_SOURCE", tempDir.FullName),
                ("ASPIRE_E2E_DOTNET_TOOL", null),
                ("ASPIRE_E2E_QUALITY", null),
                ("ASPIRE_E2E_VERSION", "13.3.0-preview.1.25175.1"),
                ("ASPIRE_E2E_PREINSTALLED", null),
                ("GITHUB_PR_NUMBER", null),
                ("GITHUB_PR_HEAD_SHA", null),
                ("CI", null),
                ("GITHUB_ACTIONS", null));

            var strategy = CliInstallStrategy.Detect();

            Assert.Equal(CliInstallMode.DotnetTool, strategy.Mode);
            Assert.Equal("13.3.0-preview.1.25175.1", strategy.Version);
            Assert.Equal(tempDir.FullName, strategy.NupkgSourcePath);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Detect_DotnetToolLocalSource_InfersVersion()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-test-nupkg-");

        try
        {
            File.WriteAllText(Path.Combine(tempDir.FullName, "Aspire.Cli.13.3.0-preview.1.25175.1.nupkg"), "");
            File.WriteAllText(Path.Combine(tempDir.FullName, "Aspire.Cli.linux-x64.13.3.0-preview.1.25175.1.nupkg"), "");

            using var environment = new EnvironmentVariableScope(
                ("ASPIRE_E2E_ARCHIVE", null),
                ("ASPIRE_E2E_DOTNET_TOOL_SOURCE", tempDir.FullName),
                ("ASPIRE_E2E_DOTNET_TOOL", null),
                ("ASPIRE_E2E_QUALITY", null),
                ("ASPIRE_E2E_VERSION", null),
                ("ASPIRE_E2E_PREINSTALLED", null),
                ("GITHUB_PR_NUMBER", null),
                ("GITHUB_PR_HEAD_SHA", null),
                ("CI", null),
                ("GITHUB_ACTIONS", null));

            var strategy = CliInstallStrategy.Detect();

            Assert.Equal(CliInstallMode.DotnetTool, strategy.Mode);
            Assert.Equal("13.3.0-preview.1.25175.1", strategy.Version);
            Assert.Equal(tempDir.FullName, strategy.NupkgSourcePath);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Detect_DotnetTool_TakesPriorityOverQuality()
    {
        using var environment = new EnvironmentVariableScope(
            ("ASPIRE_E2E_ARCHIVE", null),
            ("ASPIRE_E2E_DOTNET_TOOL_SOURCE", null),
            ("ASPIRE_E2E_DOTNET_TOOL", "true"),
            ("ASPIRE_E2E_QUALITY", "dev"),
            ("ASPIRE_E2E_VERSION", null),
            ("ASPIRE_E2E_PREINSTALLED", null),
            ("GITHUB_PR_NUMBER", null),
            ("GITHUB_PR_HEAD_SHA", null),
            ("CI", null),
            ("GITHUB_ACTIONS", null));

        var strategy = CliInstallStrategy.Detect();

        Assert.Equal(CliInstallMode.DotnetTool, strategy.Mode);
        Assert.True(strategy.IncludePrerelease);
    }

    [Fact]
    public void Detect_DotnetTool_IncludesPrereleaseForStagingQuality()
    {
        using var environment = new EnvironmentVariableScope(
            ("ASPIRE_E2E_ARCHIVE", null),
            ("ASPIRE_E2E_DOTNET_TOOL_SOURCE", null),
            ("ASPIRE_E2E_DOTNET_TOOL", "true"),
            ("ASPIRE_E2E_QUALITY", "staging"),
            ("ASPIRE_E2E_VERSION", null),
            ("ASPIRE_E2E_PREINSTALLED", null),
            ("GITHUB_PR_NUMBER", null),
            ("GITHUB_PR_HEAD_SHA", null),
            ("CI", null),
            ("GITHUB_ACTIONS", null));

        var strategy = CliInstallStrategy.Detect();

        Assert.Equal(CliInstallMode.DotnetTool, strategy.Mode);
        Assert.True(strategy.IncludePrerelease);
    }

    [Fact]
    public void Detect_DotnetTool_DoesNotIncludePrereleaseForReleaseQuality()
    {
        using var environment = new EnvironmentVariableScope(
            ("ASPIRE_E2E_ARCHIVE", null),
            ("ASPIRE_E2E_DOTNET_TOOL_SOURCE", null),
            ("ASPIRE_E2E_DOTNET_TOOL", "true"),
            ("ASPIRE_E2E_QUALITY", "release"),
            ("ASPIRE_E2E_VERSION", null),
            ("ASPIRE_E2E_PREINSTALLED", null),
            ("GITHUB_PR_NUMBER", null),
            ("GITHUB_PR_HEAD_SHA", null),
            ("CI", null),
            ("GITHUB_ACTIONS", null));

        var strategy = CliInstallStrategy.Detect();

        Assert.Equal(CliInstallMode.DotnetTool, strategy.Mode);
        Assert.False(strategy.IncludePrerelease);
    }

    [Fact]
    public void Detect_DotnetTool_DoesNotIncludePrereleaseWhenVersionIsSpecified()
    {
        using var environment = new EnvironmentVariableScope(
            ("ASPIRE_E2E_ARCHIVE", null),
            ("ASPIRE_E2E_DOTNET_TOOL_SOURCE", null),
            ("ASPIRE_E2E_DOTNET_TOOL", "true"),
            ("ASPIRE_E2E_QUALITY", "dev"),
            ("ASPIRE_E2E_VERSION", "13.3.0-preview.1.25175.1"),
            ("ASPIRE_E2E_PREINSTALLED", null),
            ("GITHUB_PR_NUMBER", null),
            ("GITHUB_PR_HEAD_SHA", null),
            ("CI", null),
            ("GITHUB_ACTIONS", null));

        var strategy = CliInstallStrategy.Detect();

        Assert.Equal(CliInstallMode.DotnetTool, strategy.Mode);
        Assert.Equal("13.3.0-preview.1.25175.1", strategy.Version);
        Assert.False(strategy.IncludePrerelease);
    }

    [Fact]
    public void DotnetToolSmokeTests_ThrowsWhenPublishedFeedFallbackHasNoSelector()
    {
        using var environment = new EnvironmentVariableScope(
            ("ASPIRE_E2E_DOTNET_TOOL_SOURCE", null),
            ("BUILT_NUGETS_PATH", null),
            ("ASPIRE_E2E_VERSION", null),
            ("ASPIRE_E2E_QUALITY", null));

        var exception = Assert.Throws<InvalidOperationException>(DotnetToolSmokeTests.GetDotnetToolStrategy);

        Assert.Contains("ASPIRE_E2E_QUALITY", exception.Message);
    }

    [Fact]
    public void DotnetToolSmokeTests_UsesPublishedFeedWhenQualityIsSet()
    {
        using var environment = new EnvironmentVariableScope(
            ("ASPIRE_E2E_DOTNET_TOOL_SOURCE", null),
            ("BUILT_NUGETS_PATH", null),
            ("ASPIRE_E2E_VERSION", null),
            ("ASPIRE_E2E_QUALITY", "staging"));

        var strategy = DotnetToolSmokeTests.GetDotnetToolStrategy();

        Assert.Equal(CliInstallMode.DotnetTool, strategy.Mode);
        Assert.True(strategy.IncludePrerelease);
        Assert.Null(strategy.Version);
    }

    [Fact]
    public void DotnetToolSmokeTests_UsesPublishedFeedWhenVersionIsSet()
    {
        using var environment = new EnvironmentVariableScope(
            ("ASPIRE_E2E_DOTNET_TOOL_SOURCE", null),
            ("BUILT_NUGETS_PATH", null),
            ("ASPIRE_E2E_VERSION", "13.3.0"),
            ("ASPIRE_E2E_QUALITY", null));

        var strategy = DotnetToolSmokeTests.GetDotnetToolStrategy();

        Assert.Equal(CliInstallMode.DotnetTool, strategy.Mode);
        Assert.Equal("13.3.0", strategy.Version);
        Assert.False(strategy.IncludePrerelease);
    }

    [Fact]
    public void ConfigureContainer_MountsNupkgSourceForDotnetToolLocalSource()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-test-nupkg-");

        try
        {
            var strategy = CliInstallStrategy.FromDotnetToolLocalSource(tempDir.FullName, "13.3.0");
            var options = new DockerContainerOptions();

            strategy.ConfigureContainer(options);

            Assert.Contains(options.Volumes, v => v.Contains("/tmp/aspire-nupkg-source:ro"));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ConfigureContainer_NoVolumeForDotnetToolPublishedFeed()
    {
        var strategy = CliInstallStrategy.FromDotnetTool("9.5.0");
        var options = new DockerContainerOptions();

        strategy.ConfigureContainer(options);

        Assert.DoesNotContain(options.Volumes, v => v.Contains("aspire-nupkg-source"));
    }

    [Fact]
    public void GetDotnetToolInstallCommandInDocker_WithVersionOnly()
    {
        var strategy = CliInstallStrategy.FromDotnetTool("9.5.0");
        var command = AspireCliShellCommandHelpers.GetDotnetToolInstallCommandInDocker(strategy);

        Assert.Equal("dotnet tool install --global Aspire.Cli --version 9.5.0", command);
    }

    [Fact]
    public void GetDotnetToolInstallCommandInDocker_WithPrereleaseVersion()
    {
        var strategy = CliInstallStrategy.FromDotnetTool("13.3.0-preview.1.25175.1");
        var command = AspireCliShellCommandHelpers.GetDotnetToolInstallCommandInDocker(strategy);

        Assert.Equal("dotnet tool install --global Aspire.Cli --version 13.3.0-preview.1.25175.1 --configfile '/opt/aspire-scripts/NuGet.config'", command);
    }

    [Fact]
    public void GetDotnetToolInstallCommandInDocker_WithLocalSource()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-test-nupkg-");

        try
        {
            var strategy = CliInstallStrategy.FromDotnetToolLocalSource(tempDir.FullName, "13.3.0");
            var command = AspireCliShellCommandHelpers.GetDotnetToolInstallCommandInDocker(strategy);

            Assert.Equal("dotnet tool install --global Aspire.Cli --version 13.3.0 --add-source '/tmp/aspire-nupkg-source'", command);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetDotnetToolInstallCommandInDocker_WithoutVersion()
    {
        var strategy = CliInstallStrategy.FromDotnetTool();
        var command = AspireCliShellCommandHelpers.GetDotnetToolInstallCommandInDocker(strategy);

        Assert.Equal("dotnet tool install --global Aspire.Cli", command);
    }

    [Fact]
    public void GetDotnetToolInstallCommandInDocker_WithPrerelease()
    {
        var strategy = CliInstallStrategy.FromDotnetTool(includePrerelease: true);
        var command = AspireCliShellCommandHelpers.GetDotnetToolInstallCommandInDocker(strategy);

        Assert.Equal("dotnet tool install --global Aspire.Cli --prerelease --configfile '/opt/aspire-scripts/NuGet.config'", command);
    }

    [Fact]
    public void Detect_ReturnsLocalArchive_WhenArchiveDirIsSetInCIWithoutPrMetadata()
    {
        var tempDir = Directory.CreateTempSubdirectory("cli-archives-test");
        try
        {
            using var environment = new EnvironmentVariableScope(
                ("ASPIRE_E2E_ARCHIVE", null),
                ("ASPIRE_E2E_DOTNET_TOOL_SOURCE", null),
                ("ASPIRE_E2E_DOTNET_TOOL", null),
                ("ASPIRE_E2E_QUALITY", null),
                ("ASPIRE_E2E_VERSION", null),
                ("ASPIRE_E2E_PREINSTALLED", null),
                ("GITHUB_PR_NUMBER", null),
                ("GITHUB_PR_HEAD_SHA", null),
                (CliE2ETestHelpers.CliArchiveDirEnvironmentVariableName, tempDir.FullName),
                ("CI", "true"),
                ("GITHUB_ACTIONS", "true"));

            var strategy = CliInstallStrategy.Detect();

            Assert.Equal(CliInstallMode.LocalArchive, strategy.Mode);
            Assert.Equal(tempDir.FullName, strategy.ArchiveDir);
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    [Fact]
    public void FromLocalArchive_ThrowsWhenDirectoryDoesNotExist()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            CliInstallStrategy.FromLocalArchive("/nonexistent/cli-archives-path"));
    }

    [Fact]
    public void FromLocalArchive_ExtractsExpectedVersionFromNupkg()
    {
        var tempDir = Directory.CreateTempSubdirectory("cli-archives-test");
        try
        {
            File.WriteAllText(Path.Combine(tempDir.FullName, "Aspire.Cli.13.3.0-preview.1.12345.1.nupkg"), "");

            var strategy = CliInstallStrategy.FromLocalArchive(tempDir.FullName);

            Assert.Equal("13.3.0-preview.1.12345.1", strategy.ExpectedVersion);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void FromLocalArchive_IgnoresRidSpecificCliNupkg()
    {
        var tempDir = Directory.CreateTempSubdirectory("cli-archives-test");
        try
        {
            File.WriteAllText(Path.Combine(tempDir.FullName, "Aspire.Cli.linux-x64.13.3.0.nupkg"), "");

            var strategy = CliInstallStrategy.FromLocalArchive(tempDir.FullName);

            Assert.Null(strategy.ExpectedVersion);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void FromLocalArchive_ThrowsWhenMultiplePointerCliNupkgsArePresent()
    {
        var tempDir = Directory.CreateTempSubdirectory("cli-archives-test");
        try
        {
            File.WriteAllText(Path.Combine(tempDir.FullName, "Aspire.Cli.13.3.0.nupkg"), "");
            File.WriteAllText(Path.Combine(tempDir.FullName, "Aspire.Cli.13.4.0.nupkg"), "");

            var exception = Assert.Throws<InvalidOperationException>(() => CliInstallStrategy.FromLocalArchive(tempDir.FullName));

            Assert.Contains("Found 2 Aspire.Cli pointer nupkg files", exception.Message);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void FromDotnetToolLocalSource_ThrowsWhenOnlyRidSpecificPackagesArePresent()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-test-nupkg-");

        try
        {
            File.WriteAllText(Path.Combine(tempDir.FullName, "Aspire.Cli.linux-x64.13.3.0.nupkg"), "");

            var exception = Assert.Throws<InvalidOperationException>(() => CliInstallStrategy.FromDotnetToolLocalSource(tempDir.FullName));

            Assert.Contains("No Aspire.Cli tool nupkg found", exception.Message);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Detect_ReturnsDotnetToolLocalSource_WhenBothToolSourceAndArchiveDirAreSet()
    {
        var nupkgDir = Directory.CreateTempSubdirectory("aspire-test-nupkg-");
        var archiveDir = Directory.CreateTempSubdirectory("cli-archives-test");
        try
        {
            using var environment = new EnvironmentVariableScope(
                ("ASPIRE_E2E_ARCHIVE", null),
                ("ASPIRE_E2E_DOTNET_TOOL_SOURCE", nupkgDir.FullName),
                ("ASPIRE_E2E_DOTNET_TOOL", null),
                ("ASPIRE_E2E_QUALITY", null),
                ("ASPIRE_E2E_VERSION", "10.0.0-dev.12345.1"),
                ("ASPIRE_E2E_PREINSTALLED", null),
                ("GITHUB_PR_NUMBER", null),
                ("GITHUB_PR_HEAD_SHA", null),
                (CliE2ETestHelpers.CliArchiveDirEnvironmentVariableName, archiveDir.FullName),
                ("CI", null),
                ("GITHUB_ACTIONS", null));

            var strategy = CliInstallStrategy.Detect();

            Assert.Equal(CliInstallMode.DotnetTool, strategy.Mode);
            Assert.Equal(nupkgDir.FullName, strategy.NupkgSourcePath);
            Assert.Equal("10.0.0-dev.12345.1", strategy.Version);
        }
        finally
        {
            nupkgDir.Delete(recursive: true);
            archiveDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Detect_ReturnsDotnetTool_WhenBothToolFlagAndPrMetadataAreSet()
    {
        using var environment = new EnvironmentVariableScope(
            ("ASPIRE_E2E_ARCHIVE", null),
            ("ASPIRE_E2E_DOTNET_TOOL_SOURCE", null),
            ("ASPIRE_E2E_DOTNET_TOOL", "true"),
            ("ASPIRE_E2E_QUALITY", null),
            ("ASPIRE_E2E_VERSION", null),
            ("ASPIRE_E2E_PREINSTALLED", null),
            ("GITHUB_PR_NUMBER", "16131"),
            ("GITHUB_PR_HEAD_SHA", "abc123"),
            (CliE2ETestHelpers.CliArchiveDirEnvironmentVariableName, null),
            ("CI", null),
            ("GITHUB_ACTIONS", null));

        var strategy = CliInstallStrategy.Detect();

        Assert.Equal(CliInstallMode.DotnetTool, strategy.Mode);
    }

    [Fact]
    public void FromDotnetToolLocalSource_ThrowsWhenDirectoryDoesNotExist()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            CliInstallStrategy.FromDotnetToolLocalSource("/nonexistent/nupkg-path", "1.0.0"));
    }

    [Fact]
    public void FromPullRequest_ThrowsWhenPrMetadataIsMissing()
    {
        using var environment = new EnvironmentVariableScope(
            ("GITHUB_PR_NUMBER", null),
            ("GITHUB_PR_HEAD_SHA", null));

        Assert.Throws<InvalidOperationException>(CliInstallStrategy.FromPullRequest);
    }

    [Fact]
    public void TryGetPullRequestHeadSha_ReturnsFalseOutsidePrContext()
    {
        using var environment = new EnvironmentVariableScope(
            ("GITHUB_PR_NUMBER", null),
            ("GITHUB_PR_HEAD_SHA", null),
            ("GITHUB_EVENT_NAME", null),
            ("GITHUB_SHA", "52669a7cac3d4f10c6269909fc38e77124ed177c"));

        var result = CliE2ETestHelpers.TryGetPullRequestHeadSha(out var commitSha);

        Assert.False(result);
        Assert.Equal(string.Empty, commitSha);
    }

    [Fact]
    public void TryGetPullRequestHeadSha_ThrowsInPrContextWithoutHeadSha()
    {
        using var environment = new EnvironmentVariableScope(
            ("GITHUB_PR_NUMBER", "16131"),
            ("GITHUB_PR_HEAD_SHA", null),
            ("GITHUB_EVENT_NAME", null));

        var exception = Assert.Throws<InvalidOperationException>(() => CliE2ETestHelpers.TryGetPullRequestHeadSha(out _));

        Assert.Contains("GITHUB_PR_HEAD_SHA must be set", exception.Message);
    }

    [Fact]
    public void TryGetPullRequestHeadSha_ThrowsInPrContextWithInvalidHeadSha()
    {
        using var environment = new EnvironmentVariableScope(
            ("GITHUB_PR_NUMBER", null),
            ("GITHUB_PR_HEAD_SHA", "abc123"),
            ("GITHUB_EVENT_NAME", "pull_request"));

        var exception = Assert.Throws<InvalidOperationException>(() => CliE2ETestHelpers.TryGetPullRequestHeadSha(out _));

        Assert.Contains("40-character commit SHA", exception.Message);
    }

    [Fact]
    public void TryGetPullRequestHeadSha_ReturnsHeadShaInPrContext()
    {
        const string expectedSha = "52669a7cac3d4f10c6269909fc38e77124ed177c";
        using var environment = new EnvironmentVariableScope(
            ("GITHUB_PR_NUMBER", "16131"),
            ("GITHUB_PR_HEAD_SHA", expectedSha),
            ("GITHUB_EVENT_NAME", null));

        var result = CliE2ETestHelpers.TryGetPullRequestHeadSha(out var commitSha);

        Assert.True(result);
        Assert.Equal(expectedSha, commitSha);
    }

    [Fact]
    public void Detect_ReturnsPreinstalled_WhenPreinstalledIsSet()
    {
        using var environment = new EnvironmentVariableScope(
            ("ASPIRE_E2E_ARCHIVE", null),
            ("ASPIRE_E2E_DOTNET_TOOL_SOURCE", null),
            ("ASPIRE_E2E_DOTNET_TOOL", null),
            ("ASPIRE_E2E_QUALITY", null),
            ("ASPIRE_E2E_VERSION", null),
            ("ASPIRE_E2E_PREINSTALLED", "true"),
            ("GITHUB_PR_NUMBER", null),
            ("GITHUB_PR_HEAD_SHA", null),
            (CliE2ETestHelpers.CliArchiveDirEnvironmentVariableName, null),
            ("CI", null),
            ("GITHUB_ACTIONS", null));

        var strategy = CliInstallStrategy.Detect();

        Assert.Equal(CliInstallMode.Preinstalled, strategy.Mode);
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _originalValues;

        public EnvironmentVariableScope(params (string Name, string? Value)[] variables)
        {
            _originalValues = variables.ToDictionary(
                variable => variable.Name,
                variable => Environment.GetEnvironmentVariable(variable.Name));

            foreach (var (name, value) in variables)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _originalValues)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliInstallEnvironmentCollection
{
    public const string Name = nameof(CliInstallEnvironmentCollection);
}
