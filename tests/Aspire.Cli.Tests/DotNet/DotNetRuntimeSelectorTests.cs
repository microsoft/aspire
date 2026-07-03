// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aspire.Cli.Tests.DotNet;

public class DotNetRuntimeSelectorTests
{
    [Fact]
    public void Constructor_SetsDefaultValues()
    {
        var logger = NullLogger<DotNetRuntimeSelector>.Instance;
        var configuration = new ConfigurationBuilder().Build();
        var sdkInstaller = new TestSdkInstaller();
        var interactionService = new TestInteractionService();
        var console = new TestAnsiConsole();

        var selector = new DotNetRuntimeSelector(logger, configuration, sdkInstaller, interactionService, console);

        Assert.Equal(DotNetRuntimeMode.System, selector.Mode);
        Assert.Equal("dotnet", selector.DotNetExecutablePath);
    }

    [Fact]
    public async Task InitializeAsync_WithAvailableSystemSdk_ReturnsTrue()
    {
        var logger = NullLogger<DotNetRuntimeSelector>.Instance;
        var configuration = new ConfigurationBuilder().Build();
        var sdkInstaller = new TestSdkInstaller { CheckResult = true };
        var interactionService = new TestInteractionService();
        var console = new TestAnsiConsole();

        var selector = new DotNetRuntimeSelector(logger, configuration, sdkInstaller, interactionService, console);

        var result = await selector.InitializeAsync();

        Assert.True(result);
        Assert.Equal(DotNetRuntimeMode.System, selector.Mode);
        Assert.Equal("dotnet", selector.DotNetExecutablePath);
    }

