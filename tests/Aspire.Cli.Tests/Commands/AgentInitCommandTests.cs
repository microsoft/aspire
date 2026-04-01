// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Agents;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aspire.Cli.Tests.Commands;

public class AgentInitCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task AgentInitCommand_UsesNormalizedDisplayPath_WhenInstallingUserLevelSkill()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var interactionService = new TestAgentInitInteractionService(workspace.WorkspaceRoot.FullName);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.CliExecutionContextFactory = _ => CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory);
        });

        var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("agent init");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.Contains(
            interactionService.Messages,
            message => message == string.Format(
                CultureInfo.CurrentCulture,
                AgentCommandStrings.InitCommand_InstalledSkill,
                SkillDefinition.Aspire.Name,
                "~/.agents/skills/aspire"));
    }

    [Fact]
    public async Task AgentInitCommand_IncludesSpecificSkillDirectory_WhenInstallFails()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var invalidRootFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "not-a-directory.txt");
        await File.WriteAllTextAsync(invalidRootFilePath, "blocked").DefaultTimeout();

        var interactionService = new TestAgentInitInteractionService(invalidRootFilePath);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });

        var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("agent init");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);

        var expectedSkillDirectoryPath = Path.Combine(invalidRootFilePath, ".agents", "skills", SkillDefinition.Aspire.Name);
        Assert.Contains(
            interactionService.Errors,
            message => message.Contains(expectedSkillDirectoryPath, StringComparison.Ordinal));
    }

    private static CliExecutionContext CreateExecutionContext(DirectoryInfo workingDirectory, DirectoryInfo homeDirectory)
    {
        var hivesDirectory = new DirectoryInfo(Path.Combine(workingDirectory.FullName, ".aspire", "hives"));
        var cacheDirectory = new DirectoryInfo(Path.Combine(workingDirectory.FullName, ".aspire", "cache"));
        var logsDirectory = new DirectoryInfo(Path.Combine(workingDirectory.FullName, ".aspire", "logs"));
        var logFilePath = Path.Combine(logsDirectory.FullName, "test.log");
        return new CliExecutionContext(
            workingDirectory,
            hivesDirectory,
            cacheDirectory,
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-sdks")),
            logsDirectory,
            logFilePath,
            homeDirectory: homeDirectory);
    }

    private sealed class TestAgentInitInteractionService(string workspaceRootPath) : IInteractionService
    {
        public List<string> Messages { get; } = [];
        public List<string> Errors { get; } = [];

        public ConsoleOutput Console { get; set; }

        public Task<T> ShowStatusAsync<T>(string statusText, Func<Task<T>> action, KnownEmoji? emoji = null, bool allowMarkup = false) => action();

        public void ShowStatus(string statusText, Action action, KnownEmoji? emoji = null, bool allowMarkup = false) => action();

        public Task<string> PromptForStringAsync(string promptText, string? defaultValue = null, Func<string, ValidationResult>? validator = null, bool isSecret = false, bool required = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> PromptForFilePathAsync(string promptText, string? defaultValue = null, Func<string, ValidationResult>? validator = null, bool directory = false, bool required = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(workspaceRootPath);

        public Task<bool> ConfirmAsync(string promptText, bool defaultValue = true, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<T> PromptForSelectionAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, CancellationToken cancellationToken = default) where T : notnull =>
            Task.FromResult(choices.First());

        public Task<IReadOnlyList<T>> PromptForSelectionsAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, IEnumerable<T>? preSelected = null, bool optional = false, CancellationToken cancellationToken = default) where T : notnull
        {
            var selected = choices.Where(choice =>
            {
                return choice switch
                {
                    SkillLocation location => location == SkillLocation.Standard,
                    SkillDefinition skill => skill == SkillDefinition.Aspire,
                    _ => false
                };
            }).ToArray();

            return Task.FromResult<IReadOnlyList<T>>(selected);
        }

        public int DisplayIncompatibleVersionError(AppHostIncompatibleException ex, string appHostHostingVersion) => ExitCodeConstants.InvalidCommand;

        public void DisplayError(string errorMessage) => Errors.Add(errorMessage);

        public void DisplayMessage(KnownEmoji emoji, string message, bool allowMarkup = false) => Messages.Add(message);

        public void DisplayPlainText(string text)
        {
        }

        public void DisplayRawText(string text, ConsoleOutput? consoleOverride = null)
        {
        }

        public void DisplayMarkdown(string markdown)
        {
        }

        public void DisplayMarkupLine(string markup)
        {
        }

        public void DisplaySuccess(string message, bool allowMarkup = false) => Messages.Add(message);

        public void DisplaySubtleMessage(string message, bool allowMarkup = false)
        {
        }

        public void DisplayLines(IEnumerable<(OutputLineStream Stream, string Line)> lines)
        {
        }

        public void DisplayRenderable(IRenderable renderable)
        {
        }

        public Task DisplayLiveAsync(IRenderable initialRenderable, Func<Action<IRenderable>, Task> callback) =>
            callback(_ => { });

        public void DisplayCancellationMessage()
        {
        }

        public void DisplayEmptyLine()
        {
        }

        public void DisplayVersionUpdateNotification(string newerVersion, string? updateCommand = null)
        {
        }

        public void WriteConsoleLog(string message, int? lineNumber = null, string? type = null, bool isErrorMessage = false)
        {
        }
    }
}
