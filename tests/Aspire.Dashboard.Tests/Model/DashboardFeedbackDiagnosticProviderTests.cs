// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Hosting;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;
using LayoutResources = Aspire.Dashboard.Resources.Layout;

namespace Aspire.Dashboard.Tests.Model;

// The provider localizes the issue-body labels and failure messages, so pin the culture to en-US to
// keep the asserted English output deterministic regardless of the test runner's culture.
[UseCulture("en-US")]
public sealed class DashboardFeedbackDiagnosticProviderTests
{
    private const string SampleAppHostInfo = "C# (`MyApp.AppHost.csproj`) using Aspire.AppHost.Sdk 13.5.0 and Aspire.Hosting.AppHost 9.0.0 targeting `net10.0`";

    [Fact]
    public void BuildAdditionalContext_WithoutAppHostInfo_IncludesEnvironmentLinesOnly()
    {
        var provider = CreateProvider(new ConfigurationBuilder().Build());

        var result = provider.BuildAdditionalContext(includeAppHostInfo: false);

        Assert.Collection(GetNonEmptyLines(result),
            line => Assert.StartsWith("- Posted from: Dashboard", line, StringComparison.Ordinal),
            line => Assert.StartsWith("- Aspire version:", line, StringComparison.Ordinal),
            line => Assert.StartsWith("- Operating system:", line, StringComparison.Ordinal),
            line => Assert.Equal("- Dashboard route: /resources", line));
    }

    [Fact]
    public void BuildAdditionalContext_IncludeAppHostInfoAndConfigured_AppendsAppHostLine()
    {
        var provider = CreateProvider(CreateAppHostInfoConfiguration(SampleAppHostInfo));

        var result = provider.BuildAdditionalContext(includeAppHostInfo: true);

        Assert.Collection(GetNonEmptyLines(result),
            line => Assert.StartsWith("- Posted from: Dashboard", line, StringComparison.Ordinal),
            line => Assert.StartsWith("- Aspire version:", line, StringComparison.Ordinal),
            line => Assert.StartsWith("- Operating system:", line, StringComparison.Ordinal),
            line => Assert.Equal("- Dashboard route: /resources", line),
            line => Assert.Equal($"- AppHost: {SampleAppHostInfo}", line));
    }

