// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Hosting;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class DashboardFeedbackDiagnosticProviderTests
{
    [Fact]
    public void BuildAdditionalContext_IncludesEnvironmentLinesOnly()
    {
        var provider = CreateProvider(new ConfigurationBuilder().Build());

        var result = provider.BuildAdditionalContext();

        Assert.Collection(GetNonEmptyLines(result),
            line => Assert.StartsWith("- Posted from: Dashboard", line, StringComparison.Ordinal),
            line => Assert.StartsWith("- Aspire version:", line, StringComparison.Ordinal),
            line => Assert.StartsWith("- Operating system:", line, StringComparison.Ordinal),
            line => Assert.Equal("- Dashboard route: /resources", line));
    }

    [Fact]
    public async Task CaptureAppHostContextAsync_NoAppHostFilePath_ReturnsNull()
    {
        var provider = CreateProvider(new ConfigurationBuilder().Build());

        var result = await provider.CaptureAppHostContextAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CaptureAppHostContextAsync_ConfiguredMissingAppHostFile_ReturnsNull()
    {
        using var tempDirectory = new TemporaryDirectory();
        var provider = CreateProvider(CreateConfiguration(Path.Combine(tempDirectory.Path, "missing.csproj")));

        var result = await provider.CaptureAppHostContextAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CaptureAppHostContextAsync_CSharpProjectAppHost_UsesMSBuildAndIncludesSdkPackageAndTargetFramework()
    {
        using var tempDirectory = new TemporaryDirectory();
        var appHostPath = Path.Combine(tempDirectory.Path, "MyApp.AppHost.csproj");
        File.WriteAllText(appHostPath, "<Project />");
        var runner = new FakeFeedbackDiagnosticProcessRunner((fileName, _) => Success("""
            {
              "Properties": {
                "AspireHostingSDKVersion": "13.5.0-preview.1.26319.9",
                "TargetFramework": "net10.0"
              },
              "Items": {
                "PackageReference": [
                  { "Identity": "Aspire.Hosting.AppHost", "Version": "9.0.0" }
                ]
              }
            }
            """));
        var provider = CreateProvider(CreateConfiguration(appHostPath), runner);

        var result = await provider.CaptureAppHostContextAsync(CancellationToken.None);

        Assert.Equal($"- AppHost: C# at `{appHostPath}` using Aspire.AppHost.Sdk 13.5.0-preview.1.26319.9 and Aspire.Hosting.AppHost 9.0.0 targeting `net10.0`{Environment.NewLine}", result);
        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal("dotnet", invocation.FileName);
        Assert.Equal("msbuild", invocation.Arguments[0]);
        Assert.Equal(Path.GetDirectoryName(appHostPath), invocation.WorkingDirectory);
    }

    [Fact]
    public async Task CaptureAppHostContextAsync_CSharpProjectAppHost_CentralPackageManagement_UsesPackageVersion()
    {
        using var tempDirectory = new TemporaryDirectory();
        var appHostPath = Path.Combine(tempDirectory.Path, "MyApp.AppHost.csproj");
        File.WriteAllText(appHostPath, "<Project />");
        var runner = new FakeFeedbackDiagnosticProcessRunner((_, _) => Success("""
            {
              "Properties": {
                "AspireHostingSDKVersion": "13.5.0",
                "TargetFramework": "net10.0"
              },
              "Items": {
                "PackageReference": [
                  { "Identity": "Aspire.Hosting.AppHost" }
                ],
                "PackageVersion": [
                  { "Identity": "Aspire.Hosting.AppHost", "Version": "9.1.0" }
                ]
              }
            }
            """));
        var provider = CreateProvider(CreateConfiguration(appHostPath), runner);

        var result = await provider.CaptureAppHostContextAsync(CancellationToken.None);

        Assert.Equal($"- AppHost: C# at `{appHostPath}` using Aspire.AppHost.Sdk 13.5.0 and Aspire.Hosting.AppHost 9.1.0 targeting `net10.0`{Environment.NewLine}", result);
    }

    [Fact]
    public async Task CaptureAppHostContextAsync_CSharpSingleFileAppHost_UsesBuildDriver()
    {
        using var tempDirectory = new TemporaryDirectory();
        var appHostPath = Path.Combine(tempDirectory.Path, "apphost.cs");
        File.WriteAllText(appHostPath, "var builder = DistributedApplication.CreateBuilder(args);");
        var runner = new FakeFeedbackDiagnosticProcessRunner((_, _) => Success("""
            {
              "Properties": {
                "AspireHostingSDKVersion": "13.5.0",
                "TargetFramework": "net10.0"
              },
              "Items": {}
            }
            """));
        var provider = CreateProvider(CreateConfiguration(appHostPath), runner);

        var result = await provider.CaptureAppHostContextAsync(CancellationToken.None);

        Assert.Equal($"- AppHost: C# at `{appHostPath}` using Aspire.AppHost.Sdk 13.5.0 targeting `net10.0`{Environment.NewLine}", result);
        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal("build", invocation.Arguments[0]);
    }

    [Fact]
    public async Task CaptureAppHostContextAsync_CSharpProjectAppHost_MSBuildProbeFails_FallsBackToPathOnly()
    {
        using var tempDirectory = new TemporaryDirectory();
        var appHostPath = Path.Combine(tempDirectory.Path, "MyApp.AppHost.csproj");
        File.WriteAllText(appHostPath, "<Project />");
        var runner = new FakeFeedbackDiagnosticProcessRunner((_, _) =>
            new FeedbackDiagnosticProcessResult(Started: true, ExitCode: 1, StandardOutput: string.Empty, TimedOut: false, FailureMessage: null));
        var provider = CreateProvider(CreateConfiguration(appHostPath), runner);

        var result = await provider.CaptureAppHostContextAsync(CancellationToken.None);

        Assert.Equal($"- AppHost: C# at `{appHostPath}`{Environment.NewLine}", result);
    }

    [Fact]
    public async Task CaptureAppHostContextAsync_TypeScriptAppHost_IncludesNodeVersionFromPath()
    {
        using var tempDirectory = new TemporaryDirectory();
        var appHostPath = Path.Combine(tempDirectory.Path, "apphost.mts");
        File.WriteAllText(appHostPath, "await createBuilder();");
        var runner = new FakeFeedbackDiagnosticProcessRunner((fileName, arguments) =>
        {
            Assert.Equal("node", fileName);
            Assert.Equal("--version", arguments[0]);
            return Success("v24.15.0\n");
        });
        var provider = CreateProvider(CreateConfiguration(appHostPath), runner);

        var result = await provider.CaptureAppHostContextAsync(CancellationToken.None);

        Assert.Equal($"- AppHost: TypeScript at `{appHostPath}` on Node.js v24.15.0{Environment.NewLine}", result);
    }

    [Fact]
    public async Task CaptureAppHostContextAsync_TypeScriptAppHost_NodeUnavailable_OmitsNodeVersion()
    {
        using var tempDirectory = new TemporaryDirectory();
        var appHostPath = Path.Combine(tempDirectory.Path, "apphost.ts");
        File.WriteAllText(appHostPath, "await createBuilder();");
        var runner = new FakeFeedbackDiagnosticProcessRunner((_, _) =>
            new FeedbackDiagnosticProcessResult(Started: false, ExitCode: -1, StandardOutput: string.Empty, TimedOut: false, FailureMessage: "node not found"));
        var provider = CreateProvider(CreateConfiguration(appHostPath), runner);

        var result = await provider.CaptureAppHostContextAsync(CancellationToken.None);

        Assert.Equal($"- AppHost: TypeScript at `{appHostPath}`{Environment.NewLine}", result);
    }

    [Fact]
    public async Task CaptureAspireDoctorOutputAsync_ReturnsTrimmedStandardOutput()
    {
        var runner = new FakeFeedbackDiagnosticProcessRunner((_, _) => Success("""

            {"sdk":"10.0.301"}

            """));
        var provider = CreateProvider(new ConfigurationBuilder().Build(), runner);

        var result = await provider.CaptureAspireDoctorOutputAsync(CancellationToken.None);

        Assert.Equal("""{"sdk":"10.0.301"}""", result);
    }

    [Fact]
    public async Task CaptureAspireDoctorOutputAsync_ProcessNotStarted_ReturnsFailureMessage()
    {
        var runner = new FakeFeedbackDiagnosticProcessRunner((_, _) =>
            new FeedbackDiagnosticProcessResult(Started: false, ExitCode: -1, StandardOutput: string.Empty, TimedOut: false, FailureMessage: "aspire not found"));
        var provider = CreateProvider(new ConfigurationBuilder().Build(), runner);

        var result = await provider.CaptureAspireDoctorOutputAsync(CancellationToken.None);

        Assert.Equal("Could not capture `aspire doctor` output (aspire not found).", result);
    }

    [Fact]
    public async Task CaptureAspireDoctorOutputAsync_TimedOut_ReturnsTimeoutMessage()
    {
        var runner = new FakeFeedbackDiagnosticProcessRunner((_, _) =>
            new FeedbackDiagnosticProcessResult(Started: true, ExitCode: -1, StandardOutput: string.Empty, TimedOut: true, FailureMessage: null));
        var provider = CreateProvider(new ConfigurationBuilder().Build(), runner);

        var result = await provider.CaptureAspireDoctorOutputAsync(CancellationToken.None);

        Assert.Equal("Could not capture `aspire doctor` output because it did not complete within 30 seconds.", result);
    }

    [Fact]
    public async Task CaptureAspireDoctorOutputAsync_NonZeroExitWithEmptyOutput_ReturnsExitCodeMessage()
    {
        var runner = new FakeFeedbackDiagnosticProcessRunner((_, _) =>
            new FeedbackDiagnosticProcessResult(Started: true, ExitCode: 3, StandardOutput: "   ", TimedOut: false, FailureMessage: null));
        var provider = CreateProvider(new ConfigurationBuilder().Build(), runner);

        var result = await provider.CaptureAspireDoctorOutputAsync(CancellationToken.None);

        Assert.Equal("Could not capture `aspire doctor` output (exit code 3).", result);
    }

    private static FeedbackDiagnosticProcessResult Success(string standardOutput) =>
        new(Started: true, ExitCode: 0, StandardOutput: standardOutput, TimedOut: false, FailureMessage: null);

    private static string[] GetNonEmptyLines(string value)
    {
        return value.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    private static DashboardFeedbackDiagnosticProvider CreateProvider(IConfiguration configuration, IFeedbackDiagnosticProcessRunner? processRunner = null)
    {
        return new DashboardFeedbackDiagnosticProvider(
            new TestNavigationManager(),
            configuration,
            processRunner ?? new FakeFeedbackDiagnosticProcessRunner((_, _) => Success(string.Empty)),
            NullLogger<DashboardFeedbackDiagnosticProvider>.Instance);
    }

    private static IConfiguration CreateConfiguration(string appHostPath)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection([
                new KeyValuePair<string, string?>(DashboardConfigNames.AppHostFilePathName.ConfigKey, appHostPath)
            ])
            .Build();
    }

    private sealed class FakeFeedbackDiagnosticProcessRunner(Func<string, IReadOnlyList<string>, FeedbackDiagnosticProcessResult> handler) : IFeedbackDiagnosticProcessRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory)> Invocations { get; } = [];

        public Task<FeedbackDiagnosticProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory,
            IReadOnlyDictionary<string, string>? environment,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Invocations.Add((fileName, arguments, workingDirectory));
            return Task.FromResult(handler(fileName, arguments));
        }
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager()
        {
            Initialize("http://localhost/", "http://localhost/resources");
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("dashboard-feedback-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
