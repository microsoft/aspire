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
        var interactionService = new TestInteractionService();
        var console = new TestAnsiConsole();

        var selector = new DotNetRuntimeSelector(logger, configuration, interactionService, console);

        Assert.Equal(DotNetRuntimeMode.System, selector.Mode);
        Assert.Equal("dotnet", selector.DotNetExecutablePath);
    }

    [Fact]
    public async Task InitializeAsync_WithAvailableSystemSdk_ReturnsTrue()
    {
        var logger = NullLogger<DotNetRuntimeSelector>.Instance;
        // Use a version that's very likely available in any test environment
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("overrideMinimumSdkVersion", "1.0.0")
            })
            .Build();
        var interactionService = new TestInteractionService();
        var console = new TestAnsiConsole();

        var selector = new DotNetRuntimeSelector(logger, configuration, interactionService, console);

        var result = await selector.InitializeAsync();

        Assert.True(result);
        Assert.Equal(DotNetRuntimeMode.System, selector.Mode);
        Assert.Equal("dotnet", selector.DotNetExecutablePath);
    }

    [Fact]
    public async Task InitializeAsync_WithDisablePrivateSdkAndMissingSdk_ReturnsFalse()
    {
        var logger = NullLogger<DotNetRuntimeSelector>.Instance;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("ASPIRE_DISABLE_PRIVATE_SDK", "1"),
                // Use an unreasonably high version that won't be on any system
                new KeyValuePair<string, string?>("overrideMinimumSdkVersion", "99.0.0")
            })
            .Build();
        var interactionService = new TestInteractionService();
        var console = new TestAnsiConsole();

        var selector = new DotNetRuntimeSelector(logger, configuration, interactionService, console);

        var result = await selector.InitializeAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task InitializeAsync_WithVersionOverride_UsesOverrideVersion()
    {
        var logger = NullLogger<DotNetRuntimeSelector>.Instance;
        // Use a version that should be available in the test environment
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("overrideMinimumSdkVersion", "1.0.0")
            })
            .Build();
        var interactionService = new TestInteractionService();
        var console = new TestAnsiConsole();

        var selector = new DotNetRuntimeSelector(logger, configuration, interactionService, console);

        var result = await selector.InitializeAsync();

        // Should succeed with an old version override since we have dotnet installed
        Assert.True(result);
    }

    [Fact]
    public async Task InitializeAsync_ConfigurationVersionTakesPrecedenceOverEnvironment()
    {
        var logger = NullLogger<DotNetRuntimeSelector>.Instance;
        // Config says use a very low version (should pass)
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("ASPIRE_DOTNET_SDK_VERSION", "1.0.0")
            })
            .Build();
        var interactionService = new TestInteractionService();
        var console = new TestAnsiConsole();

        // Environment says use a very high version that would fail
        Environment.SetEnvironmentVariable("ASPIRE_DOTNET_SDK_VERSION", "99.0.0");
        Environment.SetEnvironmentVariable("ASPIRE_DISABLE_PRIVATE_SDK", "1");

        try
        {
            var selector = new DotNetRuntimeSelector(logger, configuration, interactionService, console);

            var result = await selector.InitializeAsync();

            // Config value (1.0.0) should take precedence over env var (99.0.0)
            // So initialization should succeed since dotnet 1.0.0+ is definitely available
            Assert.True(result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPIRE_DOTNET_SDK_VERSION", null);
            Environment.SetEnvironmentVariable("ASPIRE_DISABLE_PRIVATE_SDK", null);
        }
    }

    [Fact]
    public void GetEnvironmentVariables_ReturnsEmptyDictionary_WhenNotInitialized()
    {
        var logger = NullLogger<DotNetRuntimeSelector>.Instance;
        var configuration = new ConfigurationBuilder().Build();
        var interactionService = new TestInteractionService();
        var console = new TestAnsiConsole();

        var selector = new DotNetRuntimeSelector(logger, configuration, interactionService, console);

        var envVars = selector.GetEnvironmentVariables();

        Assert.Empty(envVars);
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
