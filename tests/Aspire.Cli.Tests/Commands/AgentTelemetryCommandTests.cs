// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Commands;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Telemetry;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class AgentTelemetryCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task AgentTelemetry_EmitsReportedActivityWithProvidedTags()
    {
        var (capturedActivities, listener) = CreateCapturingListener(out var reportedSourceName);
        using (listener)
        {
            using var workspace = TemporaryWorkspace.Create(outputHelper);
            var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
            {
                options.TelemetryFactory = _ => TestTelemetryHelper.CreateInitializedTelemetry(reportedSourceName, $"Diag.{Path.GetRandomFileName()}");
            });
            using var provider = services.BuildServiceProvider();

            var command = provider.GetRequiredService<RootCommand>();
            var result = command.Parse("agent telemetry --event-type skill_invocation --client-name copilot-cli --session-id 11111111-1111-1111-1111-111111111111 --skill-name aspire --timestamp 2026-01-01T00:00:00Z");

            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, exitCode);

            var activity = Assert.Single(capturedActivities);
            Assert.Equal(TelemetryConstants.Activities.AgentTelemetry, activity.OperationName);

            var tags = activity.Tags.ToDictionary(t => t.Key, t => t.Value);
            Assert.Equal("skill_invocation", tags[TelemetryConstants.Tags.AgentEventType]);
            Assert.Equal("copilot-cli", tags[TelemetryConstants.Tags.AgentClientName]);
            Assert.Equal("11111111-1111-1111-1111-111111111111", tags[TelemetryConstants.Tags.AgentSessionId]);
            Assert.Equal("aspire", tags[TelemetryConstants.Tags.AgentSkillName]);
            Assert.Equal("2026-01-01T00:00:00Z", tags[TelemetryConstants.Tags.AgentEventTimestamp]);
        }
    }

    [Fact]
    public async Task AgentTelemetry_DoesNotEmitTagsForMissingOptions()
    {
        var (capturedActivities, listener) = CreateCapturingListener(out var reportedSourceName);
        using (listener)
        {
            using var workspace = TemporaryWorkspace.Create(outputHelper);
            var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
            {
                options.TelemetryFactory = _ => TestTelemetryHelper.CreateInitializedTelemetry(reportedSourceName, $"Diag.{Path.GetRandomFileName()}");
            });
            using var provider = services.BuildServiceProvider();

            var command = provider.GetRequiredService<RootCommand>();
            var result = command.Parse("agent telemetry --event-type tool_invocation --tool-name aspire-list_resources");

            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, exitCode);

            var activity = Assert.Single(capturedActivities);
            var tags = activity.Tags.ToDictionary(t => t.Key, t => t.Value);
            Assert.Equal("tool_invocation", tags[TelemetryConstants.Tags.AgentEventType]);
            Assert.Equal("aspire-list_resources", tags[TelemetryConstants.Tags.AgentToolName]);
            Assert.False(tags.ContainsKey(TelemetryConstants.Tags.AgentSkillName));
            Assert.False(tags.ContainsKey(TelemetryConstants.Tags.AgentFileReference));
            Assert.False(tags.ContainsKey(TelemetryConstants.Tags.AgentClientName));
        }
    }

    [Fact]
    public async Task AgentTelemetry_DropsOverlongAndUnsafeValues()
    {
        var (capturedActivities, listener) = CreateCapturingListener(out var reportedSourceName);
        using (listener)
        {
            using var workspace = TemporaryWorkspace.Create(outputHelper);
            var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
            {
                options.TelemetryFactory = _ => TestTelemetryHelper.CreateInitializedTelemetry(reportedSourceName, $"Diag.{Path.GetRandomFileName()}");
            });
            using var provider = services.BuildServiceProvider();

            var longValue = new string('a', 1000);
            var command = provider.GetRequiredService<RootCommand>();
            var result = command.Parse($"agent telemetry --event-type reference_file_read --file-reference {longValue}");

            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, exitCode);

            var activity = Assert.Single(capturedActivities);
            var tags = activity.Tags.ToDictionary(t => t.Key, t => t.Value);
            // An overlong file reference is dropped (not truncated) so oversized values never reach the backend.
            Assert.False(tags.ContainsKey(TelemetryConstants.Tags.AgentFileReference));
            Assert.Equal("reference_file_read", tags[TelemetryConstants.Tags.AgentEventType]);
        }
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Users\\someone\\secret.txt")]
    [InlineData("../../etc/passwd")]
    [InlineData("~/secret")]
    public async Task AgentTelemetry_DropsUnsafeFileReferences(string fileReference)
    {
        var (capturedActivities, listener) = CreateCapturingListener(out var reportedSourceName);
        using (listener)
        {
            using var workspace = TemporaryWorkspace.Create(outputHelper);
            var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
            {
                options.TelemetryFactory = _ => TestTelemetryHelper.CreateInitializedTelemetry(reportedSourceName, $"Diag.{Path.GetRandomFileName()}");
            });
            using var provider = services.BuildServiceProvider();

            var command = provider.GetRequiredService<RootCommand>();
            var result = command.Parse(["agent", "telemetry", "--event-type", "reference_file_read", "--file-reference", fileReference]);

            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, exitCode);

            var activity = Assert.Single(capturedActivities);
            var tags = activity.Tags.ToDictionary(t => t.Key, t => t.Value);
            Assert.False(tags.ContainsKey(TelemetryConstants.Tags.AgentFileReference));
        }
    }

    [Fact]
    public async Task AgentTelemetry_DropsUnknownEventType()
    {
        var (capturedActivities, listener) = CreateCapturingListener(out var reportedSourceName);
        using (listener)
        {
            using var workspace = TemporaryWorkspace.Create(outputHelper);
            var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
            {
                options.TelemetryFactory = _ => TestTelemetryHelper.CreateInitializedTelemetry(reportedSourceName, $"Diag.{Path.GetRandomFileName()}");
            });
            using var provider = services.BuildServiceProvider();

            var command = provider.GetRequiredService<RootCommand>();
            var result = command.Parse("agent telemetry --event-type not_a_real_event --skill-name aspire");

            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, exitCode);

            var activity = Assert.Single(capturedActivities);
            var tags = activity.Tags.ToDictionary(t => t.Key, t => t.Value);
            Assert.False(tags.ContainsKey(TelemetryConstants.Tags.AgentEventType));
            Assert.Equal("aspire", tags[TelemetryConstants.Tags.AgentSkillName]);
        }
    }

    [Fact]
    public async Task AgentTelemetry_ExitsZero_WithUnknownToken()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        // A newer hook script may pass a flag this CLI version does not understand; it must not fail.
        var result = command.Parse("agent telemetry --event-type skill_invocation --some-future-flag value");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task AgentTelemetry_ExitsZero_WithNoOptions()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("agent telemetry");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public void AgentTelemetry_IsHidden()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AgentTelemetryCommand>();
        Assert.True(command.Hidden);
    }

    [Fact]
    public async Task AgentTelemetry_RecordsValidRelativeFileReference()
    {
        var (capturedActivities, listener) = CreateCapturingListener(out var reportedSourceName);
        using (listener)
        {
            using var workspace = TemporaryWorkspace.Create(outputHelper);
            var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
            {
                options.TelemetryFactory = _ => TestTelemetryHelper.CreateInitializedTelemetry(reportedSourceName, $"Diag.{Path.GetRandomFileName()}");
            });
            using var provider = services.BuildServiceProvider();

            var command = provider.GetRequiredService<RootCommand>();
            var result = command.Parse("agent telemetry --event-type reference_file_read --file-reference aspire/references/deploy.md");

            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, exitCode);

            var activity = Assert.Single(capturedActivities);
            var tags = activity.Tags.ToDictionary(t => t.Key, t => t.Value);
            Assert.Equal("aspire/references/deploy.md", tags[TelemetryConstants.Tags.AgentFileReference]);
        }
    }

    private static (List<Activity> Activities, ActivityListener Listener) CreateCapturingListener(out string reportedSourceName)
    {
        reportedSourceName = $"Test.{Path.GetRandomFileName()}";
        var captured = new List<Activity>();
        var sourceName = reportedSourceName;

        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => captured.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        return (captured, listener);
    }
}