    [Fact]
    public async Task InitializeAsync_WithDisablePrivateSdkEnvVar_StopsAtSystemCheck()
    {
        var logger = NullLogger<DotNetRuntimeSelector>.Instance;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("ASPIRE_DISABLE_PRIVATE_SDK", "1")
            })
            .Build();
        var sdkInstaller = new TestSdkInstaller { CheckResult = false };
        var interactionService = new TestInteractionService();
        var console = new TestAnsiConsole();

        var selector = new DotNetRuntimeSelector(logger, configuration, sdkInstaller, interactionService, console);

        var result = await selector.InitializeAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task InitializeAsync_WithVersionOverride_UsesOverrideVersion()
    {
        var logger = NullLogger<DotNetRuntimeSelector>.Instance;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("overrideMinimumSdkVersion", "8.0.100")
            })
            .Build();
        
        var sdkInstaller = new TestSdkInstaller { CheckResult = true };
        var interactionService = new TestInteractionService();
        var console = new TestAnsiConsole();

        var selector = new DotNetRuntimeSelector(logger, configuration, sdkInstaller, interactionService, console);

        var result = await selector.InitializeAsync();

        Assert.True(result);
        Assert.Equal("8.0.100", sdkInstaller.LastCheckedVersion);
    }

    [Fact]
    public async Task InitializeAsync_ConfigurationTakesPrecedenceOverEnvironment()
    {
        var logger = NullLogger<DotNetRuntimeSelector>.Instance;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("ASPIRE_DOTNET_SDK_VERSION", "9.0.100"),
                new KeyValuePair<string, string?>("ASPIRE_AUTO_INSTALL", "1")
            })
            .Build();
        var sdkInstaller = new TestSdkInstaller { CheckResult = true };
        var interactionService = new TestInteractionService();
        var console = new TestAnsiConsole();

        // Set different values in environment variables
        Environment.SetEnvironmentVariable("ASPIRE_DOTNET_SDK_VERSION", "8.0.100");
        Environment.SetEnvironmentVariable("ASPIRE_AUTO_INSTALL", "0");

        try
        {
            var selector = new DotNetRuntimeSelector(logger, configuration, sdkInstaller, interactionService, console);

            var result = await selector.InitializeAsync();

            Assert.True(result);
            // Verify the configuration value was used, not the environment variable
            Assert.Equal("9.0.100", sdkInstaller.LastCheckedVersion);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPIRE_DOTNET_SDK_VERSION", null);
            Environment.SetEnvironmentVariable("ASPIRE_AUTO_INSTALL", null);
        }
    }

    [Fact]
    public void GetEnvironmentVariables_ReturnsEmptyDictionary_WhenNotInitialized()
    {
        var logger = NullLogger<DotNetRuntimeSelector>.Instance;
        var configuration = new ConfigurationBuilder().Build();
        var sdkInstaller = new TestSdkInstaller();
        var interactionService = new TestInteractionService();
        var console = new TestAnsiConsole();

        var selector = new DotNetRuntimeSelector(logger, configuration, sdkInstaller, interactionService, console);

        var envVars = selector.GetEnvironmentVariables();

        Assert.Empty(envVars);
    }

    private sealed class TestSdkInstaller : IDotNetSdkInstaller
    {
        public bool CheckResult { get; set; } = true;
        public string? LastCheckedVersion { get; private set; }
        public string DotNetExecutablePath { get; set; } = "dotnet";

        public Task<bool> CheckAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CheckResult);
        }

        public Task<bool> CheckAsync(string minimumVersion, CancellationToken cancellationToken = default)
        {
            LastCheckedVersion = minimumVersion;
            return Task.FromResult(CheckResult);
        }

        public Task InstallAsync(CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }

    private sealed class TestInteractionService : IInteractionService
    {
        public Task<T> ShowStatusAsync<T>(string statusText, Func<Task<T>> action) => action();
        public void ShowStatus(string statusText, Action action) => action();
        public Task<string> PromptForStringAsync(string promptText, string? defaultValue = null, Func<string, Spectre.Console.ValidationResult>? validator = null, bool isSecret = false, bool required = false, CancellationToken cancellationToken = default) => Task.FromResult(defaultValue ?? string.Empty);
        public Task<bool> ConfirmAsync(string promptText, bool defaultValue = true, CancellationToken cancellationToken = default) => Task.FromResult(defaultValue);
        public Task<T> PromptForSelectionAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, CancellationToken cancellationToken = default) where T : notnull => Task.FromResult(choices.First());
        public int DisplayIncompatibleVersionError(Aspire.Cli.Backchannel.AppHostIncompatibleException ex, string appHostHostingVersion) => 1;
        public void DisplayError(string errorMessage) { }
        public void DisplayMessage(string emoji, string message) { }
        public void DisplayPlainText(string text) { }
        public void DisplayMarkdown(string markdown) { }
        public void DisplaySuccess(string message) { }
        public void DisplaySubtleMessage(string message) { }
        public void DisplayDashboardUrls((string BaseUrlWithLoginToken, string? CodespacesUrlWithLoginToken) dashboardUrls) { }
        public void DisplayLines(IEnumerable<(string Stream, string Line)> lines) { }
        public void DisplayCancellationMessage() { }
        public void DisplayEmptyLine() { }
        public void DisplayVersionUpdateNotification(string newerVersion) { }
        public void WriteConsoleLog(string message, int? lineNumber = null, string? type = null, bool isErrorMessage = false) { }
    }

    private sealed class TestAnsiConsole : IAnsiConsole
    {
        public Profile Profile => new Profile(new TestConsoleOutput(), Encoding.UTF8);
        public IAnsiConsoleCursor Cursor => throw new NotImplementedException();
        public IAnsiConsoleInput Input => throw new NotImplementedException();
        public IExclusivityMode ExclusivityMode => throw new NotImplementedException();
        public RenderPipeline Pipeline => throw new NotImplementedException();

        public void Clear(bool home) { }
        public void Write(IRenderable renderable) { }
    }

    private sealed class TestConsoleOutput : IAnsiConsoleOutput
    {
        public TextWriter Writer => Console.Out;
        public bool IsTerminal => false;
        public int Width => 80;
        public int Height => 25;

        public void SetEncoding(Encoding encoding) { }
    }
}