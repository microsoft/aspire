// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Projects;

public class ExtensionGuestLauncherTests
{
    [Fact]
    public async Task LaunchAsync_PrependsCommandAsFirstArg()
    {
        string? capturedProjectFile = null;
        List<string>? capturedArgs = null;
        var service = new FakeLaunchExtensionService((projectFile, args, _, _) =>
        {
            capturedProjectFile = projectFile;
            capturedArgs = args;
        });

        var launcher = new ExtensionGuestLauncher(
            service,
            new FileInfo("/tmp/apphost.ts"),
            debug: false);

        await launcher.LaunchAsync(
            "npx",
            ["tsx", "/tmp/apphost.ts"],
            new DirectoryInfo("/tmp"),
            new Dictionary<string, string>(),
            CancellationToken.None);

        Assert.NotNull(capturedArgs);
        Assert.Equal("npx", capturedArgs[0]);
        Assert.Equal("tsx", capturedArgs[1]);
        Assert.Equal("/tmp/apphost.ts", capturedArgs[2]);
    }

    [Fact]
    public async Task LaunchAsync_PassesAppHostFileAsProjectFile()
    {
        string? capturedProjectFile = null;
        var service = new FakeLaunchExtensionService((projectFile, _, _, _) =>
        {
            capturedProjectFile = projectFile;
        });

        var appHostFile = new FileInfo("/home/user/project/apphost.ts");
        var launcher = new ExtensionGuestLauncher(service, appHostFile, debug: true);

        await launcher.LaunchAsync("npx", ["tsx"], new DirectoryInfo("/tmp"), new Dictionary<string, string>(), CancellationToken.None);

        Assert.Equal(appHostFile.FullName, capturedProjectFile);
    }

    [Fact]
    public async Task LaunchAsync_PassesDebugFlag()
    {
        bool? capturedDebug = null;
        var service = new FakeLaunchExtensionService((_, _, _, debug) =>
        {
            capturedDebug = debug;
        });

        var launcher = new ExtensionGuestLauncher(service, new FileInfo("/tmp/apphost.ts"), debug: true);
        await launcher.LaunchAsync("npx", [], new DirectoryInfo("/tmp"), new Dictionary<string, string>(), CancellationToken.None);

        Assert.True(capturedDebug);
    }

    [Fact]
    public async Task LaunchAsync_PassesEnvironmentAsEnvVars()
    {
        List<EnvVar>? capturedEnv = null;
        var service = new FakeLaunchExtensionService((_, _, env, _) =>
        {
            capturedEnv = env;
        });

        var launcher = new ExtensionGuestLauncher(service, new FileInfo("/tmp/apphost.ts"), debug: false);
        var envVars = new Dictionary<string, string>
        {
            ["SOCKET_PATH"] = "/tmp/socket",
            ["NODE_ENV"] = "development"
        };

        await launcher.LaunchAsync("npx", ["tsx"], new DirectoryInfo("/tmp"), envVars, CancellationToken.None);

        Assert.NotNull(capturedEnv);
        Assert.Equal(2, capturedEnv.Count);
        Assert.Contains(capturedEnv, e => e.Name == "SOCKET_PATH" && e.Value == "/tmp/socket");
        Assert.Contains(capturedEnv, e => e.Name == "NODE_ENV" && e.Value == "development");
    }

