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
    [Theory]
    [InlineData("skill", "aspire")]
    [InlineData("extension", "aspireify")]
    public async Task AgentTelemetry_EmitsReportedActivityWithProvidedAssetTags(string assetKind, string assetName)
    {
        var (capturedActivities, listener) = CreateCapturingListener(out var reportedSourceName);
        using (listener)
        {
            using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
            var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
            {
                options.TelemetryFactory = _ => TestTelemetryHelper.CreateInitializedTelemetry(reportedSourceName, $"Diag.{Path.GetRandomFileName()}");
            });
            using var provider = services.BuildServiceProvider();

            var command = provider.GetRequiredService<RootCommand>();
            var result = command.Parse($"agent telemetry --event-type asset_invocation --client-name copilot-cli --session-id 11111111-1111-1111-1111-111111111111 --asset-kind {assetKind} --asset-name {assetName} --timestamp 2026-01-01T00:00:00Z");

            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, exitCode);

            var activity = Assert.Single(capturedActivities);
            Assert.Equal(TelemetryConstants.Activities.AgentTelemetry, activity.OperationName);

            var tags = activity.Tags.ToDictionary(t => t.Key, t => t.Value);
            Assert.Equal("asset_invocation", tags[TelemetryConstants.Tags.AgentEventType]);
            Assert.Equal("copilot-cli", tags[TelemetryConstants.Tags.AgentClientName]);
            Assert.Equal("11111111-1111-1111-1111-111111111111", tags[TelemetryConstants.Tags.AgentSessionId]);
            Assert.Equal(assetKind, tags[TelemetryConstants.Tags.AgentAssetKind]);
            Assert.Equal(assetName, tags[TelemetryConstants.Tags.AgentAssetName]);
            Assert.Equal("2026-01-01T00:00:00Z", tags[TelemetryConstants.Tags.AgentEventTimestamp]);
        }
    }

    [Fact]
    public async Task AgentTelemetry_EmitsReportedActivityWithProvidedAssetInteractionTags()
    {
        var (capturedActivities, listener) = CreateCapturingListener(out var reportedSourceName);
        using (listener)
        {
            using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
            var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
            {
                options.TelemetryFactory = _ => TestTelemetryHelper.CreateInitializedTelemetry(reportedSourceName, $"Diag.{Path.GetRandomFileName()}");
            });
            using var provider = services.BuildServiceProvider();

            var command = provider.GetRequiredService<RootCommand>();
            var result = command.Parse("agent telemetry --event-type asset_interaction --client-name copilot-cli --asset-kind extension --asset-name aspireify --interaction-type canvas_lifecycle --interaction-name closed --outcome success --duration-ms 9223372036854775807");

            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, exitCode);
            var activity = Assert.Single(capturedActivities);
            var tags = activity.Tags.ToDictionary(t => t.Key, t => t.Value);
            var tagObjects = activity.TagObjects.ToDictionary(t => t.Key, t => t.Value);
            Assert.Equal("asset_interaction", tags[TelemetryConstants.Tags.AgentEventType]);
            Assert.Equal("extension", tags[TelemetryConstants.Tags.AgentAssetKind]);
            Assert.Equal("aspireify", tags[TelemetryConstants.Tags.AgentAssetName]);
            Assert.Equal("canvas_lifecycle", tags[TelemetryConstants.Tags.AgentInteractionType]);
            Assert.Equal("closed", tags[TelemetryConstants.Tags.AgentInteractionName]);
            Assert.Equal("success", tags[TelemetryConstants.Tags.AgentInteractionOutcome]);
            Assert.Equal(long.MaxValue, tagObjects[TelemetryConstants.Tags.AgentInteractionDurationMilliseconds]);
        }
    }

    [Fact]
    public async Task AgentTelemetry_DoesNotEmitTagsForMissingOptions()
    {
        var (capturedActivities, listener) = CreateCapturingListener(out var reportedSourceName);
        using (listener)
        {
            using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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
            Assert.False(tags.ContainsKey(TelemetryConstants.Tags.AgentAssetKind));
            Assert.False(tags.ContainsKey(TelemetryConstants.Tags.AgentAssetName));
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
            using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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
            using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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
            using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
            var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
            {
                options.TelemetryFactory = _ => TestTelemetryHelper.CreateInitializedTelemetry(reportedSourceName, $"Diag.{Path.GetRandomFileName()}");
            });
            using var provider = services.BuildServiceProvider();

            var command = provider.GetRequiredService<RootCommand>();
            var result = command.Parse("agent telemetry --event-type not_a_real_event --asset-kind skill --asset-name aspire");

            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, exitCode);

            var activity = Assert.Single(capturedActivities);
            var tags = activity.Tags.ToDictionary(t => t.Key, t => t.Value);
            Assert.False(tags.ContainsKey(TelemetryConstants.Tags.AgentEventType));
            Assert.Equal("skill", tags[TelemetryConstants.Tags.AgentAssetKind]);
            Assert.Equal("aspire", tags[TelemetryConstants.Tags.AgentAssetName]);
        }
    }

    [Fact]
    public async Task AgentTelemetry_EmitsNoActivity_WhenAllValuesInvalid()
    {
        var (capturedActivities, listener) = CreateCapturingListener(out var reportedSourceName);
        using (listener)
        {
            using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
            var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
            {
                options.TelemetryFactory = _ => TestTelemetryHelper.CreateInitializedTelemetry(reportedSourceName, $"Diag.{Path.GetRandomFileName()}");
            });
            using var provider = services.BuildServiceProvider();

            var command = provider.GetRequiredService<RootCommand>();
            // Every value fails validation (unknown event type, identifier with a space, absolute path).
            // When nothing survives, the command must emit no span at all rather than a tagless one.
            var result = command.Parse(["agent", "telemetry", "--event-type", "not_a_real_event", "--asset-kind", "not-an-asset-kind", "--asset-name", "bad name", "--file-reference", "/etc/passwd"]);

            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, exitCode);
            Assert.Empty(capturedActivities);
        }
    }

    [Fact]
    public async Task AgentTelemetry_ExitsZero_WithUnknownToken()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        // A newer hook script may pass a flag this CLI version does not understand; it must not fail.
        var result = command.Parse("agent telemetry --event-type asset_invocation --some-future-flag value");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task AgentTelemetry_ExitsZero_WithNoOptions()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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
            using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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

    [Fact]
    public async Task AgentTelemetry_NormalizesLegacySkillInvocation()
    {
        var (capturedActivities, listener) = CreateCapturingListener(out var reportedSourceName);
        using (listener)
        {
            using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
            var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
            {
                options.TelemetryFactory = _ => TestTelemetryHelper.CreateInitializedTelemetry(reportedSourceName, $"Diag.{Path.GetRandomFileName()}");
            });
            using var provider = services.BuildServiceProvider();

            var command = provider.GetRequiredService<RootCommand>();
            var result = command.Parse("agent telemetry --event-type skill_invocation --skill-name aspire");

            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, exitCode);
            var activity = Assert.Single(capturedActivities);
            var tags = activity.Tags.ToDictionary(t => t.Key, t => t.Value);
            Assert.Equal("asset_invocation", tags[TelemetryConstants.Tags.AgentEventType]);
            Assert.Equal("skill", tags[TelemetryConstants.Tags.AgentAssetKind]);
            Assert.Equal("aspire", tags[TelemetryConstants.Tags.AgentAssetName]);
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
