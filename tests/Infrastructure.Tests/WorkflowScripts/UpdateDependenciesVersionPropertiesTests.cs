// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for .github/workflows/update-dependencies-version-properties.js, which puts
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

    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TemporaryWorkspace _workspace;
    private readonly string _repoRoot;
    private readonly ITestOutputHelper _output;

    public UpdateDependenciesVersionPropertiesTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _repoRoot = RepoRoot.Path;
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    [RequiresTools(["node"])]
    public async Task MovesInlinedExternalVersionsIntoVersionsProps()
    {
        var updatedPackagesProps = OriginalPackagesProps
            .Replace("$(OpenTelemetryExtensionsHostingVersion)", "1.21.0")
            .Replace("$(OpenTelemetryInstrumentationHttpVersion)", "1.21.0")
            .Replace("\"1.18.0\"", "\"1.21.0\"");

        var result = await InvokeAsync(updatedPackagesProps, VersionsProps);

        Assert.Equal(
            OriginalPackagesProps.Replace("\"1.18.0\"", "\"1.21.0\""),
            result.PackagesProps);
        Assert.Equal(
            VersionsProps
                .Replace("<OpenTelemetryExtensionsHostingVersion>1.18.0<", "<OpenTelemetryExtensionsHostingVersion>1.21.0<")
                .Replace("<OpenTelemetryInstrumentationHttpVersion>1.18.0<", "<OpenTelemetryInstrumentationHttpVersion>1.21.0<"),
            result.VersionsProps);
        Assert.Equal(
            [
                new PropertyChange("OpenTelemetry.Extensions.Hosting", "OpenTelemetryExtensionsHostingVersion", "1.21.0", null),
                new PropertyChange("OpenTelemetry.Instrumentation.Http", "OpenTelemetryInstrumentationHttpVersion", "1.21.0", null),
            ],
            result.Updated);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RestoresReferencesWithoutUpdatingVersionsOwnedByOtherProcesses()
    {
        var updatedPackagesProps = OriginalPackagesProps
            .Replace("$(MicrosoftDotNetBuildTasksArchivesVersion)", "11.0.0-beta.2")
            .Replace("$(MicrosoftSignedWixVersion)", "6.0.0")
            .Replace("$(SystemTextJsonNet10Version)", "11.0.0");

        var result = await InvokeAsync(updatedPackagesProps, VersionsProps);

        Assert.Equal(OriginalPackagesProps, result.PackagesProps);
        Assert.Equal(VersionsProps, result.VersionsProps);
        Assert.Empty(result.Updated);
        Assert.Equal(
            [
                new PropertyChange("Microsoft.DotNet.Build.Tasks.Archives", "MicrosoftDotNetBuildTasksArchivesVersion", "11.0.0-beta.2", "the package is managed by Darc in eng/Version.Details.xml"),
                new PropertyChange("Microsoft.Signed.Wix", "MicrosoftSignedWixVersion", "6.0.0", "the property is not defined in eng/Versions.props"),
                new PropertyChange("System.Text.Json", "SystemTextJsonNet10Version", "11.0.0", "the package is outside the external dependencies ItemGroup"),
            ],
            result.Skipped);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task KeepsSharedPropertyWhenPackagesResolveToDifferentVersions()
    {
        var originalPackagesProps = OriginalPackagesProps.Replace("$(OpenTelemetryInstrumentationHttpVersion)", "$(OpenTelemetryExtensionsHostingVersion)");
        var updatedPackagesProps = OriginalPackagesProps
            .Replace("$(OpenTelemetryExtensionsHostingVersion)", "1.21.0")
            .Replace("$(OpenTelemetryInstrumentationHttpVersion)", "1.20.0");

        var result = await InvokeAsync(updatedPackagesProps, VersionsProps, originalPackagesProps);

        Assert.Equal(originalPackagesProps, result.PackagesProps);
        Assert.Equal(VersionsProps, result.VersionsProps);
        Assert.Empty(result.Updated);
        Assert.Equal(
            [
                new PropertyChange("OpenTelemetry.Extensions.Hosting", "OpenTelemetryExtensionsHostingVersion", "1.21.0", "packages sharing the property resolved to different versions (1.20.0, 1.21.0)"),
                new PropertyChange("OpenTelemetry.Instrumentation.Http", "OpenTelemetryExtensionsHostingVersion", "1.20.0", "packages sharing the property resolved to different versions (1.20.0, 1.21.0)"),
            ],
            result.Skipped);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CommandRewritesFilesInWorkingDirectory()
    {
        var originalPath = Path.Combine(_workspace.Path, "original.props");
        await File.WriteAllTextAsync(originalPath, OriginalPackagesProps);
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "Directory.Packages.props"), OriginalPackagesProps.Replace("$(OpenTelemetryInstrumentationHttpVersion)", "1.21.0"));
        Directory.CreateDirectory(Path.Combine(_workspace.Path, "eng"));
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "eng", "Versions.props"), VersionsProps);
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "eng", "Version.Details.xml"), VersionDetailsXml);

        using var command = new NodeCommand(_output, "update-dependencies-version-properties");
        command.WithWorkingDirectory(_workspace.Path);
        var result = await command.ExecuteScriptAsync(
            Path.Combine(_repoRoot, ".github", "workflows", "update-dependencies-version-properties.js"),
            originalPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(OriginalPackagesProps, await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "Directory.Packages.props")));
        Assert.Equal(
            VersionsProps.Replace("<OpenTelemetryInstrumentationHttpVersion>1.18.0<", "<OpenTelemetryInstrumentationHttpVersion>1.21.0<"),
            await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "eng", "Versions.props")));
    }

    private async Task<RestoreResult> InvokeAsync(string updatedPackagesProps, string versionsProps, string originalPackagesProps = OriginalPackagesProps)
    {
        var requestPath = Path.Combine(_workspace.Path, $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            requestPath,
            JsonSerializer.Serialize(new { originalPackagesProps, updatedPackagesProps, versionsProps, versionDetailsXml = VersionDetailsXml }, s_jsonOptions));

        using var command = new NodeCommand(_output, "update-dependencies-version-properties");
        command.WithWorkingDirectory(_repoRoot);
        var result = await command.ExecuteScriptAsync(
            Path.Combine(_repoRoot, "tests", "Infrastructure.Tests", "WorkflowScripts", "update-dependencies-version-properties.harness.js"),
            requestPath);
        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<HarnessResponse>(result.Output, s_jsonOptions);
        Assert.NotNull(response);
        Assert.Null(response.Error);
        return response.Result!;
    }

    private sealed record HarnessResponse(RestoreResult? Result, string? Error);

    private sealed record RestoreResult(string PackagesProps, string VersionsProps, PropertyChange[] Updated, PropertyChange[] Skipped);

    private sealed record PropertyChange(string PackageId, string Property, string Version, string? Reason);
}
