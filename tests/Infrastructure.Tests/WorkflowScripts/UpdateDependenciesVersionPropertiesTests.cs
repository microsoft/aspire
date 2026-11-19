// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for .github/workflows/update-dependencies-version-properties.cs, which puts
/// $(...Version) references back after dotnet-outdated inlines them in Directory.Packages.props.
/// </summary>
public sealed class UpdateDependenciesVersionPropertiesTests : IDisposable
{
    private const string OriginalPackagesProps = """
        <Project>
          <ItemGroup>
            <PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="$(OpenTelemetryExtensionsHostingVersion)" />
            <PackageVersion Include="OpenTelemetry.Instrumentation.Http" Version="$(OpenTelemetryInstrumentationHttpVersion)" />
            <PackageVersion Include="OpenTelemetry.Exporter.Console" Version="1.18.0" />
            <PackageVersion Include="Microsoft.DotNet.Build.Tasks.Archives" Version="$(MicrosoftDotNetBuildTasksArchivesVersion)" />
            <PackageVersion Include="Microsoft.Signed.Wix" Version="$(MicrosoftSignedWixVersion)" />
          </ItemGroup>
          <ItemGroup>
            <PackageVersion Include="System.Text.Json" Version="$(SystemTextJsonNet10Version)" />
          </ItemGroup>
          <ItemGroup Condition="'$(TargetFramework)' == 'net11.0'">
            <PackageVersion Update="System.Text.Json" Version="$(SystemTextJsonVersion)" />
          </ItemGroup>
        </Project>
        """;

    private const string VersionsProps = """
        <Project>
          <PropertyGroup>
            <MicrosoftDotNetBuildTasksArchivesVersion>10.0.0-beta.1</MicrosoftDotNetBuildTasksArchivesVersion>
            <OpenTelemetryExtensionsHostingVersion>1.18.0</OpenTelemetryExtensionsHostingVersion>
            <OpenTelemetryInstrumentationHttpVersion>1.18.0</OpenTelemetryInstrumentationHttpVersion>
            <SystemTextJsonNet10Version>10.0.15</SystemTextJsonNet10Version>
          </PropertyGroup>
        </Project>
        """;

    private const string VersionDetailsXml = """
        <Dependencies>
          <ToolsetDependencies>
            <Dependency Name="Microsoft.DotNet.Build.Tasks.Archives" Version="10.0.0-beta.1">
              <Uri>https://github.com/dotnet/arcade</Uri>
            </Dependency>
          </ToolsetDependencies>
        </Dependencies>
        """;

    private static readonly string s_appPath = Path.Combine(RepoRoot.Path, ".github", "workflows", "update-dependencies-version-properties.cs");

    private readonly TemporaryWorkspace _workspace;
    private readonly ITestOutputHelper _output;

    public UpdateDependenciesVersionPropertiesTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task MovesInlinedExternalVersionsIntoVersionsProps()
    {
        var updatedPackagesProps = OriginalPackagesProps
            .Replace("$(OpenTelemetryExtensionsHostingVersion)", "1.21.0")
            .Replace("$(OpenTelemetryInstrumentationHttpVersion)", "1.21.0")
            .Replace("\"1.18.0\"", "\"1.21.0\"");

        var result = await RunAsync(OriginalPackagesProps, updatedPackagesProps);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(OriginalPackagesProps.Replace("\"1.18.0\"", "\"1.21.0\""), ReadWorkspaceFile("Directory.Packages.props"));
        Assert.Equal(
            VersionsProps
                .Replace("<OpenTelemetryExtensionsHostingVersion>1.18.0<", "<OpenTelemetryExtensionsHostingVersion>1.21.0<")
                .Replace("<OpenTelemetryInstrumentationHttpVersion>1.18.0<", "<OpenTelemetryInstrumentationHttpVersion>1.21.0<"),
            ReadWorkspaceFile("eng", "Versions.props"));
        Assert.Equal(
            [
                "Moved OpenTelemetry.Extensions.Hosting 1.21.0 into $(OpenTelemetryExtensionsHostingVersion) in eng/Versions.props.",
                "Moved OpenTelemetry.Instrumentation.Http 1.21.0 into $(OpenTelemetryInstrumentationHttpVersion) in eng/Versions.props.",
            ],
            OutputLines(result));
    }

