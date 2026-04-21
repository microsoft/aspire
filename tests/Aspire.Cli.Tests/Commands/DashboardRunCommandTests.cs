// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Commands;
using Aspire.Cli.DotNet;
using Aspire.Cli.Layout;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class DashboardRunCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task DashboardRunCommand_BundleNotAvailable_DisplaysError()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var testInteractionService = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("dashboard run");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.DashboardFailure, exitCode);
        var errorMessage = Assert.Single(testInteractionService.DisplayedErrors);
        Assert.Equal(DashboardCommandStrings.BundleLayoutNotFound, errorMessage);
    }

    [Fact]
    public async Task DashboardRunCommand_Help_ReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("dashboard run --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Theory]
    [InlineData("--frontend-url http://localhost:5000")]
    [InlineData("--otlp-grpc-url http://localhost:4317")]
    [InlineData("--otlp-http-url http://localhost:4318")]
    [InlineData("--allow-anonymous")]
    [InlineData("--config-file-path /path/to/config.json")]
    public void DashboardRunCommand_ParsesOptionsWithoutErrors(string args)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"dashboard run {args}");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void DashboardRunCommand_ForwardsUnmatchedTokens()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("dashboard run --ASPNETCORE_URLS=http://localhost:9999");

        Assert.Empty(result.Errors);
        Assert.Equal("--ASPNETCORE_URLS=http://localhost:9999", Assert.Single(result.UnmatchedTokens));
    }

    [Fact]
    public void DashboardRunCommand_SkipsDefaultWhenEnvVarIsSet()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var envVars = new Dictionary<string, string?>
        {
            ["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = "http://custom:9999"
        };
        var executionContext = CreateExecutionContext(workspace, envVars);

        var unmatchedTokens = Array.Empty<string>();

        Assert.True(DashboardRunCommand.ConfigSettingHasValue(unmatchedTokens, executionContext, "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"));
        Assert.False(DashboardRunCommand.ConfigSettingHasValue(unmatchedTokens, executionContext, "ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"));
    }

    [Fact]
    public void DashboardRunCommand_SkipsDefaultWhenUnmatchedTokenHasValue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var executionContext = CreateExecutionContext(workspace, new Dictionary<string, string?>());

        var unmatchedTokens = new[] { "--ASPNETCORE_URLS=http://localhost:9999" };

        Assert.True(DashboardRunCommand.ConfigSettingHasValue(unmatchedTokens, executionContext, "ASPNETCORE_URLS"));
        Assert.False(DashboardRunCommand.ConfigSettingHasValue(unmatchedTokens, executionContext, "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"));
    }

    [Fact]
    public void DashboardRunCommand_SkipsDefaultWhenUnmatchedTokenHasSpaceSeparatedValue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var executionContext = CreateExecutionContext(workspace, new Dictionary<string, string?>());

        var unmatchedTokens = new[] { "--ASPNETCORE_URLS", "http://localhost:9999" };

        Assert.True(DashboardRunCommand.ConfigSettingHasValue(unmatchedTokens, executionContext, "ASPNETCORE_URLS"));
    }

    [Fact]
    public async Task DashboardRunCommand_DefaultOptions_DoesNotEmitAllowAnonymous()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        string[]? capturedArgs = null;
        var (services, _, executionFactory) = CreateServicesWithLayout(workspace);
        executionFactory.AssertionCallback = (args, _, _, _) => { capturedArgs = args; };

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("dashboard run");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.NotNull(capturedArgs);
        Assert.DoesNotContain(capturedArgs, arg => arg.Contains("ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS"));
    }

    [Fact]
    public async Task DashboardRunCommand_DefaultOptions_PassesDefaultArgsToProcess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        string[]? capturedArgs = null;
        var (services, _, executionFactory) = CreateServicesWithLayout(workspace);
        executionFactory.AssertionCallback = (args, _, _, _) => { capturedArgs = args; };

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("dashboard run");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.NotNull(capturedArgs);
        Assert.Collection(capturedArgs,
            arg => Assert.Equal("dashboard", arg),
            arg => Assert.Equal("--ASPNETCORE_URLS=http://localhost:18888", arg),
            arg => Assert.Equal("--ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL=http://localhost:4317", arg),
            arg => Assert.Equal("--ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL=http://localhost:4318", arg),
            arg => Assert.Equal("--ASPIRE_DASHBOARD_API_ENABLED=true", arg));
    }

    [Theory]
    [InlineData("--frontend-url http://localhost:5000", "--ASPNETCORE_URLS=http://localhost:5000")]
    [InlineData("--otlp-grpc-url http://localhost:9317", "--ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL=http://localhost:9317")]
    [InlineData("--otlp-http-url http://localhost:9318", "--ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL=http://localhost:9318")]
    [InlineData("--allow-anonymous", "--ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true")]
    [InlineData("--config-file-path /path/to/config.json", "--ASPIRE_DASHBOARD_CONFIG_FILE_PATH=/path/to/config.json")]
    public async Task DashboardRunCommand_IndividualOption_PassesCorrectArgToProcess(string cliArgs, string expectedArg)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        string[]? capturedArgs = null;
        var (services, _, executionFactory) = CreateServicesWithLayout(workspace);
        executionFactory.AssertionCallback = (args, _, _, _) => { capturedArgs = args; };

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"dashboard run {cliArgs}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.NotNull(capturedArgs);
        Assert.Contains(expectedArg, capturedArgs);
    }

    [Fact]
    public async Task DashboardRunCommand_WithoutAllowAnonymous_SetsBrowserTokenEnvVar()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        IDictionary<string, string>? capturedEnv = null;
        var (services, _, executionFactory) = CreateServicesWithLayout(workspace);
        executionFactory.AssertionCallback = (_, env, _, _) => { capturedEnv = env; };

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("dashboard run");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.NotNull(capturedEnv);
        Assert.True(capturedEnv.ContainsKey("DASHBOARD__FRONTEND__BROWSERTOKEN"));
        Assert.False(string.IsNullOrEmpty(capturedEnv["DASHBOARD__FRONTEND__BROWSERTOKEN"]));
        Assert.True(capturedEnv.ContainsKey("DASHBOARD__API__PRIMARYAPIKEY"));
        Assert.False(string.IsNullOrEmpty(capturedEnv["DASHBOARD__API__PRIMARYAPIKEY"]));
        Assert.Equal("ApiKey", capturedEnv["DASHBOARD__API__AUTHMODE"]);
    }

    [Fact]
    public async Task DashboardRunCommand_UnmatchedTokens_ForwardedToProcess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        string[]? capturedArgs = null;
        var (services, _, executionFactory) = CreateServicesWithLayout(workspace);
        executionFactory.AssertionCallback = (args, _, _, _) => { capturedArgs = args; };

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("dashboard run --CUSTOM_SETTING=myvalue");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.NotNull(capturedArgs);
        Assert.Collection(capturedArgs,
            arg => Assert.Equal("dashboard", arg),
            arg => Assert.Equal("--ASPNETCORE_URLS=http://localhost:18888", arg),
            arg => Assert.Equal("--ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL=http://localhost:4317", arg),
            arg => Assert.Equal("--ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL=http://localhost:4318", arg),
            arg => Assert.Equal("--ASPIRE_DASHBOARD_API_ENABLED=true", arg),
            arg => Assert.Equal("--CUSTOM_SETTING=myvalue", arg));
    }

    [Fact]
    public async Task DashboardRunCommand_CombinedOptions_PassesAllArgsToProcess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        string[]? capturedArgs = null;
        var (services, _, executionFactory) = CreateServicesWithLayout(workspace);
        executionFactory.AssertionCallback = (args, _, _, _) => { capturedArgs = args; };

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("dashboard run --frontend-url http://localhost:5000 --otlp-grpc-url http://localhost:9317 --otlp-http-url http://localhost:9318 --allow-anonymous --config-file-path /my/config.json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.NotNull(capturedArgs);
        Assert.Collection(capturedArgs,
            arg => Assert.Equal("dashboard", arg),
            arg => Assert.Equal("--ASPNETCORE_URLS=http://localhost:5000", arg),
            arg => Assert.Equal("--ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL=http://localhost:9317", arg),
            arg => Assert.Equal("--ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL=http://localhost:9318", arg),
            arg => Assert.Equal("--ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true", arg),
            arg => Assert.Equal("--ASPIRE_DASHBOARD_API_ENABLED=true", arg),
            arg => Assert.Equal("--ASPIRE_DASHBOARD_CONFIG_FILE_PATH=/my/config.json", arg));
    }

    [Fact]
    public async Task DashboardRunCommand_ProcessExitsWithError_ReturnsFailure()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var testInteractionService = new TestInteractionService();
        var (services, _, executionFactory) = CreateServicesWithLayout(workspace, interactionService: testInteractionService);
        executionFactory.AttemptCallback = (_, _) => (1, null);

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("dashboard run");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.DashboardFailure, exitCode);
    }

    [Fact]
    public async Task DashboardRunCommand_ProcessFailsToStart_DisplaysErrorAndReturnsFailure()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var testInteractionService = new TestInteractionService();
        var (services, managedPath, executionFactory) = CreateServicesWithLayout(workspace, interactionService: testInteractionService);

        // Make CreateExecution return an execution whose Start() returns false,
        // which causes LayoutProcessRunner.Start to throw InvalidOperationException.
        executionFactory.CreateExecutionCallback = (_, _, _, _) =>
            new TestProcessExecution("fake", [], null, new ProcessInvocationOptions(), (_, _) => (0, null), () => 0)
            {
                StartReturnValue = false
            };

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("dashboard run");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.DashboardFailure, exitCode);
        var errorMessage = Assert.Single(testInteractionService.DisplayedErrors);
        var expectedMessage = string.Format(CultureInfo.CurrentCulture, DashboardCommandStrings.DashboardFailedToStart, $"Failed to start process: {managedPath}");
        Assert.Equal(expectedMessage, errorMessage);
    }

    [Theory]
    [InlineData("", "http://localhost:18888")]
    [InlineData(";;;", "http://localhost:18888")]
    [InlineData("http://first:5000;http://second:5001", "http://first:5000")]
    [InlineData("http://custom:9000", "http://custom:9000")]
    [InlineData("http://trailing:8080/", "http://trailing:8080")]
    public void ResolveDashboardInfo_FrontendUrlVariants_ResolvesExpectedUrl(string urlValue, string expectedDashboardUrl)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var executionContext = CreateExecutionContext(workspace, new Dictionary<string, string?>());

        var args = new List<string> { "dashboard", $"--ASPNETCORE_URLS={urlValue}" };
        var unmatchedTokens = Array.Empty<string>();

        var info = DashboardRunCommand.ResolveDashboardInfo(args, unmatchedTokens, executionContext, browserToken: null);

        Assert.Equal(expectedDashboardUrl, info.DashboardUrl);
    }

    [Fact]
    public void ResolveDashboardInfo_EnvVarFrontendUrl_UsedInSummary()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var executionContext = CreateExecutionContext(workspace, new Dictionary<string, string?>
        {
            ["ASPNETCORE_URLS"] = "http://envhost:9999"
        });

        // No arg in the list — should fall back to the environment variable.
        var args = new List<string> { "dashboard" };
        var unmatchedTokens = Array.Empty<string>();

        var info = DashboardRunCommand.ResolveDashboardInfo(args, unmatchedTokens, executionContext, browserToken: null);

        Assert.Equal("http://envhost:9999", info.DashboardUrl);
    }

    [Fact]
    public void ResolveDashboardInfo_SpaceSeparatedUnmatchedToken_UsedInSummary()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var executionContext = CreateExecutionContext(workspace, new Dictionary<string, string?>());

        // No --ASPNETCORE_URLS= in the args list; the value comes through space-separated unmatched tokens.
        var args = new List<string> { "dashboard" };
        var unmatchedTokens = new[] { "--ASPNETCORE_URLS", "http://space:7777" };

        var info = DashboardRunCommand.ResolveDashboardInfo(args, unmatchedTokens, executionContext, browserToken: null);

        Assert.Equal("http://space:7777", info.DashboardUrl);
    }

    [Fact]
    public void ResolveDashboardInfo_EnvVarOtlpUrls_UsedInSummary()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var executionContext = CreateExecutionContext(workspace, new Dictionary<string, string?>
        {
            ["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = "http://grpc:1111",
            ["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] = "http://http:2222"
        });

        var args = new List<string> { "dashboard" };
        var unmatchedTokens = Array.Empty<string>();

        var info = DashboardRunCommand.ResolveDashboardInfo(args, unmatchedTokens, executionContext, browserToken: null);

        Assert.Equal("http://grpc:1111", info.OtlpGrpcUrl);
        Assert.Equal("http://http:2222", info.OtlpHttpUrl);
    }

    [Fact]
    public void ResolveDashboardInfo_ArgTakesPrecedenceOverEnvVar()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var executionContext = CreateExecutionContext(workspace, new Dictionary<string, string?>
        {
            ["ASPNETCORE_URLS"] = "http://envhost:9999"
        });

        var args = new List<string> { "dashboard", "--ASPNETCORE_URLS=http://arghost:5555" };
        var unmatchedTokens = Array.Empty<string>();

        var info = DashboardRunCommand.ResolveDashboardInfo(args, unmatchedTokens, executionContext, browserToken: null);

        Assert.Equal("http://arghost:5555", info.DashboardUrl);
    }

    [Fact]
    public void ResolveDashboardInfo_WithBrowserToken_AppendsLoginPath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var executionContext = CreateExecutionContext(workspace, new Dictionary<string, string?>());

        var args = new List<string> { "dashboard", "--ASPNETCORE_URLS=http://localhost:18888" };
        var unmatchedTokens = Array.Empty<string>();

        var info = DashboardRunCommand.ResolveDashboardInfo(args, unmatchedTokens, executionContext, browserToken: "abc123");

        Assert.Equal("http://localhost:18888/login?t=abc123", info.DashboardUrl);
    }

    private (IServiceCollection Services, string ManagedPath, TestProcessExecutionFactory ExecutionFactory) CreateServicesWithLayout(
        TemporaryWorkspace workspace,
        TestInteractionService? interactionService = null)
    {
        var layoutDir = Path.Combine(workspace.WorkspaceRoot.FullName, "layout");
        var managedDir = Path.Combine(layoutDir, "managed");
        Directory.CreateDirectory(managedDir);
        var managedPath = Path.Combine(managedDir, BundleDiscovery.GetExecutableFileName("aspire-managed"));
        File.WriteAllText(managedPath, "fake");

        var layout = new LayoutConfiguration
        {
            LayoutPath = layoutDir,
            Components = new LayoutComponents { Managed = "managed" }
        };

        var executionFactory = new TestProcessExecutionFactory
        {
            AttemptCallback = (_, _) => (0, "Now listening on: http://localhost:18888")
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.LayoutDiscoveryFactory = _ => new FakeLayoutDiscovery(layout);
            options.BundleServiceFactory = _ => new TestBundleService(true) { Layout = layout };
            options.DotNetCliExecutionFactoryFactory = _ => executionFactory;
            if (interactionService is not null)
            {
                options.InteractionServiceFactory = _ => interactionService;
            }
        });

        return (services, managedPath, executionFactory);
    }

    private static CliExecutionContext CreateExecutionContext(TemporaryWorkspace workspace, Dictionary<string, string?> envVars)
    {
        var dir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(dir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(dir.FullName, ".aspire", "cache"));
        var logsDir = new DirectoryInfo(Path.Combine(dir.FullName, ".aspire", "logs"));
        var logFile = Path.Combine(logsDir.FullName, "test.log");
        return new CliExecutionContext(dir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-sdks")), logsDir, logFile, environmentVariables: envVars);
    }

    private sealed class FakeLayoutDiscovery(LayoutConfiguration layout) : ILayoutDiscovery
    {
        public LayoutConfiguration? DiscoverLayout(string? projectDirectory = null) => layout;
        public string? GetComponentPath(LayoutComponent component, string? projectDirectory = null) => layout.GetComponentPath(component);
        public bool IsBundleModeAvailable(string? projectDirectory = null) => true;
    }

}
