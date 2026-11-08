// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace Aspire.Cli.Tests.Interaction;

public class ExtensionInteractionServiceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task PromptForStringAsync_PreCancelledRequestNeverProbesOrPrompts()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var backchannel = new TestExtensionBackchannel
        {
            HasCapabilityAsyncCallback = (_, _) => throw new InvalidOperationException("Must not probe."),
            PromptForSecretStringAsyncCallback = (_, _, _, _) => throw new InvalidOperationException("Must not prompt.")
        };
        using var interaction = CreateExtensionInteractionService(workspace, backchannel);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            interaction.PromptForStringAsync("Bound origin", isSecret: true, logResponse: false, cancellationToken: cancellation.Token));
        await interaction.FlushAsync().DefaultTimeout();
    }

    [Theory]
    [InlineData("probe", false)]
    [InlineData("probe", true)]
    [InlineData("prompt", false)]
    [InlineData("prompt", true)]
    public async Task PromptForStringAsync_LinksRequestAndServiceCancellation(string stage, bool dispose)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken received = default;
        async Task<T> WaitForCancellationAsync<T>(CancellationToken token)
        {
            received = token;
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable.");
        }
        var backchannel = new TestExtensionBackchannel
        {
            HasCapabilityAsyncCallback = (_, token) => stage == "probe"
                ? WaitForCancellationAsync<bool>(token) : Task.FromResult(true),
            PromptForSecretStringAsyncCallback = (_, _, _, token) => WaitForCancellationAsync<string>(token)
        };
        using var interaction = CreateExtensionInteractionService(workspace, backchannel);
        using var caller = new CancellationTokenSource();
        var result = interaction.PromptForStringAsync("Bound origin", isSecret: true, logResponse: false, cancellationToken: caller.Token);
        await started.Task.DefaultTimeout();
        Assert.True(received.CanBeCanceled);
        if (dispose)
        {
            interaction.Dispose();
        }
        else
        {
            await caller.CancelAsync();
        }
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result.DefaultTimeout());
        Assert.True(received.IsCancellationRequested);
        Assert.Equal(received, exception.CancellationToken);
        if (dispose)
        {
            await interaction.PumpTask.DefaultTimeout();
        }
        else
        {
            await interaction.FlushAsync().DefaultTimeout();
        }
    }

    [Fact]
    public async Task PromptForStringAsync_CancelledQueuedRequestNeverOpensLater()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompts = 0;
        var backchannel = new TestExtensionBackchannel
        {
            HasCapabilityAsyncCallback = (_, _) => Task.FromResult(true),
            PromptForSecretStringAsyncCallback = (_, _, _, _) =>
            {
                prompts++;
                started.TrySetResult();
                return release.Task;
            }
        };
        using var interaction = CreateExtensionInteractionService(workspace, backchannel);
        var first = interaction.PromptForStringAsync("First", isSecret: true, logResponse: false);
        await started.Task.DefaultTimeout();
        using var caller = new CancellationTokenSource();
        var queued = interaction.PromptForStringAsync("Second", isSecret: true, logResponse: false, cancellationToken: caller.Token);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.DefaultTimeout());
        release.SetResult("synthetic");
        Assert.Equal("synthetic", await first.DefaultTimeout());
        await interaction.FlushAsync().DefaultTimeout();
        Assert.Equal(1, prompts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PromptForStringAsync_MaskedTerminalInputObservesLinkedCancellation(bool extensionPromptEnabled, bool dispose)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringBuilder();
        var console = new TestAnsiConsoleWithInput(
            AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Interactive = InteractionSupport.Yes,
                Out = new AnsiConsoleOutput(new StringWriter(output))
            }), async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return null;
            });
        var consoleInteraction = new ConsoleInteractionService(
            new ConsoleEnvironment(console, console, TextReader.Null), workspace.CreateExecutionContext(),
            TestHelpers.CreateInteractiveHostEnvironment(), new EnvironmentProcessPathProvider(),
            NullLoggerFactory.Instance, new ConsoleLogBufferContext());
        var backchannel = new TestExtensionBackchannel { HasCapabilityAsyncCallback = (_, _) => Task.FromResult(false) };
        using var interaction = new ExtensionInteractionService(
            consoleInteraction, backchannel, extensionPromptEnabled, NullLogger<ExtensionInteractionService>.Instance);
        using var caller = new CancellationTokenSource();
        var result = interaction.PromptForStringAsync("Bound origin:", isSecret: true, logResponse: false, cancellationToken: caller.Token);
        await started.Task.DefaultTimeout();
        if (dispose)
        {
            interaction.Dispose();
        }
        else
        {
            await caller.CancelAsync();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result.DefaultTimeout());
        if (dispose)
        {
            await interaction.PumpTask.DefaultTimeout();
        }
        else
        {
            await interaction.FlushAsync().DefaultTimeout();
        }
    }

    [Fact]
    public async Task PromptForStringAsync_DisposalCancelsQueuedWorkAndObservesLateFailure()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompts = 0;
        var backchannel = new TestExtensionBackchannel
        {
            HasCapabilityAsyncCallback = (_, _) => Task.FromResult(true),
            PromptForSecretStringAsyncCallback = (_, _, _, _) =>
            {
                prompts++;
                started.TrySetResult();
                return release.Task;
            }
        };
        using var interaction = CreateExtensionInteractionService(workspace, backchannel);
        var first = interaction.PromptForStringAsync("First", isSecret: true, logResponse: false);
        await started.Task.DefaultTimeout();
        var queued = interaction.PromptForStringAsync("Second", isSecret: true, logResponse: false);
        interaction.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.DefaultTimeout());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.DefaultTimeout());
        release.SetException(new InvalidOperationException("Late operation failure."));
        await interaction.PumpTask.DefaultTimeout();
        Assert.Equal(1, prompts);
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("none", true)]
    [InlineData("v1", false)]
    [InlineData("v1", true)]
    [InlineData("v2", false)]
    [InlineData("v2", true)]
    public async Task PromptForStringAsync_Secret_UsesMaskedPrompt(string capabilityVersion, bool required)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var output = new StringBuilder();
        var console = new TestAnsiConsoleWithInput(
            AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Interactive = InteractionSupport.Yes,
                Out = new AnsiConsoleOutput(new StringWriter(output))
            }),
            new StringReader("y\n"));
        var consoleInteractionService = new ConsoleInteractionService(
            new ConsoleEnvironment(console, console, TextReader.Null),
            workspace.CreateExecutionContext(),
            TestHelpers.CreateInteractiveHostEnvironment(),
            new EnvironmentProcessPathProvider(),
            NullLoggerFactory.Instance,
            new ConsoleLogBufferContext());
        var secretPromptCalls = 0;
        var regularPromptCalls = 0;
        var backchannel = new TestExtensionBackchannel
        {
            HasCapabilityAsyncCallback = (capability, _) =>
            {
                Assert.Equal(KnownCapabilities.CancellableSecretPrompts, capability);
                return Task.FromResult(capabilityVersion == "v2" ||
                    capabilityVersion == "v1" && capability == KnownCapabilities.SecretPrompts);
            },
            PromptForSecretStringAsyncCallback = (_, _, promptRequired, _) =>
            {
                secretPromptCalls++;
                Assert.Equal(required, promptRequired);
                return Task.FromResult("y");
            },
            PromptForStringAsyncCallback = (_, _, _, _, _) =>
            {
                regularPromptCalls++;
                return Task.FromResult("unmasked");
            }
        };
        using var interactionService = new ExtensionInteractionService(
            consoleInteractionService, backchannel, extensionPromptEnabled: true,
            NullLogger<ExtensionInteractionService>.Instance);

        var result = await interactionService.PromptForStringAsync("Secret:", isSecret: true, required: required).DefaultTimeout();

        Assert.Equal("y", result);
        Assert.Equal(0, regularPromptCalls);
        Assert.Equal(capabilityVersion == "v2" ? 1 : 0, secretPromptCalls);
        Assert.Equal(capabilityVersion == "v2" ? "" : $"Secret: *{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task CredentialPrompt_HelperDisconnectCancelsExtensionInput()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backchannel = new TestExtensionBackchannel
        {
            HasCapabilityAsyncCallback = (capability, _) => Task.FromResult(capability == KnownCapabilities.CancellableSecretPrompts),
            PromptForSecretStringAsyncCallback = async (prompt, _, required, token) =>
            {
                Assert.Equal(string.Format(InteractionServiceStrings.GitPasswordPrompt, "https://example.test"), prompt);
                Assert.True(required);
                opened.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                    throw new InvalidOperationException("Unreachable.");
                }
                finally
                {
                    cancelled.TrySetResult();
                }
            }
        };
        using var interaction = CreateExtensionInteractionService(workspace, backchannel);
        await using var session = new GitAskPassSession(new Uri("https://example.test"),
            (prompt, token) => GitCommandRunner.PromptForCredentialAsync(interaction, prompt, token),
            TestContext.Current.CancellationToken);
        var info = new ProcessStartInfo("git");
        session.Configure(info, "helper");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(info.Environment[GitAskPassProtocol.SocketVariable]!),
            TestContext.Current.CancellationToken);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await GitAskPassProtocol.WriteAsync(stream, "Password for 'https://private-user@example.test': ",
            GitAskPassProtocol.MaxPromptBytes, TestContext.Current.CancellationToken);
        await opened.Task.DefaultTimeout();
        socket.Dispose();
        await cancelled.Task.DefaultTimeout();
        await interaction.FlushAsync().DefaultTimeout();
    }

    [Fact]
    public async Task DisplaySourceTrustWarning_NotifiesExtensionOnlyOnce()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var messages = new List<string>();
        var backchannel = new TestExtensionBackchannel
        {
            DisplayMessageAsyncCallback = (_, message) =>
            {
                messages.Add(message);
                return Task.CompletedTask;
            }
        };
        using var interaction = CreateExtensionInteractionService(workspace, backchannel);
        var executionContext = workspace.CreateExecutionContext();

        interaction.DisplaySourceTrustWarning(executionContext);
        interaction.DisplaySourceTrustWarning(executionContext);
        await interaction.FlushAsync();

        Assert.Equal([SharedCommandStrings.SourceTrustWarning], messages);
    }

    [Fact]
    public async Task DisplayMessage_DoesNotRenderTerminalHyperlinksToDebugConsoleCapturedOutput()
    {
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        console.Profile.Capabilities.Links = true;
        console.Profile.Width = int.MaxValue;

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var logFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "cli [extension].log");
        var executionContext = workspace.CreateExecutionContext(logFilePath: logFilePath);
        var consoleInteractionService = new ConsoleInteractionService(
            new ConsoleEnvironment(console, console, TextReader.Null),
            executionContext,
            TestHelpers.CreateInteractiveHostEnvironment(),
            new EnvironmentProcessPathProvider(),
            NullLoggerFactory.Instance,
            new ConsoleLogBufferContext());
        var extensionInteractionService = new ExtensionInteractionService(
            consoleInteractionService,
            new TestExtensionBackchannel(),
            extensionPromptEnabled: false,
            logger: NullLogger<ExtensionInteractionService>.Instance);

        var fileLinkMarkup = MarkupHelpers.SafeFileLink(extensionInteractionService, logFilePath);
        extensionInteractionService.DisplayMessage(
            KnownEmojis.PageFacingUp,
            string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.SeeLogsAt, fileLinkMarkup),
            allowMarkup: true,
            consoleOverride: ConsoleOutput.Error);
        await extensionInteractionService.FlushAsync();

        var outputString = output.ToString();
        Assert.Contains(logFilePath, outputString);
        Assert.DoesNotContain("\u001b]8;", outputString);
        Assert.DoesNotContain("file://", outputString);
    }

    [Fact]
    public async Task DisplayError_WithActions_SendsStructuredActions()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var logFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.cli.log");
        IReadOnlyList<InteractionMessageAction>? capturedActions = null;
        var backchannel = new TestExtensionBackchannel
        {
            DisplayErrorWithActionsAsyncCallback = (_, actions) =>
            {
                capturedActions = actions;
                return Task.CompletedTask;
            }
        };
        using var interactionService = CreateExtensionInteractionService(workspace, backchannel);

        interactionService.DisplayError(
            "The project could not be built.",
            [
                InteractionMessageAction.OpenFile("Open CLI Log", logFilePath),
                InteractionMessageAction.ExecuteCommand("Retry", "aspire-vscode.retry")
            ]);
        await interactionService.FlushAsync();

        Assert.Collection(
            Assert.IsAssignableFrom<IReadOnlyList<InteractionMessageAction>>(capturedActions),
            action =>
            {
                Assert.Equal("Open CLI Log", action.DisplayName);
                Assert.Equal(logFilePath, action.FilePath);
                Assert.Null(action.Command);
            },
            action =>
            {
                Assert.Equal("Retry", action.DisplayName);
                Assert.Equal("aspire-vscode.retry", action.Command);
                Assert.Null(action.FilePath);
            });
    }

    [Fact]
    public async Task TryDisplayCommandFailureAsync_WithError_CombinesDiagnosticLogActions()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cliLogFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.cli.log");
        var appHostLogFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cli.log");
        string? capturedError = null;
        IReadOnlyList<InteractionMessageAction>? capturedActions = null;
        var displayedLogNotification = false;
        var backchannel = new TestExtensionBackchannel
        {
            HasCapabilityAsyncCallback = (capability, _) => Task.FromResult(capability == KnownCapabilities.MessageActions),
            DisplayErrorWithActionsAsyncCallback = (error, actions) =>
            {
                capturedError = error;
                capturedActions = actions;
                return Task.CompletedTask;
            },
            DisplayMessageWithActionsAsyncCallback = (_, _, _) =>
            {
                displayedLogNotification = true;
                return Task.CompletedTask;
            }
        };
        using var interactionService = CreateExtensionInteractionService(workspace, backchannel);

        var displayed = await interactionService.TryDisplayCommandFailureAsync(
            "The project could not be built.",
            cliLogFilePath,
            appHostLogFilePath,
            CancellationToken.None);
        await interactionService.FlushAsync();

        Assert.True(displayed);
        Assert.Equal("The project could not be built.", capturedError);
        Assert.Collection(
            Assert.IsAssignableFrom<IReadOnlyList<InteractionMessageAction>>(capturedActions),
            action =>
            {
                Assert.Equal(InteractionServiceStrings.OpenCliLog, action.DisplayName);
                Assert.Equal(cliLogFilePath, action.FilePath);
            },
            action =>
            {
                Assert.Equal(InteractionServiceStrings.OpenAppHostLog, action.DisplayName);
                Assert.Equal(appHostLogFilePath, action.FilePath);
            });
        Assert.False(displayedLogNotification);
    }

    [Fact]
    public async Task TryDisplayCommandFailureAsync_WithoutError_SendsActionableLogMessage()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var logFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.cli.log");
        string? capturedMessage = null;
        IReadOnlyList<InteractionMessageAction>? capturedActions = null;
        var backchannel = new TestExtensionBackchannel
        {
            HasCapabilityAsyncCallback = (capability, _) => Task.FromResult(capability == KnownCapabilities.MessageActions),
            DisplayMessageWithActionsAsyncCallback = (_, message, actions) =>
            {
                capturedMessage = message;
                capturedActions = actions;
                return Task.CompletedTask;
            }
        };
        using var interactionService = CreateExtensionInteractionService(workspace, backchannel);

        var displayed = await interactionService.TryDisplayCommandFailureAsync(
            errorMessage: null,
            logFilePath,
            appHostCliLogFilePath: null,
            CancellationToken.None);
        await interactionService.FlushAsync();

        Assert.True(displayed);
        Assert.Contains(logFilePath, capturedMessage);
        var action = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<InteractionMessageAction>>(capturedActions));
        Assert.Equal(InteractionServiceStrings.OpenCliLog, action.DisplayName);
        Assert.Equal(logFilePath, action.FilePath);
        Assert.Null(action.Command);
    }

    [Fact]
    public async Task TryDisplayCommandFailureAsync_WithoutCapability_ReturnsFalseWithoutDisplaying()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var displayedMessage = false;
        var backchannel = new TestExtensionBackchannel
        {
            HasCapabilityAsyncCallback = (_, _) => Task.FromResult(false),
            DisplayErrorAsyncCallback = _ =>
            {
                displayedMessage = true;
                return Task.CompletedTask;
            },
            DisplayErrorWithActionsAsyncCallback = (_, _) =>
            {
                displayedMessage = true;
                return Task.CompletedTask;
            },
            DisplayMessageAsyncCallback = (_, _) =>
            {
                displayedMessage = true;
                return Task.CompletedTask;
            },
            DisplayMessageWithActionsAsyncCallback = (_, _, _) =>
            {
                displayedMessage = true;
                return Task.CompletedTask;
            }
        };
        using var interactionService = CreateExtensionInteractionService(workspace, backchannel);

        var displayed = await interactionService.TryDisplayCommandFailureAsync(
            "The project could not be built.",
            Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.cli.log"),
            appHostCliLogFilePath: null,
            CancellationToken.None);
        await interactionService.FlushAsync();

        Assert.False(displayed);
        Assert.False(displayedMessage);
    }

    [Fact]
    public async Task SupportsMessageActionsAsync_UsesExtensionCapability()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        string? requestedCapability = null;
        var backchannel = new TestExtensionBackchannel
        {
            HasCapabilityAsyncCallback = (capability, _) =>
            {
                requestedCapability = capability;
                return Task.FromResult(true);
            }
        };
        using var interactionService = CreateExtensionInteractionService(workspace, backchannel);

        var supported = await interactionService.SupportsMessageActionsAsync(CancellationToken.None);

        Assert.True(supported);
        Assert.Equal(KnownCapabilities.MessageActions, requestedCapability);
    }

    [Fact]
    public async Task SupportsMessageActionsAsync_WhenCapabilityProbeFails_UsesLegacyNotifications()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var backchannel = new TestExtensionBackchannel
        {
            HasCapabilityAsyncCallback = (_, _) => throw new InvalidOperationException("Capability probe failed.")
        };
        using var interactionService = CreateExtensionInteractionService(workspace, backchannel);

        var supported = await interactionService.SupportsMessageActionsAsync(CancellationToken.None);

        Assert.False(supported);
    }

    [Fact]
    public async Task DisplayCancellationMessage_WithCustomMessage_UsesCancellationBackchannel()
    {
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executionContext = workspace.CreateExecutionContext();
        var consoleInteractionService = new ConsoleInteractionService(
            new ConsoleEnvironment(console, console, TextReader.Null),
            executionContext,
            TestHelpers.CreateInteractiveHostEnvironment(),
            new EnvironmentProcessPathProvider(),
            NullLoggerFactory.Instance,
            new ConsoleLogBufferContext());
        var cancellationMessageCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var displayMessageCalled = false;
        var backchannel = new TestExtensionBackchannel
        {
            DisplayCancellationMessageAsyncCalled = cancellationMessageCalled,
            DisplayMessageAsyncCallback = (_, _) =>
            {
                displayMessageCalled = true;
                return Task.CompletedTask;
            }
        };
        using var extensionInteractionService = new ExtensionInteractionService(
            consoleInteractionService,
            backchannel,
            extensionPromptEnabled: false,
            logger: NullLogger<ExtensionInteractionService>.Instance);

        extensionInteractionService.DisplayCancellationMessage("Stopping dashboard.");
        await extensionInteractionService.FlushAsync();

        Assert.True(cancellationMessageCalled.Task.IsCompletedSuccessfully);
        Assert.False(displayMessageCalled);
    }

    [Fact]
    public async Task PromptForSelectionAsync_GroupingFallsBackToExistingFlatExtensionChoices()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        SelectionChoice[] choices = [new("Same (same) - first"), new("Same (same) - second")];
        IReadOnlyList<string>? capturedChoices = null;
        var backchannel = new TestExtensionBackchannel
        {
            PromptForSelectionAsyncCallback = (_, labels) =>
            {
                capturedChoices = labels;
                return Task.FromResult(labels[1]);
            }
        };
        using var interaction = CreateExtensionInteractionService(workspace, backchannel);

        var selected = await interaction.PromptForSelectionAsync("Select:", choices, choice => $"[bold]{choice.Label}[/]",
            _ => throw new InvalidOperationException("Flat extension fallback must not evaluate grouping."),
            _ => throw new InvalidOperationException("Flat extension fallback must not evaluate terminal labels."));

        Assert.Equal(["Same (same) - first", "Same (same) - second"], capturedChoices);
        Assert.Same(choices[1], selected);
    }

    [Fact]
    public async Task PromptForSelectionAsync_DisabledExtensionPromptsRetainConsoleGrouping()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            Interactive = InteractionSupport.Yes,
            Out = new AnsiConsoleOutput(new StringWriter()),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        var inputConsole = new TestAnsiConsoleWithInput(console,
            [new('\0', ConsoleKey.DownArrow, false, false, false), new('\r', ConsoleKey.Enter, false, false, false)]);
        var consoleInteraction = new ConsoleInteractionService(
            new ConsoleEnvironment(inputConsole, inputConsole, TextReader.Null),
            workspace.CreateExecutionContext(), TestHelpers.CreateInteractiveHostEnvironment(),
            new EnvironmentProcessPathProvider(), NullLoggerFactory.Instance, new ConsoleLogBufferContext());
        var backchannel = new TestExtensionBackchannel
        {
            PromptForSelectionAsyncCallback = (_, _) => throw new InvalidOperationException("Console prompts must not use extension RPC.")
        };
        using var interaction = new ExtensionInteractionService(consoleInteraction, backchannel,
            extensionPromptEnabled: false, logger: NullLogger<ExtensionInteractionService>.Instance);
        SelectionChoice[] choices = [new("Same (same) - first"), new("Same (same) - second")];
        var groupedChoices = 0;

        var selected = await interaction.PromptForSelectionAsync("Select:", choices, choice => choice.Label,
            choice =>
            {
                groupedChoices++;
                return new SelectionChoiceGroup(choice.Label, "Source");
            }, _ => "Same - Observable services");

        Assert.Equal(2, groupedChoices);
        Assert.Same(choices[1], selected);
    }

    [Fact]
    public async Task PromptForSelectionsAsync_ForwardsFormattedDefaultsAndMapsSelectedValues()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        SelectionChoice[] choices =
        [
            new("Aspire skill"),
            new("Playwright CLI")
        ];
        string? capturedPrompt = null;
        IReadOnlyList<string>? capturedChoices = null;
        IReadOnlyList<string>? capturedPreSelected = null;
        var backchannel = new TestExtensionBackchannel
        {
            PromptForSelectionsAsyncCallback = (prompt, formattedChoices, formattedPreSelected) =>
            {
                capturedPrompt = prompt;
                capturedChoices = formattedChoices;
                capturedPreSelected = formattedPreSelected;
                return Task.FromResult<IReadOnlyList<string>>(["Playwright CLI"]);
            }
        };
        using var interactionService = CreateExtensionInteractionService(workspace, backchannel);

        var result = await interactionService.PromptForSelectionsAsync(
            "[bold]Select skills:[/]",
            choices,
            choice => $"[bold]{choice.Label}[/]",
            preSelected: [choices[0]]);

        Assert.Equal("Select skills:", capturedPrompt);
        Assert.Equal(["Aspire skill", "Playwright CLI"], capturedChoices);
        Assert.Equal(["Aspire skill"], capturedPreSelected);
        Assert.Same(choices[1], Assert.Single(result));
    }

    [Fact]
    public async Task PromptForFilePathAsync_RetriesAfterInvalidSelection()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var invalidPath = Path.Combine(workspace.WorkspaceRoot.FullName, "invalid");
        var validPath = Path.Combine(workspace.WorkspaceRoot.FullName, "valid");
        var selections = new Queue<string?>([invalidPath, validPath]);
        var displayedErrors = new List<string>();
        var promptCount = 0;
        const string validationMessage = "The selected directory is not available.";

        var backchannel = new TestExtensionBackchannel
        {
            HasCapabilityAsyncCallback = (capability, _) => Task.FromResult(capability == KnownCapabilities.FilePickers),
            PromptForFilePathAsyncCallback = (_, _, _) =>
            {
                promptCount++;
                return Task.FromResult(selections.Dequeue());
            },
            DisplayErrorAsyncCallback = error =>
            {
                displayedErrors.Add(error);
                return Task.CompletedTask;
            }
        };

        using var interactionService = CreateExtensionInteractionService(workspace, backchannel);

        var result = await interactionService.PromptForFilePathAsync(
            "Select a directory",
            validator: path => string.Equals(path, validPath, StringComparison.Ordinal)
                ? ValidationResult.Success()
                : ValidationResult.Error(validationMessage),
            directory: true,
            retryOnValidationFailure: true);
        await interactionService.FlushAsync();

        Assert.Equal(validPath, result);
        Assert.Equal(2, promptCount);
        Assert.Equal([validationMessage], displayedErrors);
    }

    [Fact]
    public async Task PromptForFilePathAsync_CancelAfterInvalidSelectionThrows()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var invalidPath = Path.Combine(workspace.WorkspaceRoot.FullName, "invalid");
        var selections = new Queue<string?>([invalidPath, null]);
        var displayedErrors = new List<string>();
        var promptCount = 0;
        const string validationMessage = "The selected directory is not available.";

        var backchannel = new TestExtensionBackchannel
        {
            HasCapabilityAsyncCallback = (capability, _) => Task.FromResult(capability == KnownCapabilities.FilePickers),
            PromptForFilePathAsyncCallback = (_, _, _) =>
            {
                promptCount++;
                return Task.FromResult(selections.Dequeue());
            },
            DisplayErrorAsyncCallback = error =>
            {
                displayedErrors.Add(error);
                return Task.CompletedTask;
            }
        };

        using var interactionService = CreateExtensionInteractionService(workspace, backchannel);

        await Assert.ThrowsAsync<ExtensionOperationCanceledException>(() =>
            interactionService.PromptForFilePathAsync(
                "Select a directory",
                validator: _ => ValidationResult.Error(validationMessage),
                directory: true,
                retryOnValidationFailure: true));
        await interactionService.FlushAsync();

        Assert.Equal(2, promptCount);
        Assert.Equal([validationMessage], displayedErrors);
    }

    [Fact]
    public async Task PromptForFilePathAsync_InvalidSelectionWithoutRetryThrows()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var backchannel = new TestExtensionBackchannel
        {
            HasCapabilityAsyncCallback = (capability, _) => Task.FromResult(capability == KnownCapabilities.FilePickers),
            PromptForFilePathAsyncCallback = (_, _, _) => Task.FromResult<string?>("invalid")
        };

        using var interactionService = CreateExtensionInteractionService(workspace, backchannel);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            interactionService.PromptForFilePathAsync(
                "Select a directory",
                validator: _ => ValidationResult.Error("Invalid path.")));

        Assert.Equal("Invalid path.", exception.Message);
    }

    [Fact]
    public async Task Dispose_StopsBackgroundPump()
    {
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var logFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "cli [extension].log");
        var executionContext = workspace.CreateExecutionContext(logFilePath: logFilePath);
        var consoleInteractionService = new ConsoleInteractionService(
            new ConsoleEnvironment(console, console, TextReader.Null),
            executionContext,
            TestHelpers.CreateInteractiveHostEnvironment(),
            new EnvironmentProcessPathProvider(),
            NullLoggerFactory.Instance,
            new ConsoleLogBufferContext());
        var extensionInteractionService = new ExtensionInteractionService(
            consoleInteractionService,
            new TestExtensionBackchannel(),
            extensionPromptEnabled: false,
            logger: NullLogger<ExtensionInteractionService>.Instance);

        extensionInteractionService.Dispose();

        // The background pump should exit promptly after disposal.
        await extensionInteractionService.PumpTask.DefaultTimeout();
    }

    private static ExtensionInteractionService CreateExtensionInteractionService(
        TemporaryWorkspace workspace,
        TestExtensionBackchannel backchannel)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter()),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        console.Profile.Width = int.MaxValue;

        var consoleInteractionService = new ConsoleInteractionService(
            new ConsoleEnvironment(console, console, TextReader.Null),
            workspace.CreateExecutionContext(),
            TestHelpers.CreateInteractiveHostEnvironment(),
            new EnvironmentProcessPathProvider(),
            NullLoggerFactory.Instance,
            new ConsoleLogBufferContext());

        return new ExtensionInteractionService(
            consoleInteractionService,
            backchannel,
            extensionPromptEnabled: true,
            logger: NullLogger<ExtensionInteractionService>.Instance);
    }

    private sealed record SelectionChoice(string Label);
}
