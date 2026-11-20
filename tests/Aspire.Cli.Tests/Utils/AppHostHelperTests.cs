// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Cli.Tests.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class AppHostHelperTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("10.0.0", true)]
    [InlineData("9.2.0", true)]
    [InlineData("9.3.0", true)]
    [InlineData("13.0.0-preview.1", true)]
    [InlineData("17.*", true)]
    [InlineData("17.1.*", true)]
    [InlineData("9.2.*", true)]
    [InlineData("9.1.0", false)]
    [InlineData("9.1.*", false)]
    [InlineData("8.0.0", false)]
    [InlineData("1.0.0", false)]
    public async Task CheckAppHostCompatibility_VersionCheck(string aspireVersion, bool expectedCompatible)
    {
        var runner = new TestDotNetCliRunner
        {
            GetAppHostInformationAsyncCallback = (_, _, _) => (0, true, aspireVersion)
        };
        var interactionService = new TestInteractionService();
        var telemetry = TestTelemetryHelper.CreateInitializedTelemetry();
        var projectFile = new FileInfo(Path.Combine(Path.GetTempPath(), "test.csproj"));
        var workingDirectory = new DirectoryInfo(Path.GetTempPath());

        var (isCompatible, returnedVersion) = await AppHostHelper.CheckAppHostCompatibilityAsync(
            runner, interactionService, projectFile, telemetry, workingDirectory, "test.log", CancellationToken.None);

        Assert.Equal(expectedCompatible, isCompatible);
        Assert.Equal(aspireVersion, returnedVersion);
    }

    [Theory]
    [InlineData("17.*", true)]
    [InlineData("17.1.*-*", true)]
    [InlineData("*", true)]
    [InlineData("17.1.0", false)]
    [InlineData("17.1.0-preview.1", false)]
    [InlineData("[17.0.0, )", false)]
    [InlineData("not-a-version", false)]
    [InlineData(null, false)]
    public void IsFloatingVersion_IdentifiesNuGetFloatingRanges(string? version, bool expected)
    {
        Assert.Equal(expected, AppHostHelper.IsFloatingVersion(version));
    }

    [Theory]
    [InlineData("*", true)]
    [InlineData("*-*", true)]
    [InlineData("9.*", true)]
    [InlineData("9.*-*", true)]
    [InlineData("9.1.*", false)]
    [InlineData("9.1.*-*", false)]
    [InlineData("9.1.0-*", false)]
    [InlineData("9.2.*", false)]
    [InlineData("13.*", false)]
    [InlineData("9.1.0", false)]
    [InlineData(null, false)]
    public void IsFloatingVersionSpanningMinimum_OnlyMatchesRangesCrossingTheMinimum(string? version, bool expected)
    {
        Assert.Equal(expected, AppHostHelper.IsFloatingVersionSpanningMinimum(version, AppHostHelper.MinimumSupportedAspireHostingVersion));
    }

    [Fact]
    public void TryGetBuiltAspireHostingVersion_ReadsHostingAssemblyNextToTargetPath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceAssembly = typeof(AppHostHelper).Assembly;
        File.Copy(sourceAssembly.Location, Path.Combine(workspace.WorkspaceRoot.FullName, "Aspire.Hosting.dll"));
        var informationalVersion = sourceAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        var version = AppHostHelper.TryGetBuiltAspireHostingVersion(Path.Combine(workspace.WorkspaceRoot.FullName, "App.dll"));

        Assert.Equal(informationalVersion.Split('+')[0], version);
    }

    [Fact]
    public void TryGetBuiltAspireHostingVersion_ReturnsNullWithoutBuildOutput()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        Assert.Null(AppHostHelper.TryGetBuiltAspireHostingVersion(Path.Combine(workspace.WorkspaceRoot.FullName, "App.dll")));
        Assert.Null(AppHostHelper.TryGetBuiltAspireHostingVersion(null));
    }
}
