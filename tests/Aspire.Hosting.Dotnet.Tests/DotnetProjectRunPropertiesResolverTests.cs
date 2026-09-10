// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetProjectRunPropertiesResolverTests(ITestOutputHelper outputHelper)
{
    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task FailedResolutionPreservesBuildEnvironmentValuesInMsBuildOutput()
    {
        const string errorMarker = "RUN_PROPERTIES_ERROR_MARKER";

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = Path.Combine(workspace.Path, "Broken.csproj");
        File.WriteAllText(projectPath, $$"""
            <Project>
              <Target Name="ComputeRunArguments">
                <Error Text="$(BUILD_SECRET)" />
              </Target>
            </Project>
            """);
        var sink = new TestSink();
        var logger = new TestLogger(nameof(DotnetProjectRunPropertiesResolverTests), sink, enabled: true);

        await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            DotnetProjectRunPropertiesResolver.ResolveAsync(
                projectPath,
                buildConfiguration: null,
                new Dictionary<string, string>
                {
                    ["BUILD_SECRET"] = errorMarker,
                },
                workspace.Path,
                logger,
                TestContext.Current.CancellationToken));

        var log = Assert.Single(sink.Writes);
        Assert.Equal(LogLevel.Debug, log.LogLevel);
        Assert.Contains("Standard output:", log.Message, StringComparison.Ordinal);
        Assert.Contains(errorMarker, log.Message, StringComparison.Ordinal);
        Assert.Contains("Standard error:", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task SuccessfulResolutionReadsDedicatedResultWhenMsBuildEmitsDiagnostics()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = Path.Combine(workspace.Path, "Warning.csproj");
        File.WriteAllText(projectPath, """
            <Project>
              <PropertyGroup>
                <RunCommand>dotnet</RunCommand>
                <RunArguments>exec app.dll</RunArguments>
                <RunWorkingDirectory>bin</RunWorkingDirectory>
              </PropertyGroup>
              <Target Name="ComputeRunArguments">
                <Warning Text="A build diagnostic." />
              </Target>
            </Project>
            """);

        var result = await DotnetProjectRunPropertiesResolver.ResolveAsync(
            projectPath,
            buildConfiguration: null,
            new Dictionary<string, string>(),
            workspace.Path,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Equal(new("dotnet", "exec app.dll", Path.Combine(workspace.Path, "bin")), result);
    }
}
