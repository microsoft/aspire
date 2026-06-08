// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Diagnostics;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using AuxiliaryBackchannelRpcTarget = Aspire.Hosting.Backchannel.AuxiliaryBackchannelRpcTarget;
using ExecuteResourceCommandRequest = Aspire.Hosting.Backchannel.ExecuteResourceCommandRequest;

namespace Aspire.Hosting.Tests;

#pragma warning disable ASPIREINTERACTION001 // InteractionInput is used to describe resource command arguments.
#pragma warning disable ASPIREPROCESSCOMMAND001 // Process command APIs are experimental.
#pragma warning disable CS0618 // Tests intentionally cover the deprecated TypeScript withProcessCommandFactory export.

[Trait("Partition", "6")]
public class WithProcessCommandTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void ProcessCommandOptions_Default_ReturnsIndependentInstances()
    {
        var options = ProcessCommandOptions.Default;

        options.Description = "Mutated description";
        options.MaxOutputLineCount = 1;
        options.DisplayImmediately = false;
        options.SuccessExitCodes = [0, 5];

        var defaultOptions = ProcessCommandOptions.Default;

        Assert.Null(defaultOptions.Description);
        Assert.Equal(50, defaultOptions.MaxOutputLineCount);
        Assert.True(defaultOptions.DisplayImmediately);
        Assert.Equal([0], defaultOptions.SuccessExitCodes);
    }

    [Fact]
    public void WithProcessCommand_AddsResourceCommandAnnotation_WithCustomValues()
    {
        using var builder = CreateTestDistributedApplicationBuilder();

        var resourceBuilder = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "my-command",
                "Run command",
                "dotnet",
                ["--version"],
                new ProcessCommandOptions
                {
                    Description = "Command description",
                    ConfirmationMessage = "Are you sure?",
                    IconName = "Command",
                    IconVariant = IconVariant.Filled,
                    IsHighlighted = true,
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "target",
                            InputType = InputType.Text
                        }
                    ]
                });

        var command = resourceBuilder.Resource.Annotations.OfType<ResourceCommandAnnotation>().Single();

        Assert.Equal("my-command", command.Name);
        Assert.Equal("Run command", command.DisplayName);
        Assert.Equal("Command description", command.DisplayDescription);
        Assert.Equal("Are you sure?", command.ConfirmationMessage);
        Assert.Equal("Command", command.IconName);
        Assert.Equal(IconVariant.Filled, command.IconVariant);
        Assert.True(command.IsHighlighted);
        var argument = Assert.Single(command.Arguments);
        Assert.Equal("target", argument.Name);
    }

    [Fact]
    public async Task WithProcessCommandExport_AddsResourceCommandAnnotationAndRunsProcess()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(output: ["export-line"]);
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommandExport(
                "export-command",
                "Run export command",
                new ProcessCommandExportOptions
                {
                    ExecutablePath = "export-command-executable",
                    Arguments = ["--export"],
                    CommandOptions = new CommandOptions
                    {
                        Description = "Export command description",
                        ConfirmationMessage = "Run export command?",
                        IconName = "Command",
                        IconVariant = IconVariant.Filled,
                        IsHighlighted = true
                    },
                    MaxOutputLineCount = 5,
                    DisplayImmediately = false
                });

        var command = resource.Resource.Annotations.OfType<ResourceCommandAnnotation>().Single();

        Assert.Equal("export-command", command.Name);
        Assert.Equal("Run export command", command.DisplayName);
        Assert.Equal("Export command description", command.DisplayDescription);
        Assert.Equal("Run export command?", command.ConfirmationMessage);
        Assert.Equal("Command", command.IconName);
        Assert.Equal(IconVariant.Filled, command.IconVariant);
        Assert.True(command.IsHighlighted);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "export-command").DefaultTimeout();

        Assert.True(result.Success);
        Assert.Equal(CommandResultFormat.Text, result.Data?.Format);
        Assert.False(result.Data?.DisplayImmediately);
        Assert.Contains("export-line", result.Data?.Value);

        var processSpec = Assert.Single(processRunner.ProcessSpecs);
        Assert.Equal("export-command-executable", processSpec.ExecutablePath);
        Assert.Equal(["--export"], processSpec.ArgumentList);
        Assert.Equal(5, processSpec.RetainedOutputLineCount);
        Assert.True(processSpec.ResolveExecutablePath);
        Assert.False(processSpec.ThrowOnNonZeroReturnCode);
    }

    [Fact]
    public async Task WithProcessCommandExport_CreateProcessSpecReceivesExecutionContextAndArguments()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(output: ["options-callback-line"]);
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        ExecuteCommandContext? capturedContext = null;
        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommandExport(
                "options-callback-command",
                "Run options callback command",
                new ProcessCommandExportOptions
                {
                    CreateProcessSpec = context =>
                    {
                        capturedContext = context;
                        var message = context.Arguments.GetString("message") ?? string.Empty;

                        return Task.FromResult(new ProcessCommandSpecExportData
                        {
                            ExecutablePath = "options-callback-executable",
                            Arguments = ["--message", message]
                        });
                    },
                    CommandOptions = new CommandOptions
                    {
                        Arguments =
                        [
                            new InteractionInput
                            {
                                Name = "message",
                                InputType = InputType.Text
                            }
                        ]
                    }
                });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var arguments = new InteractionInputCollection(
        [
            new InteractionInput
            {
                Name = "message",
                InputType = InputType.Text,
                Value = "hello-from-options-callback"
            }
        ]);

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "options-callback-command", arguments).DefaultTimeout();

        Assert.True(result.Success);
        Assert.Contains("options-callback-line", result.Data?.Value);
        Assert.NotNull(capturedContext);
        Assert.Equal("hello-from-options-callback", capturedContext.Arguments.GetString("message"));

        var processSpec = Assert.Single(processRunner.ProcessSpecs);
        Assert.Equal("options-callback-executable", processSpec.ExecutablePath);
        Assert.Equal(["--message", "hello-from-options-callback"], processSpec.ArgumentList);
    }

    [Fact]
    public async Task WithProcessCommandFactoryExport_ProcessFactoryReceivesExecutionContextAndArguments()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(output: ["factory-line"]);
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        ExecuteCommandContext? capturedContext = null;
        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommandFactoryExport(
                "factory-command",
                "Run factory command",
                context =>
                {
                    capturedContext = context;
                    var message = context.Arguments.GetString("message") ?? string.Empty;

                    return Task.FromResult(new ProcessCommandSpecExportData
                    {
                        ExecutablePath = "factory-command-executable",
                        Arguments = ["--message", message],
                        WorkingDirectory = "/test/factory-working-directory",
                        EnvironmentVariables = new Dictionary<string, string>
                        {
                            ["PROCESS_COMMAND_FACTORY_VALUE"] = message
                        },
                        InheritEnvironmentVariables = false,
                        StandardInputContent = "from-factory-stdin",
                        KillEntireProcessTree = false
                    });
                },
                new ProcessCommandResultExportOptions
                {
                    CommandOptions = new CommandOptions
                    {
                        Description = "Factory command description",
                        Arguments =
                        [
                            new InteractionInput
                            {
                                Name = "message",
                                InputType = InputType.Text
                            }
                        ]
                    },
                    MaxOutputLineCount = 7,
                    DisplayImmediately = false
                });

        var command = resource.Resource.Annotations.OfType<ResourceCommandAnnotation>().Single();
        Assert.Equal("Factory command description", command.DisplayDescription);
        var argument = Assert.Single(command.Arguments);
        Assert.Equal("message", argument.Name);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var arguments = new InteractionInputCollection(
        [
            new InteractionInput
            {
                Name = "message",
                InputType = InputType.Text,
                Value = "hello-from-factory"
            }
        ]);

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "factory-command", arguments).DefaultTimeout();

        Assert.True(result.Success);
        Assert.Equal(CommandResultFormat.Text, result.Data?.Format);
        Assert.False(result.Data?.DisplayImmediately);
        Assert.Contains("factory-line", result.Data?.Value);
        Assert.NotNull(capturedContext);
        Assert.Equal(resource.Resource.Name, capturedContext.ResourceName);
        Assert.Equal("hello-from-factory", capturedContext.Arguments.GetString("message"));

        var processSpec = Assert.Single(processRunner.ProcessSpecs);
        Assert.Equal("factory-command-executable", processSpec.ExecutablePath);
        Assert.Equal(["--message", "hello-from-factory"], processSpec.ArgumentList);
        Assert.Equal("/test/factory-working-directory", processSpec.WorkingDirectory);
        Assert.Equal("hello-from-factory", processSpec.EnvironmentVariables["PROCESS_COMMAND_FACTORY_VALUE"]);
        Assert.False(processSpec.InheritEnv);
        Assert.Equal("from-factory-stdin", processSpec.StandardInputContent);
        Assert.False(processSpec.KillEntireProcessTree);
        Assert.Equal(7, processSpec.RetainedOutputLineCount);
    }

    [Fact]
    public async Task WithProcessCommandFactoryExport_SuccessExitCodes_TreatsConfiguredExitCodeAsSuccess()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(exitCode: 17, output: ["factory-accepted-output"]);
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommandFactoryExport(
                "factory-accepted-exit-code",
                "Factory accepted exit code",
                _ => Task.FromResult(new ProcessCommandSpecExportData
                {
                    ExecutablePath = "factory-executable"
                }),
                new ProcessCommandResultExportOptions
                {
                    SuccessExitCodes = [0, 17]
                });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "factory-accepted-exit-code").DefaultTimeout();

        Assert.True(result.Success);
        Assert.Contains("factory-accepted-output", result.Data?.Value);
    }

    [Fact]
    public async Task WithProcessCommandFactoryExport_NullProcessSpecFactoryResult_ReturnsFailure()
    {
        var processRunner = new TestProcessRunner();
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommandFactoryExport(
                "null-process-spec",
                "Null process spec",
                _ => Task.FromResult<ProcessCommandSpecExportData>(null!));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "null-process-spec").DefaultTimeout();

        Assert.False(result.Success);
        Assert.Contains("factory returned null", result.Message);
        Assert.Empty(processRunner.ProcessSpecs);
    }

    [Fact]
    public async Task WithProcessCommand_ProcessFactoryReceivesExecutionContextAndArguments()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(output: ["hello-from-argument"]);
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        ExecuteCommandContext? capturedContext = null;
        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "echo-argument",
                "Echo argument",
                context =>
                {
                    capturedContext = context;
                    var message = context.Arguments.GetString("message");
                    return CreateProcessCommandSpec(arguments: [message ?? string.Empty]);
                },
                new ProcessCommandOptions
                {
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "message",
                            InputType = InputType.Text
                        }
                    ]
                });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var arguments = new InteractionInputCollection(
        [
            new InteractionInput
            {
                Name = "message",
                InputType = InputType.Text,
                Value = "hello-from-argument"
            }
        ]);

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "echo-argument", arguments).DefaultTimeout();

        Assert.True(result.Success);
        Assert.Equal(CommandResultFormat.Text, result.Data?.Format);
        Assert.Contains("hello-from-argument", result.Data?.Value);
        Assert.NotNull(capturedContext);
        Assert.Equal(resource.Resource.Name, capturedContext.ResourceName);
        Assert.NotNull(capturedContext.ServiceProvider);
        Assert.Equal("hello-from-argument", capturedContext.Arguments.GetString("message"));
        Assert.NotNull(capturedContext.Logger);

        var processSpec = Assert.Single(processRunner.ProcessSpecs);
        Assert.Equal(["hello-from-argument"], processSpec.ArgumentList);
    }

    [Fact]
    public async Task WithProcessCommand_BackchannelNamedArgumentsFlowToProcessFactoryAndResultContext()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(output: ["hello-from-backchannel"]);
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        ProcessCommandResultContext? capturedResultContext = null;
        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "echo-argument",
                "Echo argument",
                context =>
                {
                    var message = context.Arguments.GetString("message");
                    return CreateProcessCommandSpec(arguments: ["--message", message ?? string.Empty]);
                },
                new ProcessCommandOptions
                {
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "message",
                            InputType = InputType.Text
                        }
                    ],
                    GetCommandResult = resultContext =>
                    {
                        capturedResultContext = resultContext;
                        return Task.FromResult(CommandResults.Success("received argument", resultContext.Arguments.GetString("message")!));
                    }
                });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var response = await target.ExecuteResourceCommandAsync(new ExecuteResourceCommandRequest
        {
            ResourceName = resource.Resource.Name,
            CommandName = "echo-argument",
            Arguments = JsonSerializer.SerializeToNode(new
            {
                message = "hello-from-backchannel"
            })
        }).DefaultTimeout();

        Assert.True(response.Success, response.Message);
        Assert.Equal("received argument", response.Message);
        Assert.Equal("hello-from-backchannel", response.Value?.Value);

        var processSpec = Assert.Single(processRunner.ProcessSpecs);
        Assert.Equal(["--message", "hello-from-backchannel"], processSpec.ArgumentList);

        Assert.NotNull(capturedResultContext);
        Assert.Equal("hello-from-backchannel", capturedResultContext.Arguments.GetString("message"));
    }

    [Fact]
    public async Task WithProcessCommand_AwaitsAsyncProcessFactory()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(output: ["async-factory-output"]);
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);
        var factoryAwaited = false;

        async ValueTask<ProcessCommandSpec> CreateSpecAsync(ExecuteCommandContext _)
        {
            await Task.Yield();
            factoryAwaited = true;

            return CreateProcessCommandSpec(arguments: ["from-async-factory"]);
        }

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "async-factory",
                "Async factory",
                CreateSpecAsync);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "async-factory").DefaultTimeout();

        Assert.True(result.Success);
        Assert.True(factoryAwaited);
        Assert.Contains("async-factory-output", result.Data?.Value);
        var processSpec = Assert.Single(processRunner.ProcessSpecs);
        Assert.Equal(["from-async-factory"], processSpec.ArgumentList);
    }

    [Fact]
    public async Task WithProcessCommand_ExecutesFreshProcessForEachInvocation()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(output: ["first-output"]);
        processRunner.EnqueueResult(exitCode: 3, output: ["second-output"]);
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);
        var invocationCount = 0;

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "repeat",
                "Repeat",
                _ =>
                {
                    invocationCount++;

                    return CreateProcessCommandSpec(
                        executablePath: $"test-command-{invocationCount}",
                        arguments: [$"arg-{invocationCount}"]);
                });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var firstResult = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "repeat").DefaultTimeout();
        var secondResult = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "repeat").DefaultTimeout();

        Assert.True(firstResult.Success);
        Assert.Contains("first-output", firstResult.Data?.Value);
        Assert.False(secondResult.Success);
        Assert.Contains("exited with code 3", secondResult.Message);
        Assert.Contains("configured success exit codes [0]", secondResult.Message);
        Assert.Contains("second-output", secondResult.Data?.Value);

        Assert.Collection(
            processRunner.ProcessSpecs,
            processSpec =>
            {
                Assert.Equal("test-command-1", processSpec.ExecutablePath);
                Assert.Equal(["arg-1"], processSpec.ArgumentList);
            },
            processSpec =>
            {
                Assert.Equal("test-command-2", processSpec.ExecutablePath);
                Assert.Equal(["arg-2"], processSpec.ArgumentList);
            });
    }

    [Fact]
    public async Task WithProcessCommand_ReturnsBoundedOutput()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(output: ["line-3", "line-4", "line-5"], totalOutputLineCount: 5);
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "many-lines",
                "Many lines",
                _ => CreateProcessCommandSpec(),
                new ProcessCommandOptions
                {
                    MaxOutputLineCount = 3
                });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "many-lines").DefaultTimeout();

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(CommandResultFormat.Text, result.Data.Format);
        Assert.True(result.Data.DisplayImmediately);
        Assert.Contains("Command output truncated: showing last 3 of 5 lines.", result.Data.Value);
        Assert.DoesNotContain("line-1", result.Data.Value);
        Assert.DoesNotContain("line-2", result.Data.Value);
        Assert.Contains("line-3", result.Data.Value);
        Assert.Contains("line-5", result.Data.Value);
    }

    [Fact]
    public async Task WithProcessCommand_DisplayImmediately_CanBeDisabled()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(output: ["display-immediately-output"]);
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "display-immediately",
                "Display immediately",
                _ => CreateProcessCommandSpec(),
                new ProcessCommandOptions
                {
                    DisplayImmediately = false
                });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "display-immediately").DefaultTimeout();

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.False(result.Data.DisplayImmediately);
        Assert.Contains("display-immediately-output", result.Data.Value);
    }

    [Fact]
    public async Task WithProcessCommand_SuccessWithoutOutput_ReturnsNoResultData()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult();
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "no-output",
                "No output",
                _ => CreateProcessCommandSpec());

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "no-output").DefaultTimeout();

        Assert.True(result.Success);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task WithProcessCommand_NonZeroExitWithoutOutput_ReturnsFailureWithoutResultData()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(exitCode: 9);
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "fail-no-output",
                "Fail without output",
                _ => CreateProcessCommandSpec());

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "fail-no-output").DefaultTimeout();

        Assert.False(result.Success);
        Assert.Contains("exited with code 9", result.Message);
        Assert.Contains("configured success exit codes [0]", result.Message);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task WithProcessCommand_SuccessExitCodes_TreatsConfiguredExitCodeAsSuccess()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(exitCode: 5, output: ["accepted-output"]);
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "accepted-exit-code",
                "Accepted exit code",
                _ => CreateProcessCommandSpec(),
                new ProcessCommandOptions
                {
                    SuccessExitCodes = [0, 5]
                });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "accepted-exit-code").DefaultTimeout();

        Assert.True(result.Success);
        Assert.Contains("accepted-output", result.Data?.Value);
    }

    [Fact]
    public void WithProcessCommand_EmptySuccessExitCodes_ThrowsWhenConfiguringOptions()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ProcessCommandOptions
            {
                SuccessExitCodes = []
            });

        Assert.Contains("At least one process command success exit code must be specified.", exception.Message);
    }

    [Fact]
    public async Task WithProcessCommand_NonZeroExitWithOutput_HonorsDisplayImmediatelyOption()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(exitCode: 2, output: ["failure-output"]);
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "failure-display-immediately",
                "Failure display immediately",
                _ => CreateProcessCommandSpec(),
                new ProcessCommandOptions
                {
                    DisplayImmediately = false
                });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "failure-display-immediately").DefaultTimeout();

        Assert.False(result.Success);
        Assert.NotNull(result.Data);
        Assert.False(result.Data.DisplayImmediately);
        Assert.Contains("failure-output", result.Data.Value);
    }

    [Fact]
    public async Task WithProcessCommand_StreamsStdoutAndStderrToResourceLoggerAndResult()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(output: ["stdout-line"], error: ["stderr-line"]);
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "log-output",
                "Log output",
                _ => CreateProcessCommandSpec());

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "log-output").DefaultTimeout();

        Assert.True(result.Success);
        Assert.Contains("stdout-line", result.Data?.Value);
        Assert.Contains("stderr-line", result.Data?.Value);

        var loggerService = app.Services.GetRequiredService<ResourceLoggerService>();
        var logs = await ConsoleLoggingTestHelpers.WatchForLogsAsync(loggerService, 4, resource.Resource).DefaultTimeout();
        var logContents = logs.Select(log => log.Content).ToArray();

        Assert.Contains(logContents, log => log.Contains("(stdout): stdout-line", StringComparison.Ordinal));
        Assert.Contains(logContents, log => log.Contains("(stderr): stderr-line", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithProcessCommand_GetCommandResult_CanCustomizeResultFromProcessOutput()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(
            exitCode: 42,
            outputEvents:
            [
                Output("""{"status":"custom"}"""),
                Error("diagnostic-line")
            ]);
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        ProcessCommandResultContext? capturedContext = null;
        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "custom-result",
                "Custom result",
                _ => CreateProcessCommandSpec(arguments: ["custom-argument"]),
                new ProcessCommandOptions
                {
                    SuccessExitCodes = [0],
                    GetCommandResult = context =>
                    {
                        capturedContext = context;

                        return Task.FromResult(new ExecuteCommandResult
                        {
                            Success = true,
                            Data = new CommandResultData
                            {
                                Value = context.Output[0],
                                Format = CommandResultFormat.Json,
                                DisplayImmediately = false
                            }
                        });
                    }
                });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "custom-result").DefaultTimeout();

        Assert.True(result.Success);
        Assert.Equal(CommandResultFormat.Json, result.Data?.Format);
        Assert.False(result.Data?.DisplayImmediately);
        Assert.Equal("""{"status":"custom"}""", result.Data?.Value);

        Assert.NotNull(capturedContext);
        Assert.Equal(42, capturedContext.ExitCode);
        Assert.Equal(resource.Resource.Name, capturedContext.ResourceName);
        Assert.NotNull(capturedContext.ServiceProvider);
        Assert.NotNull(capturedContext.Logger);
        Assert.Equal(["custom-argument"], capturedContext.ProcessCommandSpec.Arguments);
        Assert.Equal(["{\"status\":\"custom\"}", "diagnostic-line"], capturedContext.Output);
        Assert.Equal(2, capturedContext.TotalOutputLineCount);
        var formattedOutput = capturedContext.GetFormattedOutput(maxLines: 1);
        Assert.Contains("Command output truncated: showing last 1 of 2 lines.", formattedOutput);
        Assert.Contains("diagnostic-line", formattedOutput);
    }

    [Fact]
    public async Task WithProcessCommand_ReturnsOutputInRunnerObservedOrder()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(
            outputEvents:
            [
                Output("stdout-1"),
                Error("stderr-1"),
                Output("stdout-2")
            ]);
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "ordered-output",
                "Ordered output",
                _ => CreateProcessCommandSpec());

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "ordered-output").DefaultTimeout();

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(
            ["stdout-1", "stderr-1", "stdout-2"],
            result.Data.Value.Split(Environment.NewLine, StringSplitOptions.None));
    }

    [Fact]
    public async Task WithProcessCommand_NonZeroExit_ReturnsFailureWithBoundedOutput()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(exitCode: 7, output: ["failed-line"]);
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "fail",
                "Fail",
                _ => CreateProcessCommandSpec(),
                new ProcessCommandOptions
                {
                    MaxOutputLineCount = 5
                });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "fail").DefaultTimeout();

        Assert.False(result.Success);
        Assert.False(result.Canceled);
        Assert.NotEqual("Unhandled exception thrown.", result.Message);
        Assert.Contains("exited with code 7", result.Message);
        Assert.Contains("configured success exit codes [0]", result.Message);
        Assert.Equal(CommandResultFormat.Text, result.Data?.Format);
        Assert.True(result.Data?.DisplayImmediately);
        Assert.Contains("failed-line", result.Data?.Value);
    }

    [Fact]
    public async Task WithProcessCommand_PreservesArgumentValues()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult();
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);
        string[] expectedArguments =
        [
            "value with spaces",
            "quote\"value",
            "",
            "$HOME && rm -rf /",
            "semi;colon"
        ];

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "argument-fidelity",
                "Argument fidelity",
                _ => CreateProcessCommandSpec(arguments: expectedArguments));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "argument-fidelity").DefaultTimeout();

        Assert.True(result.Success);
        var processSpec = Assert.Single(processRunner.ProcessSpecs);
        Assert.Equal(expectedArguments, processSpec.ArgumentList);
        Assert.Null(processSpec.Arguments);
    }

    [Fact]
    public async Task WithProcessCommand_StandardInputContent_FlowsToProcessSpec()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult();
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "stdin",
                "Standard input",
                _ => CreateProcessCommandSpec(standardInputContent: "from-standard-input"));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "stdin").DefaultTimeout();

        Assert.True(result.Success);
        var processSpec = Assert.Single(processRunner.ProcessSpecs);
        Assert.Equal("from-standard-input", processSpec.StandardInputContent);
    }

    [Fact]
    public async Task WithProcessCommand_EnablesExecutablePathResolution()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult();
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "path-executable",
                "Path executable",
                _ => CreateProcessCommandSpec("test-command-name"));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "path-executable").DefaultTimeout();

        Assert.True(result.Success);
        var processSpec = Assert.Single(processRunner.ProcessSpecs);
        Assert.Equal("test-command-name", processSpec.ExecutablePath);
        Assert.True(processSpec.ResolveExecutablePath);
    }

    [Fact]
    public async Task WithProcessCommand_WorkingDirectory_FlowsToProcessSpec()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult();
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "working-directory",
                "Working directory",
                _ => CreateProcessCommandSpec(workingDirectory: "/test/working-directory"));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "working-directory").DefaultTimeout();

        Assert.True(result.Success);
        var processSpec = Assert.Single(processRunner.ProcessSpecs);
        Assert.Equal("/test/working-directory", processSpec.WorkingDirectory);
    }

    [Fact]
    public async Task WithProcessCommand_EnvironmentVariables_FlowToProcessSpec()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult();
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var environmentVariables = new Dictionary<string, string>
        {
            ["PROCESS_COMMAND_TEST_VALUE"] = "from-environment"
        };

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "environment",
                "Environment",
                _ => CreateProcessCommandSpec(environmentVariables: environmentVariables));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "environment").DefaultTimeout();

        Assert.True(result.Success);
        var processSpec = Assert.Single(processRunner.ProcessSpecs);
        Assert.Equal("from-environment", processSpec.EnvironmentVariables["PROCESS_COMMAND_TEST_VALUE"]);
    }

    [Fact]
    public async Task WithProcessCommand_InvalidEnvironmentVariableName_ReturnsFailureWithoutCreatingProcess()
    {
        var processRunner = new TestProcessRunner();
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "invalid-environment-name",
                "Invalid environment name",
                _ => CreateProcessCommandSpec(
                    environmentVariables: new Dictionary<string, string>
                    {
                        [""] = "value"
                    }));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "invalid-environment-name").DefaultTimeout();

        Assert.False(result.Success);
        Assert.Contains("environment variables require non-empty names", result.Message);
        Assert.Empty(processRunner.ProcessSpecs);
    }

    [Fact]
    public async Task WithProcessCommand_NullEnvironmentVariableValue_ReturnsFailureWithoutCreatingProcess()
    {
        var processRunner = new TestProcessRunner();
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "null-environment-value",
                "Null environment value",
                _ => CreateProcessCommandSpec(
                    environmentVariables: new Dictionary<string, string>
                    {
                        ["PROCESS_COMMAND_TEST_VALUE"] = null!
                    }));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "null-environment-value").DefaultTimeout();

        Assert.False(result.Success);
        Assert.Contains("environment variable 'PROCESS_COMMAND_TEST_VALUE' requires a value", result.Message);
        Assert.Empty(processRunner.ProcessSpecs);
    }

    [Fact]
    public async Task WithProcessCommandExport_ProcessOptionsFlowToProcessSpec()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(output: ["export-options-output"]);
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommandExport(
                "export-options",
                "Export options",
                new ProcessCommandExportOptions
                {
                    ExecutablePath = "export-executable",
                    Arguments = ["--from-export"],
                    WorkingDirectory = "/test/export-working-directory",
                    EnvironmentVariables = new Dictionary<string, string>
                    {
                        ["PROCESS_COMMAND_EXPORT_VALUE"] = "from-export-environment"
                    },
                    InheritEnvironmentVariables = false,
                    StandardInputContent = "from-export-stdin",
                    KillEntireProcessTree = false,
                    MaxOutputLineCount = 10,
                    DisplayImmediately = false,
                    SuccessExitCodes = [0, 17]
                });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "export-options").DefaultTimeout();

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.False(result.Data.DisplayImmediately);
        Assert.Contains("export-options-output", result.Data.Value);

        var processSpec = Assert.Single(processRunner.ProcessSpecs);
        Assert.Equal("export-executable", processSpec.ExecutablePath);
        Assert.Equal(["--from-export"], processSpec.ArgumentList);
        Assert.Equal("/test/export-working-directory", processSpec.WorkingDirectory);
        Assert.Equal("from-export-environment", processSpec.EnvironmentVariables["PROCESS_COMMAND_EXPORT_VALUE"]);
        Assert.False(processSpec.InheritEnv);
        Assert.Equal("from-export-stdin", processSpec.StandardInputContent);
        Assert.False(processSpec.KillEntireProcessTree);
        Assert.Equal(10, processSpec.RetainedOutputLineCount);
    }

    [Fact]
    public async Task WithProcessCommandExport_SuccessExitCodes_TreatsConfiguredExitCodeAsSuccess()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(exitCode: 17, output: ["export-accepted-output"]);
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommandExport(
                "export-accepted-exit-code",
                "Export accepted exit code",
                new ProcessCommandExportOptions
                {
                    ExecutablePath = "export-executable",
                    SuccessExitCodes = [0, 17]
                });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "export-accepted-exit-code").DefaultTimeout();

        Assert.True(result.Success);
        Assert.Contains("export-accepted-output", result.Data?.Value);
    }

    [Fact]
    public async Task WithProcessCommandExport_UnspecifiedBooleanOptionsUseProcessCommandSpecDefaults()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult();
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommandExport(
                "export-default-options",
                "Export default options",
                new ProcessCommandExportOptions
                {
                    ExecutablePath = "export-executable"
                });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "export-default-options").DefaultTimeout();

        Assert.True(result.Success);

        var processSpec = Assert.Single(processRunner.ProcessSpecs);
        Assert.True(processSpec.InheritEnv);
        Assert.True(processSpec.KillEntireProcessTree);
    }

    [Fact]
    public async Task WithProcessCommandExport_InvalidProcessOptions_ReturnsFailureWithoutCreatingProcess()
    {
        var invalidOptions = new (ProcessCommandExportOptions Options, string ExpectedMessage)[]
        {
            (
                new ProcessCommandExportOptions
                {
                    ExecutablePath = ""
                },
                "requires a non-empty executable path"),
            (
                new ProcessCommandExportOptions
                {
                    ExecutablePath = "test-command",
                    Arguments = ["valid", null!]
                },
                "arguments cannot contain null values"),
            (
                new ProcessCommandExportOptions
                {
                    ExecutablePath = "test-command",
                    EnvironmentVariables = new Dictionary<string, string>
                    {
                        [" "] = "value"
                    }
                },
                "environment variables require non-empty names"),
            (
                new ProcessCommandExportOptions
                {
                    ExecutablePath = "test-command",
                    EnvironmentVariables = new Dictionary<string, string>
                    {
                        ["PROCESS_COMMAND_EXPORT_VALUE"] = null!
                    }
                },
                "environment variable 'PROCESS_COMMAND_EXPORT_VALUE' requires a value")
        };

        for (var i = 0; i < invalidOptions.Length; i++)
        {
            var (options, expectedMessage) = invalidOptions[i];
            var processRunner = new TestProcessRunner();
            using var builder = CreateTestDistributedApplicationBuilder(processRunner);
            var resource = builder.AddResource(new CustomResource($"resource-{i}"))
                .WithProcessCommandExport(
                    $"invalid-export-{i}",
                    "Invalid export",
                    options);

            using var app = builder.Build();
            await app.StartAsync().DefaultTimeout();

            var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, $"invalid-export-{i}").DefaultTimeout();

            Assert.False(result.Success);
            Assert.Contains(expectedMessage, result.Message);
            Assert.Empty(processRunner.ProcessSpecs);
        }
    }

    [Fact]
    public void WithProcessCommandExport_InvalidMaxOutputLineCount_ThrowsWhenAddingCommand()
    {
        using var builder = CreateTestDistributedApplicationBuilder();

        var exception = Assert.Throws<DistributedApplicationException>(() =>
            builder.AddResource(new CustomResource("resource"))
                .WithProcessCommandExport(
                    "invalid-output-line-count",
                    "Invalid output line count",
                    new ProcessCommandExportOptions
                    {
                        ExecutablePath = "test-command",
                        MaxOutputLineCount = 0
                    }));

        Assert.Contains("output line count must be greater than zero", exception.Message);
    }

    [Fact]
    public async Task WithProcessCommandExport_EmptySuccessExitCodesUseDefault()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(exitCode: 1);
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommandExport(
                "default-success-exit-codes",
                "Default success exit codes",
                new ProcessCommandExportOptions
                {
                    ExecutablePath = "test-command",
                    SuccessExitCodes = []
                });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "default-success-exit-codes").DefaultTimeout();

        Assert.False(result.Success);
        Assert.Contains("configured success exit codes [0]", result.Message);
    }

    [Fact]
    public async Task WithProcessCommand_CanDisableEnvironmentInheritance()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult();
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "environment-inheritance",
                "Environment inheritance",
                _ => CreateProcessCommandSpec(
                    arguments: ["inherited-variable"],
                    inheritEnvironmentVariables: false));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "environment-inheritance").DefaultTimeout();

        Assert.True(result.Success);
        var processSpec = Assert.Single(processRunner.ProcessSpecs);
        Assert.False(processSpec.InheritEnv);
    }

    [Fact]
    public async Task WithProcessCommand_ProcessStartFailure_ReturnsFailure()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueException(new InvalidOperationException("process start failed"));
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "missing",
                "Missing",
                "aspire-process-command-missing-executable");

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "missing").DefaultTimeout();

        Assert.False(result.Success);
        Assert.False(result.Canceled);
        Assert.Equal("process start failed", result.Message);
        Assert.NotEqual("Unhandled exception thrown.", result.Message);
    }

    [Fact]
    public async Task WithProcessCommand_ProcessTaskFailure_ReturnsFailureAndDisposesProcess()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueuePending(Task.FromException<ProcessResult>(new InvalidOperationException("process failed after start")));
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "process-task-failure",
                "Process task failure",
                "test-command");

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "process-task-failure").DefaultTimeout();

        Assert.False(result.Success);
        Assert.False(result.Canceled);
        Assert.Equal("process failed after start", result.Message);
        Assert.Contains(processRunner.Disposables, disposable => disposable.DisposeCallCount > 0);
    }

    [Fact]
    public async Task WithProcessCommand_NullProcessSpecFactoryResult_ReturnsFailure()
    {
        using var builder = CreateTestDistributedApplicationBuilder();
        Func<ExecuteCommandContext, ValueTask<ProcessCommandSpec>> processSpecFactory = _ => new ValueTask<ProcessCommandSpec>((ProcessCommandSpec)null!);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "null-process-spec",
                "Null process spec",
                processSpecFactory);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "null-process-spec").DefaultTimeout();

        Assert.False(result.Success);
        Assert.Contains("factory returned null", result.Message);
    }

    [Fact]
    public async Task WithProcessCommand_ProcessSpecFactoryException_ReturnsFailureWithoutCreatingProcess()
    {
        var processRunner = new TestProcessRunner();
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        static ProcessCommandSpec CreateSpec(ExecuteCommandContext _)
        {
            throw new InvalidOperationException("factory failed");
        }

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "factory-exception",
                "Factory exception",
                CreateSpec);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "factory-exception").DefaultTimeout();

        Assert.False(result.Success);
        Assert.Equal("factory failed", result.Message);
        Assert.Empty(processRunner.ProcessSpecs);
    }

    [Fact]
    public async Task WithProcessCommand_NullArgument_ReturnsFailure()
    {
        using var builder = CreateTestDistributedApplicationBuilder();

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "null-argument",
                "Null argument",
                _ => new ProcessCommandSpec("test-command")
                {
                    Arguments = [null!]
                });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "null-argument").DefaultTimeout();

        Assert.False(result.Success);
        Assert.Contains("arguments cannot contain null values", result.Message);
    }

    [Fact]
    public async Task WithProcessCommand_ValidationFailure_DoesNotCreateProcess()
    {
        var processRunner = new TestProcessRunner();
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);
        var processSpecFactoryCalled = false;

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "validation-failure",
                "Validation failure",
                _ =>
                {
                    processSpecFactoryCalled = true;
                    return CreateProcessCommandSpec();
                },
                new ProcessCommandOptions
                {
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "required",
                            InputType = InputType.Text,
                            Required = true
                        }
                    ]
                });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "validation-failure").DefaultTimeout();

        Assert.False(result.Success);
        Assert.False(processSpecFactoryCalled);
        Assert.Empty(processRunner.ProcessSpecs);
        Assert.Equal("Command argument validation failed.", result.Message);
        Assert.NotNull(result.InvalidArguments);
        var invalidArgument = Assert.Single(result.InvalidArguments);
        Assert.Equal("required", invalidArgument.Name);
        Assert.Equal("Value is required.", Assert.Single(invalidArgument.ValidationErrors));
    }

    [Fact]
    public async Task WithProcessCommand_Cancellation_ReturnsCanceledResultAndDisposesProcess()
    {
        var processRunner = new TestProcessRunner();
        var pendingProcessResult = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        processRunner.EnqueuePending(pendingProcessResult.Task);
        using var builder = CreateTestDistributedApplicationBuilder(processRunner);

        var resource = builder.AddResource(new CustomResource("resource"))
            .WithProcessCommand(
                "wait",
                "Wait",
                _ => CreateProcessCommandSpec());

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        using var cts = new CancellationTokenSource();
        var commandTask = app.ResourceCommands.ExecuteCommandAsync(resource.Resource, "wait", cts.Token);

        await processRunner.RunStarted.Task.DefaultTimeout();
        await cts.CancelAsync();

        var result = await commandTask.DefaultTimeout();

        Assert.False(result.Success);
        Assert.True(result.Canceled);
        var disposable = Assert.Single(processRunner.Disposables);
        Assert.Equal(1, disposable.DisposeCallCount);
    }

    private IDistributedApplicationTestingBuilder CreateTestDistributedApplicationBuilder(TestProcessRunner? processRunner = null)
    {
        var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Services.AddSingleton<IProcessRunner>(processRunner ?? new TestProcessRunner());

        return builder;
    }

    private static ProcessCommandSpec CreateProcessCommandSpec(
        string executablePath = "test-command",
        IReadOnlyList<string>? arguments = null,
        string? workingDirectory = null,
        IDictionary<string, string>? environmentVariables = null,
        bool inheritEnvironmentVariables = true,
        string? standardInputContent = null,
        bool killEntireProcessTree = true)
    {
        return new ProcessCommandSpec(executablePath)
        {
            Arguments = arguments ?? [],
            WorkingDirectory = workingDirectory,
            EnvironmentVariables = environmentVariables ?? new Dictionary<string, string>(),
            InheritEnvironmentVariables = inheritEnvironmentVariables,
            StandardInputContent = standardInputContent,
            KillEntireProcessTree = killEntireProcessTree
        };
    }

    private sealed class TestProcessRunner : IProcessRunner
    {
        private readonly Queue<TestProcessRun> _runs = [];
        private readonly List<TestProcessDisposable> _disposables = [];

        public List<ProcessSpec> ProcessSpecs { get; } = [];

        public IReadOnlyList<TestProcessDisposable> Disposables => _disposables;

        public TaskCompletionSource<ProcessSpec> RunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void EnqueueResult(
            int exitCode = 0,
            IReadOnlyList<string>? output = null,
            IReadOnlyList<string>? error = null,
            int? totalOutputLineCount = null,
            IReadOnlyList<TestProcessOutput>? outputEvents = null)
        {
            _runs.Enqueue(TestProcessRun.Result(exitCode, output, error, totalOutputLineCount, outputEvents));
        }

        public void EnqueueException(Exception exception)
        {
            _runs.Enqueue(TestProcessRun.Failed(exception));
        }

        public void EnqueuePending(Task<ProcessResult> processResult)
        {
            _runs.Enqueue(TestProcessRun.Pending(processResult));
        }

        public (Task<ProcessResult>, IAsyncDisposable) Run(ProcessSpec processSpec)
        {
            ProcessSpecs.Add(processSpec);
            RunStarted.TrySetResult(processSpec);

            var disposable = new TestProcessDisposable();
            _disposables.Add(disposable);

            var run = _runs.Count > 0 ? _runs.Dequeue() : TestProcessRun.Result();
            if (run.FailureException is { } exception)
            {
                throw exception;
            }

            if (run.PendingResult is { } pendingResult)
            {
                return (pendingResult, disposable);
            }

            foreach (var output in run.OutputEvents)
            {
                if (output.IsError)
                {
                    processSpec.OnErrorData?.Invoke(output.Value);
                }
                else
                {
                    processSpec.OnOutputData?.Invoke(output.Value);
                }
            }

            var processOutput = run.OutputEvents.Select(static output => output.Value).ToArray();
            var processResult = new ProcessResult(run.ExitCode, processOutput, run.TotalOutputLineCount);

            return (Task.FromResult(processResult), disposable);
        }
    }

    private sealed record TestProcessRun(
        int ExitCode,
        IReadOnlyList<TestProcessOutput> OutputEvents,
        int? TotalOutputLineCount,
        Exception? FailureException,
        Task<ProcessResult>? PendingResult)
    {
        public static TestProcessRun Result(
            int exitCode = 0,
            IReadOnlyList<string>? output = null,
            IReadOnlyList<string>? error = null,
            int? totalOutputLineCount = null,
            IReadOnlyList<TestProcessOutput>? outputEvents = null)
        {
            outputEvents ??=
            [
                .. (output ?? Array.Empty<string>()).Select(Output),
                .. (error ?? Array.Empty<string>()).Select(Error)
            ];

            return new TestProcessRun(exitCode, outputEvents, totalOutputLineCount, null, null);
        }

        public static TestProcessRun Failed(Exception exception)
        {
            return new TestProcessRun(0, [], null, exception, null);
        }

        public static TestProcessRun Pending(Task<ProcessResult> pendingResult)
        {
            return new TestProcessRun(0, [], null, null, pendingResult);
        }
    }

    private static TestProcessOutput Output(string value) => new(false, value);

    private static TestProcessOutput Error(string value) => new(true, value);

    private sealed record TestProcessOutput(bool IsError, string Value);

    private sealed class TestProcessDisposable : IAsyncDisposable
    {
        public int DisposeCallCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;

            return ValueTask.CompletedTask;
        }
    }

    private sealed class CustomResource(string name) : Resource(name)
    {
    }
}

#pragma warning restore ASPIREPROCESSCOMMAND001
#pragma warning restore ASPIREINTERACTION001
