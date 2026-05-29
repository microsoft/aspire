// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Aspire.Cli.Commands;
using Aspire.Cli.Projects;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Spectre.Console;
using Spectre.Console.Rendering;
using InvocationConfiguration = System.CommandLine.InvocationConfiguration;

namespace Aspire.Cli.Tests.Commands;

public class LsCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task LsCommand_Help_Works()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task LsCommand_WhenNoCandidates_ReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("Json")]
    [InlineData("JSON")]
    public async Task LsCommand_FormatOption_IsCaseInsensitive(string format)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"ls --format {format}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task LsCommand_FormatOption_RejectsInvalidValue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --format invalid");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.NotEqual(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task LsCommand_JsonFormat_ReturnsCandidateAppHosts()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);
        var appHostPath1 = Path.Combine(workspace.WorkspaceRoot.FullName, "App1", "App1.AppHost.csproj");
        var appHostPath2 = Path.Combine(workspace.WorkspaceRoot.FullName, "App2", "App2.AppHost.csproj");
        var projectLocator = new TestProjectLocator
        {
            FindAppHostProjectsAsyncCallback = (_, _, _) => Task.FromResult(new List<AppHostProjectCandidate>
            {
                new(new FileInfo(appHostPath1), KnownLanguageId.CSharp),
                new(new FileInfo(appHostPath2), KnownLanguageId.TypeScript, AppHostProjectCandidateStatus.PossiblyUnbuildable)
            })
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        var jsonOutput = string.Join(string.Empty, textWriter.Logs);
        var candidateAppHosts = JsonSerializer.Deserialize(jsonOutput, JsonSourceGenerationContext.RelaxedEscaping.ListCandidateAppHostDisplayInfo);
        Assert.NotNull(candidateAppHosts);

        Assert.Collection(candidateAppHosts,
            first =>
            {
                Assert.Equal(appHostPath1, first.Path);
                Assert.Equal(KnownLanguageId.CSharp, first.Language);
                Assert.Equal("buildable", first.Status);
            },
            second =>
            {
                Assert.Equal(appHostPath2, second.Path);
                Assert.Equal(KnownLanguageId.TypeScript, second.Language);
                Assert.Equal("possibly-unbuildable", second.Status);
            });
    }

    [Fact]
    public async Task LsCommand_JsonFormat_WhenNoCandidates_ReturnsEmptyArray()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);
        var errorWriter = new StringWriter();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
            options.ErrorTextWriter = errorWriter;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        var jsonOutput = string.Join(string.Empty, textWriter.Logs);
        using var document = JsonDocument.Parse(jsonOutput);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal(0, document.RootElement.GetArrayLength());

        // Stderr should not contain JSON data
        var stderrText = errorWriter.ToString();
        Assert.Equal("", stderrText);
    }

    [Fact]
    public async Task LsCommand_JsonFormat_IncludesConfiguredAppHostOutsideWorkingDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);
        var workingDirectory = workspace.WorkspaceRoot.CreateSubdirectory("WorkingDir");
        var configuredAppHost = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "ConfiguredAppHost.csproj"));
        await File.WriteAllTextAsync(configuredAppHost.FullName, "Not a real apphost");
        await File.WriteAllTextAsync(Path.Combine(workingDirectory.FullName, "aspire.config.json"), JsonSerializer.Serialize(new
        {
            appHost = new
            {
                path = "../ConfiguredAppHost.csproj"
            }
        }));

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.WorkingDirectory = workingDirectory;
            options.OutputTextWriter = textWriter;
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        var jsonOutput = string.Join(string.Empty, textWriter.Logs);
        var candidateAppHosts = JsonSerializer.Deserialize(jsonOutput, JsonSourceGenerationContext.RelaxedEscaping.ListCandidateAppHostDisplayInfo);
        Assert.NotNull(candidateAppHosts);
        var candidate = Assert.Single(candidateAppHosts);
        Assert.Equal(configuredAppHost.FullName, candidate.Path);
        Assert.Equal(KnownLanguageId.CSharp, candidate.Language);
        Assert.Equal("buildable", candidate.Status);
    }

    [Fact]
    public async Task LsCommand_JsonFormat_OnlyJsonOnStdout_StatusMessagesOnStderr()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);
        var errorWriter = new StringWriter();
        var appHostPath1 = Path.Combine(workspace.WorkspaceRoot.FullName, "App1", "App1.AppHost.csproj");
        var appHostPath2 = Path.Combine(workspace.WorkspaceRoot.FullName, "App2", "App2.AppHost.csproj");
        var projectLocator = new TestProjectLocator
        {
            FindAppHostProjectsAsyncCallback = (_, _, _) => Task.FromResult(new List<AppHostProjectCandidate>
            {
                new(new FileInfo(appHostPath1), KnownLanguageId.CSharp),
                new(new FileInfo(appHostPath2), KnownLanguageId.TypeScript, AppHostProjectCandidateStatus.PossiblyUnbuildable)
            })
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
            options.ErrorTextWriter = errorWriter;
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        // Stdout must contain only valid JSON (parseable without error)
        var stdoutText = string.Join(string.Empty, textWriter.Logs);
        using var document = JsonDocument.Parse(stdoutText);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal(2, document.RootElement.GetArrayLength());

        // Stderr should not contain JSON data
        var stderrText = errorWriter.ToString();
        Assert.Equal("", stderrText);
    }

    [Fact]
    public async Task LsCommand_JsonFormat_NoResults_OnlyJsonOnStdout_StatusMessagesOnStderr()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);
        var errorWriter = new StringWriter();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
            options.ErrorTextWriter = errorWriter;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        // Stdout must contain only valid JSON (empty array)
        var stdoutText = string.Join(string.Empty, textWriter.Logs);
        using var document = JsonDocument.Parse(stdoutText);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal(0, document.RootElement.GetArrayLength());

        // Stderr should not contain JSON data
        var stderrText = errorWriter.ToString();
        Assert.Equal("", stderrText);
    }

    [Fact]
    public async Task LsCommand_JsonFormat_Stream_ReturnsNewlineDelimitedCandidates()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);
        var errorWriter = new StringWriter();
        var appHostPath1 = Path.Combine(workspace.WorkspaceRoot.FullName, "App1", "App1.AppHost.csproj");
        var appHostPath2 = Path.Combine(workspace.WorkspaceRoot.FullName, "App2", "App2.AppHost.csproj");
        var appHost1 = new AppHostProjectCandidate(new FileInfo(appHostPath1), KnownLanguageId.CSharp);
        var appHost2 = new AppHostProjectCandidate(new FileInfo(appHostPath2), KnownLanguageId.TypeScript, AppHostProjectCandidateStatus.PossiblyUnbuildable);
        var projectLocator = new TestProjectLocator
        {
            FindAppHostProjectsStreamAsyncCallback = (_, _, _, _) => ToAsyncEnumerable(appHost1, appHost2)
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
            options.ErrorTextWriter = errorWriter;
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --format json --stream");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        var lines = textWriter.Logs.ToArray();
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line =>
        {
            Assert.DoesNotContain('\n', line);
            using var document = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        });

        using var firstCandidate = JsonDocument.Parse(lines[0]);
        Assert.Equal(appHostPath1, firstCandidate.RootElement.GetProperty("path").GetString());
        Assert.Equal(KnownLanguageId.CSharp, firstCandidate.RootElement.GetProperty("language").GetString());
        Assert.Equal("buildable", firstCandidate.RootElement.GetProperty("status").GetString());

        using var secondCandidate = JsonDocument.Parse(lines[1]);
        Assert.Equal(appHostPath2, secondCandidate.RootElement.GetProperty("path").GetString());
        Assert.Equal(KnownLanguageId.TypeScript, secondCandidate.RootElement.GetProperty("language").GetString());
        Assert.Equal("possibly-unbuildable", secondCandidate.RootElement.GetProperty("status").GetString());
        Assert.Equal(string.Empty, errorWriter.ToString());
    }

    [Fact]
    public async Task LsCommand_JsonFormat_Stream_WhenNoCandidates_DoesNotWriteStderr()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);
        var errorWriter = new StringWriter();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
            options.ErrorTextWriter = errorWriter;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --format json --stream");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        var lines = textWriter.Logs.ToArray();
        Assert.Empty(lines);
        Assert.Equal(string.Empty, errorWriter.ToString());
    }

    [Fact]
    public async Task LsCommand_JsonFormat_Stream_FlushesCandidateBeforeDiscoveryCompletes()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);
        var candidateReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDiscoveryToComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "App", "App.AppHost.csproj");
        var appHost = new AppHostProjectCandidate(new FileInfo(appHostPath), KnownLanguageId.CSharp);
        var projectLocator = new TestProjectLocator
        {
            FindAppHostProjectsStreamAsyncCallback = (_, _, _, cancellationToken) => GetCandidatesAsync(cancellationToken)
        };

        async IAsyncEnumerable<AppHostProjectCandidate> GetCandidatesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return appHost;
            candidateReported.SetResult();
            await allowDiscoveryToComplete.Task.WaitAsync(cancellationToken);
        }

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --format json --stream");

        var invokeTask = result.InvokeAsync();
        await candidateReported.Task.DefaultTimeout();

        var partialLines = textWriter.Logs.ToArray();
        Assert.Single(partialLines);
        using var candidate = JsonDocument.Parse(partialLines[0]);
        Assert.Equal(appHostPath, candidate.RootElement.GetProperty("path").GetString());

        allowDiscoveryToComplete.SetResult();

        var exitCode = await invokeTask.DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Single(textWriter.Logs);
    }

    [Fact]
    public async Task LsCommand_JsonFormat_Stream_WhenCancelled_DoesNotWriteProtocolEvent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var cancellationTokenSource = new CancellationTokenSource();
        var textWriter = new TestOutputTextWriter(outputHelper);
        var projectLocator = new TestProjectLocator
        {
            FindAppHostProjectsStreamAsyncCallback = (_, _, _, cancellationToken) => CancelDiscoveryAsync(cancellationToken)
        };

        async IAsyncEnumerable<AppHostProjectCandidate> CancelDiscoveryAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationTokenSource.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --format json --stream");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration(), cancellationTokenSource.Token).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        var lines = textWriter.Logs.ToArray();
        Assert.Empty(lines);
    }

    [Fact]
    public async Task LsCommand_StreamOption_RequiresJsonFormat()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --stream");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.NotEqual(CliExitCodes.Success, exitCode);
        Assert.Empty(textWriter.Logs);
    }

    [Fact]
    public async Task LsCommand_TableFormat_ColorsStatus()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);
        var appHostPath1 = Path.Combine(workspace.WorkspaceRoot.FullName, "App1", "App1.AppHost.csproj");
        var appHostPath2 = Path.Combine(workspace.WorkspaceRoot.FullName, "App2", "App2.AppHost.csproj");
        var projectLocator = new TestProjectLocator
        {
            FindAppHostProjectsAsyncCallback = (_, _, _) => Task.FromResult(new List<AppHostProjectCandidate>
            {
                new(new FileInfo(appHostPath1), KnownLanguageId.CSharp),
                new(new FileInfo(appHostPath2), KnownLanguageId.TypeScript, AppHostProjectCandidateStatus.PossiblyUnbuildable)
            })
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        var output = string.Join('\n', textWriter.Logs);
        Assert.Contains("\u001b[32mbuildable", output);
        Assert.Contains("\u001b[93m", output);
        Assert.Contains("possibly-unbuild", output);
    }

    [Fact]
    public async Task LsCommand_TableFormat_InteractiveMode_ShowsSearchStatusAndFinalTable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var appHostPath1 = Path.Combine(workspace.WorkspaceRoot.FullName, "App1", "App1.AppHost.csproj");
        var appHostPath2 = Path.Combine(workspace.WorkspaceRoot.FullName, "App2", "App2.AppHost.csproj");
        var appHost1 = new AppHostProjectCandidate(new FileInfo(appHostPath1), KnownLanguageId.CSharp);
        var appHost2 = new AppHostProjectCandidate(new FileInfo(appHostPath2), KnownLanguageId.TypeScript, AppHostProjectCandidateStatus.PossiblyUnbuildable);
        var projectLocator = new TestProjectLocator
        {
            FindAppHostProjectsStreamAsyncCallback = (_, _, onDirectoryEnumerated, _) =>
            {
                onDirectoryEnumerated?.Invoke(42);
                return ToAsyncEnumerable(appHost1, appHost2);
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => interactionService;
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        // The interactive path should not stream a live table any more. It instead shows a search
        // status while discovery runs and then renders the final table exactly once.
        Assert.Empty(interactionService.DisplayedLiveRenderables);

        // The initial status text is captured by TestInteractionService even when the action completes
        // before the periodic refresh loop ticks, so this is stable across timing.
        Assert.NotEmpty(interactionService.DynamicStatusTexts);
        Assert.Contains(interactionService.DynamicStatusTexts, text => text.Contains("AppHosts found"));
        Assert.Contains(interactionService.DynamicStatusTexts, text => text.Contains("42 directories searched", StringComparison.Ordinal));

        Assert.Single(interactionService.DisplayedRenderables);
        var finalOutput = RenderToPlainConsole(interactionService.DisplayedRenderables[0]);
        Assert.Contains("App1.AppHost.csproj", finalOutput);
        Assert.Contains("App2.AppHost.csproj", finalOutput);
    }

    [Fact]
    public async Task LsCommand_TableFormat_InteractiveMode_RefreshesSearchStatusWithTimeProvider()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var timeProvider = new FakeTimeProvider();
        var interactionService = new TestInteractionService();
        var statusRefreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDiscoveryToComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var candidateReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "App", "App.AppHost.csproj");
        var appHost = new AppHostProjectCandidate(new FileInfo(appHostPath), KnownLanguageId.CSharp);
        var projectLocator = new TestProjectLocator
        {
            FindAppHostProjectsStreamAsyncCallback = (_, _, onDirectoryEnumerated, cancellationToken) =>
                GetCandidatesAsync(cancellationToken, onDirectoryEnumerated)
        };

        interactionService.ShowDynamicStatusCallback = text =>
        {
            if (text.Contains("1 directories searched", StringComparison.Ordinal) &&
                text.Contains("1 AppHosts found", StringComparison.Ordinal))
            {
                statusRefreshed.TrySetResult();
            }
        };

        async IAsyncEnumerable<AppHostProjectCandidate> GetCandidatesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken,
            Action<int>? onDirectoryEnumerated)
        {
            onDirectoryEnumerated?.Invoke(1);
            yield return appHost;
            candidateReported.SetResult();
            await allowDiscoveryToComplete.Task.WaitAsync(cancellationToken);
        }

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => interactionService;
            options.ProjectLocatorFactory = _ => projectLocator;
            options.TimeProvider = timeProvider;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls");

        var invokeTask = result.InvokeAsync();
        await candidateReported.Task.DefaultTimeout();

        Assert.Single(interactionService.DynamicStatusTexts);

        timeProvider.Advance(TimeSpan.FromMilliseconds(999));
        await Task.Yield();
        Assert.Single(interactionService.DynamicStatusTexts);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await statusRefreshed.Task.DefaultTimeout();

        allowDiscoveryToComplete.SetResult();
        var exitCode = await invokeTask.DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task LsCommand_WhenCancelled_ReturnsSuccessAndDisplaysCancellation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var cancellationTokenSource = new CancellationTokenSource();
        var interactionService = new TestInteractionService();
        var projectLocator = new TestProjectLocator
        {
            FindAppHostProjectsAsyncCallback = (_, _, cancellationToken) =>
            {
                cancellationTokenSource.Cancel();
                return Task.FromCanceled<List<AppHostProjectCandidate>>(cancellationToken);
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration(), cancellationTokenSource.Token).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Single(interactionService.DisplayedCancellations);
    }

    [Fact]
    public async Task LsCommand_DefaultsToFilteredScope()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        AppHostDiscoveryScope? capturedScope = null;
        var projectLocator = new TestProjectLocator
        {
            FindAppHostProjectsAsyncCallback = (_, scope, _) =>
            {
                capturedScope = scope;
                return Task.FromResult(new List<AppHostProjectCandidate>());
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(AppHostDiscoveryScope.DefaultFiltered, capturedScope);
    }

    [Fact]
    public async Task LsCommand_AllFlag_PassesAllFilesScope()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        AppHostDiscoveryScope? capturedScope = null;
        var projectLocator = new TestProjectLocator
        {
            FindAppHostProjectsAsyncCallback = (_, scope, _) =>
            {
                capturedScope = scope;
                return Task.FromResult(new List<AppHostProjectCandidate>());
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(AppHostDiscoveryScope.AllFiles, capturedScope);
    }

    [Fact]
    public async Task LsCommand_EmitsProfilingActivities()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        // ActivitySource listeners are process-wide, so this test can observe profiling spans
        // from other tests running in parallel. Use a unique session id and filter by it instead
        // of assuming every observed activity belongs to this command invocation.
        var sessionId = $"ls-{Guid.NewGuid():N}";
        var startedActivities = new ConcurrentBag<Activity>();
        using var listener = CreateProfilingActivityListener(startedActivities.Add);

        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "App", "App.AppHost.csproj");
        var projectLocator = new TestProjectLocator
        {
            FindAppHostProjectsAsyncCallback = (_, _, _) => Task.FromResult(new List<AppHostProjectCandidate>
            {
                new(new FileInfo(appHostPath), KnownLanguageId.CSharp)
            })
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => projectLocator;
            options.ConfigurationCallback = config =>
            {
                config[ProfilingTelemetry.EnvironmentVariables.Enabled] = "true";
                config[ProfilingTelemetry.EnvironmentVariables.SessionId] = sessionId;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --format json --all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        var lsActivity = Assert.Single(startedActivities, activity => IsActivityFromSession(activity, ProfilingTelemetry.Activities.LsCommand, sessionId));
        Assert.Equal("json", lsActivity.GetTagItem(ProfilingTelemetry.Tags.LsOutputFormat));
        Assert.Equal(true, lsActivity.GetTagItem(ProfilingTelemetry.Tags.LsIncludeAll));
        Assert.Equal(1, lsActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostCandidateCount));
        Assert.Equal(sessionId, lsActivity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId));

        var findActivity = Assert.Single(startedActivities, activity => IsActivityFromSession(activity, ProfilingTelemetry.Activities.LsFindAppHosts, sessionId));
        Assert.Equal(AppHostDiscoveryScope.AllFiles.ToString(), findActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostDiscoveryScope));
        Assert.Equal(1, findActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostCandidateCount));
    }

    private static bool IsActivityFromSession(Activity activity, string operationName, string sessionId)
    {
        return activity.OperationName == operationName &&
            Equals(sessionId, activity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId));
    }

    private static ActivityListener CreateProfilingActivityListener(Action<Activity> activityStarted)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ProfilingTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activityStarted
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static string RenderToPlainConsole(IRenderable renderable)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            Interactive = InteractionSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });

        console.Profile.Width = int.MaxValue;
        console.Profile.Capabilities.Links = false;
        console.Write(renderable);

        return writer.ToString().Replace("\r\n", "\n");
    }

    private static async IAsyncEnumerable<AppHostProjectCandidate> ToAsyncEnumerable(params AppHostProjectCandidate[] candidates)
    {
        foreach (var candidate in candidates)
        {
            await Task.Yield();
            yield return candidate;
        }
    }
}