    [Fact]
    public async Task LaunchAsync_ReturnsZeroExitCodeAndNullOutput()
    {
        var service = new FakeLaunchExtensionService((_, _, _, _) => { });
        var launcher = new ExtensionGuestLauncher(service, new FileInfo("/tmp/apphost.ts"), debug: false);

        var (exitCode, output) = await launcher.LaunchAsync("cmd", [], new DirectoryInfo("/tmp"), new Dictionary<string, string>(), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Null(output);
    }

    [Fact]
    public async Task LaunchAsync_WithEmptyArgs_OnlyContainsCommand()
    {
        List<string>? capturedArgs = null;
        var service = new FakeLaunchExtensionService((_, args, _, _) =>
        {
            capturedArgs = args;
        });

        var launcher = new ExtensionGuestLauncher(service, new FileInfo("/tmp/apphost.ts"), debug: false);
        await launcher.LaunchAsync("python", [], new DirectoryInfo("/tmp"), new Dictionary<string, string>(), CancellationToken.None);

        Assert.NotNull(capturedArgs);
        Assert.Single(capturedArgs);
        Assert.Equal("python", capturedArgs[0]);
    }

    /// <summary>
    /// Minimal fake that only implements LaunchAppHostAsync for testing ExtensionGuestLauncher.
    /// </summary>
    private sealed class FakeLaunchExtensionService : IExtensionInteractionService
    {
        private readonly Action<string, List<string>, List<EnvVar>, bool> _onLaunch;

        public FakeLaunchExtensionService(Action<string, List<string>, List<EnvVar>, bool> onLaunch)
        {
            _onLaunch = onLaunch;
        }

        public IExtensionBackchannel Backchannel => throw new NotImplementedException();

        public Task LaunchAppHostAsync(string projectFile, List<string> arguments, List<EnvVar> environment, bool debug)
        {
            _onLaunch(projectFile, arguments, environment, debug);
            return Task.CompletedTask;
        }

        // Remaining IExtensionInteractionService members - not used by ExtensionGuestLauncher
        public void OpenEditor(string projectPath) => throw new NotImplementedException();
        public void LogMessage(Microsoft.Extensions.Logging.LogLevel logLevel, string message) => throw new NotImplementedException();
        public void DisplayDashboardUrls(DashboardUrlsState dashboardUrls) => throw new NotImplementedException();
        public void NotifyAppHostStartupCompleted() => throw new NotImplementedException();
        public void DisplayConsolePlainText(string message) => throw new NotImplementedException();
        public Task StartDebugSessionAsync(string workingDirectory, string? projectFile, bool debug, DebugSessionOptions? options = null) => throw new NotImplementedException();
        public void WriteDebugSessionMessage(string message, bool stdout, string? textStyle) => throw new NotImplementedException();
        public Task RequestAppHostAttachAsync(int processId, string projectName) => throw new NotImplementedException();
        public void ConsoleDisplaySubtleMessage(string message, bool allowMarkup = false) => throw new NotImplementedException();
        public void WriteConsoleLog(string message, int? lineNumber = null, string? type = null, bool isErrorMessage = false) => throw new NotImplementedException();
        public ConsoleOutput Console { get; set; }
        public Task<T> ShowStatusAsync<T>(string statusText, Func<Task<T>> action, KnownEmoji? emoji = null, bool allowMarkup = false) => throw new NotImplementedException();
        public void ShowStatus(string statusText, Action action, KnownEmoji? emoji = null, bool allowMarkup = false) => throw new NotImplementedException();
        public Task<string> PromptForStringAsync(string promptText, Func<string, Spectre.Console.ValidationResult>? validator = null, bool isSecret = false, bool required = false, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string> PromptForFilePathAsync(string promptText, Func<string, Spectre.Console.ValidationResult>? validator = null, bool directory = false, bool required = false, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<T> PromptForSelectionAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default) where T : notnull => throw new NotImplementedException();
        public Task<IReadOnlyList<T>> PromptForSelectionsAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, IEnumerable<T>? preSelected = null, bool optional = false, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default) where T : notnull => throw new NotImplementedException();
        public int DisplayIncompatibleVersionError(AppHostIncompatibleException ex, string appHostHostingVersion) => throw new NotImplementedException();
        public void DisplayError(string errorMessage) => throw new NotImplementedException();
        public void DisplayMessage(KnownEmoji emoji, string message, bool allowMarkup = false) => throw new NotImplementedException();
        public void DisplaySuccess(string message, bool allowMarkup = false) => throw new NotImplementedException();
        public void DisplayLines(IEnumerable<(OutputLineStream Stream, string Line)> lines) => throw new NotImplementedException();
        public void DisplayCancellationMessage() => throw new NotImplementedException();
        public Task<bool> PromptConfirmAsync(string promptText, PromptBinding<bool>? binding = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public void DisplaySubtleMessage(string message, bool allowMarkup = false) => throw new NotImplementedException();
        public void DisplayEmptyLine() => throw new NotImplementedException();
        public void DisplayPlainText(string text) => throw new NotImplementedException();
        public void DisplayRawText(string text, ConsoleOutput? consoleOverride = null) => throw new NotImplementedException();
        public void DisplayMarkdown(string markdown, ConsoleOutput? consoleOverride = null, int? maxWidth = null) => throw new NotImplementedException();
        public void DisplayMarkupLine(string markup) => throw new NotImplementedException();
        public void DisplayVersionUpdateNotification(string newerVersion, string? updateCommand = null) => throw new NotImplementedException();
        public void DisplayRenderable(Spectre.Console.Rendering.IRenderable renderable) => throw new NotImplementedException();
        public Task DisplayLiveAsync(Spectre.Console.Rendering.IRenderable initialRenderable, Func<Action<Spectre.Console.Rendering.IRenderable>, Task> callback) => throw new NotImplementedException();
    }
}