    [Fact]
    public void BuildAdditionalContext_AppHostInfoConfiguredButNotRequested_OmitsAppHostLine()
    {
        var provider = CreateProvider(CreateAppHostInfoConfiguration(SampleAppHostInfo));

        var result = provider.BuildAdditionalContext(includeAppHostInfo: false);

        Assert.DoesNotContain("- AppHost:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAdditionalContext_IncludeAppHostInfoButNotConfigured_OmitsAppHostLine()
    {
        var provider = CreateProvider(new ConfigurationBuilder().Build());

        var result = provider.BuildAdditionalContext(includeAppHostInfo: true);

        Assert.DoesNotContain("- AppHost:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAdditionalContext_DropsQueryStringFromDashboardRoute()
    {
        // The route can carry user-specific filters (resource names, log filters) that must not leak
        // into a public issue, so only the page identity is kept.
        var provider = CreateProvider(
            new ConfigurationBuilder().Build(),
            currentUri: "http://localhost/structuredlogs?resource=customer-api&filters=secret-value");

        var result = provider.BuildAdditionalContext(includeAppHostInfo: false);

        var routeLine = Assert.Single(GetNonEmptyLines(result), line => line.StartsWith("- Dashboard route:", StringComparison.Ordinal));
        Assert.Equal("- Dashboard route: /structuredlogs", routeLine);
    }

    [Fact]
    public async Task CaptureAspireDoctorOutputAsync_ReturnsTrimmedStandardOutput()
    {
        var runner = new FakeFeedbackDiagnosticProcessRunner((_, _) => Success("""

            {"sdk":"10.0.301"}

            """));
        var provider = CreateProvider(CreateCliPathConfiguration("aspire"), runner);

        var result = await provider.CaptureAspireDoctorOutputAsync(CancellationToken.None);

        Assert.Equal("""{"sdk":"10.0.301"}""", result);
    }

    [Fact]
    public async Task CaptureAspireDoctorOutputAsync_ProcessNotStarted_ReturnsFailureMessage()
    {
        var runner = new FakeFeedbackDiagnosticProcessRunner((_, _) =>
            new FeedbackDiagnosticProcessResult(Started: false, ExitCode: -1, StandardOutput: string.Empty, TimedOut: false, FailureMessage: "aspire not found"));
        var provider = CreateProvider(CreateCliPathConfiguration("aspire"), runner);

        var result = await provider.CaptureAspireDoctorOutputAsync(CancellationToken.None);

        Assert.Equal("Could not capture `aspire doctor` output (aspire not found).", result);
    }

    [Fact]
    public async Task CaptureAspireDoctorOutputAsync_TimedOut_ReturnsTimeoutMessage()
    {
        var runner = new FakeFeedbackDiagnosticProcessRunner((_, _) =>
            new FeedbackDiagnosticProcessResult(Started: true, ExitCode: -1, StandardOutput: string.Empty, TimedOut: true, FailureMessage: null));
        var provider = CreateProvider(CreateCliPathConfiguration("aspire"), runner);

        var result = await provider.CaptureAspireDoctorOutputAsync(CancellationToken.None);

        Assert.Equal("Could not capture `aspire doctor` output because it did not complete within 30 seconds.", result);
    }

    [Fact]
    public async Task CaptureAspireDoctorOutputAsync_NonZeroExitWithEmptyOutput_ReturnsExitCodeMessage()
    {
        var runner = new FakeFeedbackDiagnosticProcessRunner((_, _) =>
            new FeedbackDiagnosticProcessResult(Started: true, ExitCode: 3, StandardOutput: "   ", TimedOut: false, FailureMessage: null));
        var provider = CreateProvider(CreateCliPathConfiguration("aspire"), runner);

        var result = await provider.CaptureAspireDoctorOutputAsync(CancellationToken.None);

        Assert.Equal("Could not capture `aspire doctor` output (exit code 3).", result);
    }

    [Fact]
    public async Task CaptureAspireDoctorOutputAsync_UsesConfiguredCliPath_WhenSet()
    {
        var cliPath = Path.Combine("C:\\", "tools", "aspire", "aspire.exe");
        var runner = new FakeFeedbackDiagnosticProcessRunner((_, _) => Success("""{"sdk":"10.0.301"}"""));
        var provider = CreateProvider(CreateCliPathConfiguration(cliPath), runner);

        await provider.CaptureAspireDoctorOutputAsync(CancellationToken.None);

        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal(cliPath, invocation.FileName);
        Assert.Equal(["doctor", "--format", "json"], invocation.Arguments);
    }

    [Fact]
    public void IsAspireDoctorOutputAvailable_True_WhenCliPathConfigured()
    {
        var provider = CreateProvider(CreateCliPathConfiguration("aspire"));

        Assert.True(provider.IsAspireDoctorOutputAvailable);
    }

    [Fact]
    public void IsAspireDoctorOutputAvailable_False_WhenCliPathNotConfigured()
    {
        var provider = CreateProvider(new ConfigurationBuilder().Build());

        Assert.False(provider.IsAspireDoctorOutputAvailable);
    }

    [Fact]
    public async Task CaptureAspireDoctorOutputAsync_Throws_WhenCliPathNotConfigured()
    {
        // The dashboard never probes for `aspire` on PATH, so capturing without the AppHost-forwarded
        // CLI path is a contract violation; callers must gate on IsAspireDoctorOutputAvailable.
        var runner = new FakeFeedbackDiagnosticProcessRunner((_, _) => Success("""{"sdk":"10.0.301"}"""));
        var provider = CreateProvider(new ConfigurationBuilder().Build(), runner);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CaptureAspireDoctorOutputAsync(CancellationToken.None));
        Assert.Empty(runner.Invocations);
    }

    private static FeedbackDiagnosticProcessResult Success(string standardOutput) =>
        new(Started: true, ExitCode: 0, StandardOutput: standardOutput, TimedOut: false, FailureMessage: null);

    private static string[] GetNonEmptyLines(string value)
    {
        return value.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    private static DashboardFeedbackDiagnosticProvider CreateProvider(IConfiguration configuration, IFeedbackDiagnosticProcessRunner? processRunner = null, string currentUri = "http://localhost/resources")
    {
        return new DashboardFeedbackDiagnosticProvider(
            new TestNavigationManager(currentUri),
            configuration,
            processRunner ?? new FakeFeedbackDiagnosticProcessRunner((_, _) => Success(string.Empty)),
            CreateLocalizer());
    }

    private static IStringLocalizer<LayoutResources> CreateLocalizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization();
        return services.BuildServiceProvider().GetRequiredService<IStringLocalizer<LayoutResources>>();
    }

    private static IConfiguration CreateAppHostInfoConfiguration(string appHostInfo)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection([
                new KeyValuePair<string, string?>(DashboardConfigNames.AppHostInfoName.ConfigKey, appHostInfo)
            ])
            .Build();
    }

    private static IConfiguration CreateCliPathConfiguration(string cliPath)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection([
                new KeyValuePair<string, string?>(DashboardConfigNames.CliPathName.ConfigKey, cliPath)
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
        public TestNavigationManager(string currentUri = "http://localhost/resources")
        {
            Initialize("http://localhost/", currentUri);
        }
    }
}