    [Fact]
    public async Task RestoresReferencesWithoutUpdatingVersionsOwnedByOtherProcesses()
    {
        var updatedPackagesProps = OriginalPackagesProps
            .Replace("$(MicrosoftDotNetBuildTasksArchivesVersion)", "11.0.0-beta.2")
            .Replace("$(MicrosoftSignedWixVersion)", "6.0.0")
            .Replace("$(SystemTextJsonNet10Version)", "11.0.0");

        var result = await RunAsync(OriginalPackagesProps, updatedPackagesProps);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(OriginalPackagesProps, ReadWorkspaceFile("Directory.Packages.props"));
        Assert.Equal(VersionsProps, ReadWorkspaceFile("eng", "Versions.props"));
        Assert.Equal(
            [
                "::warning::Kept $(MicrosoftDotNetBuildTasksArchivesVersion) unchanged instead of updating Microsoft.DotNet.Build.Tasks.Archives to 11.0.0-beta.2: the package is managed by Darc in eng/Version.Details.xml.",
                "::warning::Kept $(MicrosoftSignedWixVersion) unchanged instead of updating Microsoft.Signed.Wix to 6.0.0: the property is not defined in eng/Versions.props.",
                "::warning::Kept $(SystemTextJsonNet10Version) unchanged instead of updating System.Text.Json to 11.0.0: the package is outside the external dependencies ItemGroup.",
            ],
            OutputLines(result));
    }

    [Fact]
    public async Task KeepsSharedPropertyWhenPackagesResolveToDifferentVersions()
    {
        var originalPackagesProps = OriginalPackagesProps.Replace("$(OpenTelemetryInstrumentationHttpVersion)", "$(OpenTelemetryExtensionsHostingVersion)");
        var updatedPackagesProps = OriginalPackagesProps
            .Replace("$(OpenTelemetryExtensionsHostingVersion)", "1.21.0")
            .Replace("$(OpenTelemetryInstrumentationHttpVersion)", "1.20.0");

        var result = await RunAsync(originalPackagesProps, updatedPackagesProps);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(originalPackagesProps, ReadWorkspaceFile("Directory.Packages.props"));
        Assert.Equal(VersionsProps, ReadWorkspaceFile("eng", "Versions.props"));
        Assert.Equal(
            [
                "::warning::Kept $(OpenTelemetryExtensionsHostingVersion) unchanged instead of updating OpenTelemetry.Extensions.Hosting to 1.21.0: packages sharing the property resolved to different versions (1.20.0, 1.21.0).",
                "::warning::Kept $(OpenTelemetryExtensionsHostingVersion) unchanged instead of updating OpenTelemetry.Instrumentation.Http to 1.20.0: packages sharing the property resolved to different versions (1.20.0, 1.21.0).",
            ],
            OutputLines(result));
    }

    private async Task<ProcessResult> RunAsync(string originalPackagesProps, string updatedPackagesProps)
    {
        var originalPath = Path.Combine(_workspace.Path, "original.props");
        await File.WriteAllTextAsync(originalPath, originalPackagesProps);
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "Directory.Packages.props"), updatedPackagesProps);
        Directory.CreateDirectory(Path.Combine(_workspace.Path, "eng"));
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "eng", "Versions.props"), VersionsProps);
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "eng", "Version.Details.xml"), VersionDetailsXml);

        // Use the repository SDK that runs this test so the file-based app builds with the pinned SDK.
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        return await TestProcessRunner.RunAsync(_output, dotnet, ["run", s_appPath, "--", originalPath], _workspace.Path);
    }

    private string ReadWorkspaceFile(params string[] relativePath) =>
        File.ReadAllText(Path.Combine([_workspace.Path, .. relativePath]));

    private static string[] OutputLines(ProcessResult result) =>
        result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
}
