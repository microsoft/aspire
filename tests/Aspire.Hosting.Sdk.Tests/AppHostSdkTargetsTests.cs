// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Reflection;
using System.Security;
using Xunit;

namespace Aspire.Hosting.Sdk.Tests;

public class AppHostSdkTargetsTests
{
    private static readonly string[] s_supportedRids =
    [
        "win-x64",
        "win-arm64",
        "linux-x64",
        "linux-arm64",
        "linux-musl-x64",
        "osx-x64",
        "osx-arm64"
    ];

    [Fact]
    public async Task AddReferenceToDashboardAndDcpUsesSdkRidSelectionTask()
    {
        var packageReferences = await RunAddReferenceToDashboardAndDcpAsync(extraProjectXml: null);

        Assert.Contains("UseSdkPickBestRid=true", packageReferences);
        Assert.Contains("RunRidToolFallback=false", packageReferences);
        AssertDashboardAndOrchestrationReferences(packageReferences);
    }

    [Fact]
    public async Task AddReferenceToDashboardAndDcpFallsBackToRuntimeIdentifierToolForOlderSdks()
    {
        // Force the pre-.NET 10 code path by disabling the in-proc PickBestRid task and pointing the
        // Exec call at the locally-built Aspire.RuntimeIdentifier.Tool assembly (which is normally
        // resolved out of the packed SDK's tools folder).
        var ridToolPath = GetAspireRuntimeIdentifierToolPath();

        var extraProjectXml = $"""
              <PropertyGroup>
                <_AspireUseSdkPickBestRid>false</_AspireUseSdkPickBestRid>
                <AspireRidToolExecutable>{SecurityElement.Escape(ridToolPath)}</AspireRidToolExecutable>
              </PropertyGroup>
            """;

        var packageReferences = await RunAddReferenceToDashboardAndDcpAsync(extraProjectXml);

        Assert.Contains("UseSdkPickBestRid=false", packageReferences);
        Assert.Contains("RunRidToolFallback=true", packageReferences);
        AssertDashboardAndOrchestrationReferences(packageReferences);
    }

    private static async Task<string[]> RunAddReferenceToDashboardAndDcpAsync(string? extraProjectXml)
    {
        var repoRoot = GetRepoRoot();
        using var tempDirectory = new TestTempDirectory();

        var projectDirectory = Path.Combine(tempDirectory.Path, "AppHost");
        Directory.CreateDirectory(projectDirectory);

        var sdkTargetsPath = SecurityElement.Escape(Path.Combine(repoRoot, "src", "Aspire.AppHost.Sdk", "SDK", "Sdk.in.targets"));
        var packageReferencesPath = Path.Combine(projectDirectory, "obj", "package-references.txt");

        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "AppHost.csproj"),
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <SkipAspireWorkloadManifest>true</SkipAspireWorkloadManifest>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="Aspire.Hosting.AppHost" Version="13.4.0" />
              </ItemGroup>

              <Import Project="{{sdkTargetsPath}}" />

            {{extraProjectXml}}

              <Target Name="WritePackageReferences" DependsOnTargets="AddReferenceToDashboardAndDCP">
                <WriteLinesToFile File="$(BaseIntermediateOutputPath)package-references.txt"
                                  Lines="UseSdkPickBestRid=$(_AspireUseSdkPickBestRid);RunRidToolFallback=$(_AspireRunRidToolFallback);@(PackageReference->'%(Identity)=%(Version)')"
                                  Overwrite="true" />
              </Target>

            </Project>
            """);

        var result = await RunDotNetAsync(projectDirectory, "msbuild -nologo -t:WritePackageReferences");

        Assert.True(result.ExitCode == 0, result.Output);

        return await File.ReadAllLinesAsync(packageReferencesPath);
    }

    private static void AssertDashboardAndOrchestrationReferences(string[] packageReferences)
    {
        var dashboardReference = Assert.Single(packageReferences, static packageReference => packageReference.StartsWith("Aspire.Dashboard.Sdk.", StringComparison.Ordinal));
        var orchestrationReference = Assert.Single(packageReferences, static packageReference => packageReference.StartsWith("Aspire.Hosting.Orchestration.", StringComparison.Ordinal));

        var dashboardRid = GetPackageRid(dashboardReference, "Aspire.Dashboard.Sdk.");
        var orchestrationRid = GetPackageRid(orchestrationReference, "Aspire.Hosting.Orchestration.");

        Assert.Equal(dashboardRid, orchestrationRid);
        Assert.Contains(dashboardRid, s_supportedRids);
        Assert.Equal($"Aspire.Dashboard.Sdk.{dashboardRid}=13.4.0", dashboardReference);
        Assert.Equal($"Aspire.Hosting.Orchestration.{dashboardRid}=13.4.0", orchestrationReference);
    }

    private static string GetAspireRuntimeIdentifierToolPath()
    {
        // The path to the locally-built RID tool is baked into the test assembly via AssemblyMetadata
        // so the test can locate it regardless of the configuration the test was built with.
        var assembly = typeof(AppHostSdkTargetsTests).Assembly;
        var toolPath = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => string.Equals(a.Key, "AspireRuntimeIdentifierToolPath", StringComparison.Ordinal))
            .Value;
        Assert.False(string.IsNullOrEmpty(toolPath), "AspireRuntimeIdentifierToolPath assembly metadata is not set.");
        Assert.True(File.Exists(toolPath), $"Aspire.RuntimeIdentifier.Tool was not built at '{toolPath}'. Build the test project to produce it.");
        return toolPath!;
    }

    private static string GetPackageRid(string packageReference, string prefix)
    {
        var equalsIndex = packageReference.IndexOf('=');
        Assert.True(equalsIndex > prefix.Length, $"Package reference '{packageReference}' did not contain a RID.");

        return packageReference[prefix.Length..equalsIndex];
    }

    private static async Task<(int ExitCode, string Output)> RunDotNetAsync(string workingDirectory, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        });

        Assert.NotNull(process);

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"dotnet {arguments} timed out after 3 minutes.");
        }

        var output = await outputTask;
        var error = await errorTask;

        return (process.ExitCode, output + error);
    }

    private static string GetRepoRoot()
    {
        var directory = AppContext.BaseDirectory;

        while (directory is not null && !Directory.Exists(Path.Combine(directory, ".git")) && !File.Exists(Path.Combine(directory, ".git")))
        {
            directory = Directory.GetParent(directory)?.FullName;
        }

        return directory ?? throw new InvalidOperationException("Could not find repository root.");
    }
}
