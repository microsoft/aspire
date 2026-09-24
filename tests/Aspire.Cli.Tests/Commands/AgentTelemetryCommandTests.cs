// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Commands;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Telemetry;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace Aspire.Cli.Tests.Commands;

public class AgentTelemetryCommandTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("""{"toolName":"bash","toolArgs":{"command":"echo hello"}}""")]
    [InlineData("""{"toolName":"Read","tool_input":{"file_path":"/skills/unrelated/SKILL.md"}}""")]
    [InlineData("{malformed")]
    public async Task NativeHook_UsesNormalDispatchWithoutInitializingTelemetryForUnrelatedInput(string payload)
    {
        using var fixture = new TelemetryFixture(initialize: false);
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.TelemetryFactory = _ => fixture.Telemetry;
        });
        using var input = new StringReader(payload);
        using var output = new StringWriter();
        using var error = new StringWriter();
        services.AddSingleton<TextReader>(input);
        services.AddSingleton(CreateConsole(output, error));
        var manager = new Lazy<TelemetryManager>(() => throw new InvalidOperationException("Telemetry must not be initialized."));
        services.AddSingleton(manager);
        using var provider = services.BuildServiceProvider();
        var result = provider.GetRequiredService<RootCommand>().Parse(["agent", "telemetry", "--hook"]);

        Program.InitializeCommandTelemetry(result.CommandResult.Command, manager, fixture.Telemetry);
        Assert.Equal(0, await result.InvokeAsync().DefaultTimeout());

        Assert.False(manager.IsValueCreated);
        Assert.Empty(await fixture.TagsSource.TagsTask);
        Assert.Null(fixture.CapturedActivity);
        Assert.Equal("""{"continue":true}""" + Environment.NewLine, output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData("""{"toolName":"skill","toolArgs":{"skill":"aspire"}}""", "skill_invocation", TelemetryConstants.Tags.AgentSkillName, "aspire")]
    [InlineData("""{"toolName":"aspire-list_resources"}""", "tool_invocation", TelemetryConstants.Tags.AgentToolName, "aspire-list_resources")]
    [InlineData("""{"toolName":"view","toolArgs":{"path":"/skills/aspire-deployment/references/azure.md"}}""",
        "reference_file_read", TelemetryConstants.Tags.AgentFileReference, "aspire-deployment/references/azure.md")]
    public async Task NativeHook_InitializesOnDemandAndPreservesEvents(string payload, string eventType, string dimension, string value)
    {
        using var fixture = new TelemetryFixture(initialize: false, telemetryConfiguration: new TelemetryConfiguration
        {
            ReportedTelemetryEnabled = true,
            EmitInternalMicrosoftDiagnostics = false
        });
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.TelemetryFactory = _ => fixture.Telemetry;
        });
        using var input = new StringReader(payload);
        using var output = new StringWriter();
        using var error = new StringWriter();
        services.AddSingleton<TextReader>(input);
        services.AddSingleton(CreateConsole(output, error));
        using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<Lazy<TelemetryManager>>();
        var result = provider.GetRequiredService<RootCommand>().Parse(["agent", "telemetry", "--hook"]);
        Program.InitializeCommandTelemetry(result.CommandResult.Command, manager, fixture.Telemetry);
        Assert.False(manager.IsValueCreated);
        Assert.Empty(await fixture.TagsSource.TagsTask);

        Assert.Equal(0, await result.InvokeAsync().DefaultTimeout());

        Assert.True(manager.IsValueCreated);
        Assert.NotEmpty(await fixture.TagsSource.TagsTask);
        var activity = Assert.IsType<Activity>(fixture.CapturedActivity);
        Assert.Equal(TelemetryConstants.Activities.AgentTelemetry, activity.OperationName);
        Assert.Equal(eventType, activity.GetTagItem(TelemetryConstants.Tags.AgentEventType));
        Assert.Equal(value, activity.GetTagItem(dimension));
        Assert.Equal("""{"continue":true}""" + Environment.NewLine, output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData("--hook")]
    [InlineData("--drain")]
    [InlineData("--event-type")]
    public async Task OptOut_UsesNormalDispatchWithoutInitializingProviders(string mode)
    {
        using var fixture = new TelemetryFixture(initialize: false);
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.TelemetryFactory = _ => fixture.Telemetry;
        });
        services.AddSingleton<IEnvironment>(new TestEnvironment(new Dictionary<string, string?>
        {
            [AspireCliTelemetry.TelemetryOptOutConfigKey] = "true"
        }));
        using var input = new StringReader("""{"toolName":"skill","toolArgs":{"skill":"aspire"}}""");
        services.AddSingleton<TextReader>(input);
        var manager = new Lazy<TelemetryManager>(() => throw new InvalidOperationException("Opted-out command created telemetry."));
        services.AddSingleton(manager);
        using var provider = services.BuildServiceProvider();
        var args = mode == "--event-type"
            ? new[] { "agent", "telemetry", mode, "skill_invocation" }
            : ["agent", "telemetry", mode];
        var result = provider.GetRequiredService<RootCommand>().Parse(args);

        Program.InitializeCommandTelemetry(result.CommandResult.Command, manager, fixture.Telemetry);
        Assert.Equal(0, await result.InvokeAsync().DefaultTimeout());
        Assert.False(manager.IsValueCreated);
        Assert.Empty(await fixture.TagsSource.TagsTask);
    }

    [Fact]
    public async Task OrdinaryCommands_StillInitializeProvidersBeforeEnrichment()
    {
        var order = new List<string>();
        using var fixture = new TelemetryFixture(initialize: false, machineInfoProvider: new TelemetryFixture.TestMachineInformationProvider
        {
            GetDeviceIdCallback = () =>
            {
                order.Add("enrichment");
                return Task.FromResult<string?>("test-device");
            }
        });
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>().Parse(["doctor"]).CommandResult.Command;
        Assert.True(Assert.IsAssignableFrom<BaseCommand>(command).InitializeTelemetryOnStartup);
        var manager = new Lazy<TelemetryManager>(() =>
        {
            order.Add("provider");
            return new TelemetryManager(new TelemetryConfiguration(), new TelemetryTagsSource(NullLogger<TelemetryTagsSource>.Instance));
        });
        try
        {
            Program.InitializeCommandTelemetry(command, manager, fixture.Telemetry);
            await fixture.TagsSource.TagsTask.DefaultTimeout();

            Assert.Equal(["provider", "enrichment"], order);
            Assert.NotEmpty(await fixture.TagsSource.TagsTask);
        }
        finally
        {
            if (manager.IsValueCreated)
            {
                manager.Value.Dispose();
            }
        }
    }

    private static ConsoleEnvironment CreateConsole(TextWriter output, TextWriter error)
        => new(
            AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output), Ansi = AnsiSupport.No }),
            AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(error), Ansi = AnsiSupport.No }));

    [Fact]
    public async Task AgentTelemetry_EmitsReportedActivityWithProvidedTags()
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
            var result = command.Parse(["agent", "telemetry", "--event-type", "not_a_real_event", "--skill-name", "bad name", "--file-reference", "/etc/passwd"]);

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
        var result = command.Parse("agent telemetry --event-type skill_invocation --some-future-flag value");

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

    private static (List<Activity> Activities, ActivityListener Listener) CreateCapturingListener(out string reportedSourceName)
    {
        reportedSourceName = $"Test.{Path.GetRandomFileName()}";
        var captured = new List<Activity>();
        var sourceName = reportedSourceName;

        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == TelemetryConstants.Activities.AgentTelemetry)
                {
                    captured.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        return (captured, listener);
    }
}
