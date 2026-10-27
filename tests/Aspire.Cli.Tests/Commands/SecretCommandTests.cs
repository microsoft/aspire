// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Commands;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Secrets;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared.UserSecrets;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using InvocationConfiguration = System.CommandLine.InvocationConfiguration;

namespace Aspire.Cli.Tests.Commands;

public class SecretCommandTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("AppHost.csproj", false, true, "environment-secret", "Development")]
    [InlineData("AppHost.csproj", true, true, "", "Development")]
    [InlineData("apphost.ts", false, true, " \t ", "Development")]
    [InlineData("apphost.ts", true, true, "first\nsecond\r\n\"quotes\" 'single' $dollar [brackets] \\backslash", "Development")]
    [InlineData("AppHost.csproj", false, false, "positional-secret", "Development")]
    [InlineData("AppHost.csproj", true, false, "", "Development")]
    [InlineData("apphost.ts", false, false, " \t ", "Development")]
    [InlineData("apphost.ts", true, false, "positional-secret", "Development")]
    [InlineData("AppHost.csproj", true, true, "production-secret", "Production")]
    [InlineData("apphost.ts", false, true, "production-secret", "Production")]
    public async Task SecretSetCommand_PreservesValueAndOtherSecrets(
        string fileName, bool explicitAppHost, bool useEnvvarSource, string value, string environmentName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.Path, fileName));
        var userSecretsId = Guid.NewGuid().ToString("N");
        var secretsPath = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, environmentName);
        var interaction = new TestInteractionService();
        await File.WriteAllTextAsync(appHostFile.FullName, "");

        try
        {
            var store = new SecretsStore(secretsPath);
            store.Set("Parameters:api_key", "original");
            store.Set("Other:key", "preserved");
            store.Save();

            using var provider = CreateSecretTestServices(workspace, outputWriter, appHostFile, userSecretsId,
                new TestEnvironment(new Dictionary<string, string?> { ["API_KEY"] = useEnvvarSource ? value : "not-the-positional-value" }),
                options => options.InteractionServiceFactory = _ => interaction);
            var command = provider.GetRequiredService<RootCommand>();
            List<string> arguments = ["secret", "set", "Parameters:api_key", "--environment", environmentName];
            arguments.AddRange(useEnvvarSource ? ["--envvar", "API_KEY"] : [value]);
            if (explicitAppHost)
            {
                arguments.AddRange(["--apphost", appHostFile.FullName]);
            }

            var result = command.Parse(arguments.ToArray());
            Assert.Empty(result.Errors);
            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, exitCode);
            Assert.Empty(interaction.StringPromptCalls);
            Assert.Collection(new SecretsStore(secretsPath).AsEnumerable(),
                pair => Assert.Equal(new("Parameters:api_key", value), pair),
                pair => Assert.Equal(new KeyValuePair<string, string>("Other:key", "preserved"), pair));
            var project = Assert.IsType<TestAppHostProject>(provider.GetRequiredService<IAppHostProjectFactory>().GetProject(appHostFile));
            Assert.Equal(1, project.GetUserSecretsIdCallCount);
            Assert.Equal(environmentName == "Development", project.AutoInit);
            var locator = Assert.IsType<TestProjectLocator>(provider.GetRequiredService<IProjectLocator>());
            Assert.Equal(1, locator.ResolveCallCount);
            Assert.Equal(explicitAppHost ? appHostFile.FullName : null, locator.RequestedProjectFile?.FullName);
        }
        finally
        {
            File.Delete(secretsPath);
            Directory.Delete(Path.GetDirectoryName(secretsPath)!);
        }
    }

    [Theory]
    [InlineData("prompted-secret", "Development")]
    [InlineData("", "Development")]
    [InlineData("  secret [value] $literal  ", "Development")]
    [InlineData("production-secret", "Production")]
    public async Task SecretSetCommand_NoValue_PromptsForMaskedSecret(string value, string environmentName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.Path, "AppHost.csproj"));
        var userSecretsId = Guid.NewGuid().ToString("N");
        var secretsPath = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, environmentName);
        var interaction = new TestInteractionService();
        interaction.SetupStringPromptResponse(value);

        try
        {
            using var provider = CreateSecretTestServices(workspace, outputWriter, appHostFile, userSecretsId,
                configure: options => options.InteractionServiceFactory = _ => interaction);
            var result = provider.GetRequiredService<RootCommand>().Parse(["secret", "set", "Parameters:api_key", "--environment", environmentName]);
            Assert.Empty(result.Errors);

            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, exitCode);
            Assert.Equal(value, new SecretsStore(secretsPath).Get("Parameters:api_key"));
            Assert.Equal(new StringPromptCall(SecretCommandStrings.ValuePrompt, null, IsSecret: true),
                Assert.Single(interaction.StringPromptCalls));
            Assert.Equal(["Secret 'Parameters:api_key' set successfully."], interaction.DisplayedSuccess);
            Assert.Empty(interaction.DisplayedPlainText);
            Assert.Empty(interaction.DisplayedRawText);
            Assert.Empty(interaction.DisplayedErrors);
        }
        finally
        {
            if (File.Exists(secretsPath))
            {
                File.Delete(secretsPath);
                Directory.Delete(Path.GetDirectoryName(secretsPath)!);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecretSetCommand_CancelledPrompt_DoesNotChangeOrInitializeStore(bool existingStore)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.Path, "AppHost.csproj"));
        var userSecretsId = Guid.NewGuid().ToString("N");
        var secretsPath = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, "Development");
        var promptCount = 0;

        try
        {
            if (existingStore)
            {
                var store = new SecretsStore(secretsPath);
                store.Set("Parameters:api_key", "original");
                store.Save();
            }
            var originalContents = existingStore ? await File.ReadAllTextAsync(secretsPath) : null;
            using var provider = CreateSecretTestServices(workspace, outputWriter, appHostFile, userSecretsId,
                configure: options =>
                {
                    options.ExtensionBackchannelFactory = _ => new TestExtensionBackchannel();
                    options.InteractionServiceFactory = sp => new TestExtensionInteractionService(sp)
                    {
                        PromptForStringCallback = (_, _, isSecret, required, _, _) =>
                        {
                            promptCount++;
                            Assert.True(isSecret);
                            Assert.False(required);
                            throw new ExtensionOperationCanceledException("Secret entry canceled.");
                        }
                    };
                });
            var result = provider.GetRequiredService<RootCommand>().Parse(["secret", "set", "Parameters:api_key"]);
            Assert.Empty(result.Errors);

            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.Cancelled, exitCode);
            Assert.Equal(1, promptCount);
            Assert.Equal(originalContents, File.Exists(secretsPath) ? await File.ReadAllTextAsync(secretsPath) : null);
            Assert.Equal(0, Assert.IsType<TestAppHostProject>(provider.GetRequiredService<IAppHostProjectFactory>().GetProject(appHostFile)).GetUserSecretsIdCallCount);
            Assert.Equal(0, Assert.IsType<TestProjectLocator>(provider.GetRequiredService<IProjectLocator>()).ResolveCallCount);
        }
        finally
        {
            if (File.Exists(secretsPath))
            {
                File.Delete(secretsPath);
                Directory.Delete(Path.GetDirectoryName(secretsPath)!);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SecretSetCommand_UnsetVariable_DoesNotChangeOrInitializeStore(bool existingStore)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        using var errorWriter = new StringWriter();
        var appHostFile = new FileInfo(Path.Combine(workspace.Path, "AppHost.csproj"));
        var userSecretsId = Guid.NewGuid().ToString("N");
        var secretsPath = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, "Development");

        try
        {
            if (existingStore)
            {
                var store = new SecretsStore(secretsPath);
                store.Set("Parameters:api_key", "original");
                store.Save();
            }
            var originalContents = existingStore ? await File.ReadAllTextAsync(secretsPath) : null;
            using var provider = CreateSecretTestServices(workspace, outputWriter, appHostFile, userSecretsId, new TestEnvironment(),
                options => options.ErrorTextWriter = errorWriter);
            var command = provider.GetRequiredService<RootCommand>();

            var result = command.Parse(["secret", "set", "Parameters:api_key", "--envvar", "MISSING_API_KEY"]);
            Assert.Empty(result.Errors);
            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
            Assert.Equal(originalContents, File.Exists(secretsPath) ? await File.ReadAllTextAsync(secretsPath) : null);
            var project = Assert.IsType<TestAppHostProject>(provider.GetRequiredService<IAppHostProjectFactory>().GetProject(appHostFile));
            Assert.Equal(0, project.GetUserSecretsIdCallCount);
            Assert.Equal(0, Assert.IsType<TestProjectLocator>(provider.GetRequiredService<IProjectLocator>()).ResolveCallCount);
            Assert.Contains("Environment variable 'MISSING_API_KEY' is not set.", errorWriter.ToString());
        }
        finally
        {
            if (existingStore)
            {
                File.Delete(secretsPath);
                Directory.Delete(Path.GetDirectoryName(secretsPath)!);
            }
        }
    }

    [Theory]
    [InlineData(new[] { "positional-secret", "--envvar", "API_KEY" }, nameof(SecretCommandStrings.ConflictingValueSources))]
    [InlineData(new[] { "", "--envvar", "API_KEY" }, nameof(SecretCommandStrings.ConflictingValueSources))]
    [InlineData(new[] { "--non-interactive" }, nameof(SecretCommandStrings.ValueSourceRequired))]
    [InlineData(new[] { "--envvar", "" }, nameof(SecretCommandStrings.EnvvarSourceNameRequired))]
    [InlineData(new[] { "--envvar", " \t " }, nameof(SecretCommandStrings.EnvvarSourceNameRequired))]
    [InlineData(new[] { "--envvar" }, null)]
    public async Task SecretSetCommand_InvalidSources_AreRejectedBeforeExecution(string[] sourceArguments, string? resourceName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.Path, "AppHost.csproj"));
        var userSecretsId = Guid.NewGuid().ToString("N");
        var secretsPath = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, "Development");

        try
        {
            var store = new SecretsStore(secretsPath);
            store.Set("Parameters:api_key", "original");
            store.Save();
            var originalContents = await File.ReadAllTextAsync(secretsPath);
            using var provider = CreateSecretTestServices(workspace, outputWriter, appHostFile, userSecretsId,
                new TestEnvironment(new Dictionary<string, string?> { ["API_KEY"] = "environment-secret" }));
            var command = provider.GetRequiredService<RootCommand>();

            var result = command.Parse(["secret", "set", "Parameters:api_key", .. sourceArguments]);
            var error = Assert.Single(result.Errors);
            if (resourceName is not null)
            {
                Assert.Equal(SecretCommandStrings.ResourceManager.GetString(resourceName), error.Message);
            }
            var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = outputWriter, Error = outputWriter }).DefaultTimeout();

            Assert.NotEqual(CliExitCodes.Success, exitCode);
            Assert.Equal(originalContents, await File.ReadAllTextAsync(secretsPath));
            Assert.Equal(0, Assert.IsType<TestAppHostProject>(provider.GetRequiredService<IAppHostProjectFactory>().GetProject(appHostFile)).GetUserSecretsIdCallCount);
            Assert.Equal(0, Assert.IsType<TestProjectLocator>(provider.GetRequiredService<IProjectLocator>()).ResolveCallCount);
        }
        finally
        {
            File.Delete(secretsPath);
            Directory.Delete(Path.GetDirectoryName(secretsPath)!);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecretSetCommand_Diagnostics_DoNotExposeEnvironmentValue(bool conflictingValue)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        using var errorWriter = new StringWriter();
        using var parserWriter = new StringWriter();
        var appHostFile = new FileInfo(Path.Combine(workspace.Path, "AppHost.csproj"));
        var userSecretsId = Guid.NewGuid().ToString("N");
        var secretsPath = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, "Development");
        var telemetryLogger = new FakeLogger<AspireCliTelemetry>();
        using var telemetry = new TelemetryFixture(logger: telemetryLogger);
        var initialActivity = telemetry.CapturedActivity;
        var initialEventCount = telemetryLogger.Collector.GetSnapshot().Count;
        const string secretValue = "environment-value-must-not-appear-in-diagnostics";

        try
        {
            using var provider = CreateSecretTestServices(workspace, outputWriter, appHostFile, userSecretsId,
                new TestEnvironment(new Dictionary<string, string?> { ["API_KEY"] = secretValue }),
                options =>
                {
                    options.TelemetryFactory = _ => telemetry.Telemetry;
                    options.ErrorTextWriter = errorWriter;
                });
            var fileLogger = provider.GetRequiredService<FileLoggerProvider>();
            provider.GetRequiredService<ILoggerFactory>().AddProvider(fileLogger);
            var command = provider.GetRequiredService<RootCommand>();
            string[] arguments = conflictingValue
                ? ["secret", "set", "Parameters:api_key", secretValue, "--envvar", "API_KEY", "--log-level", "Trace"]
                : ["secret", "set", "Parameters:api_key", "--envvar", "API_KEY", "--log-level", "Trace"];

            var result = command.Parse(arguments);
            var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = parserWriter, Error = errorWriter }).DefaultTimeout();

            Assert.Equal(conflictingValue, exitCode != CliExitCodes.Success);
            if (conflictingValue)
            {
                Assert.False(File.Exists(secretsPath));
            }
            else
            {
                Assert.Equal(secretValue, new SecretsStore(secretsPath).Get("Parameters:api_key"));
            }
            Assert.Same(initialActivity, telemetry.CapturedActivity);
            Assert.Equal(initialEventCount, telemetryLogger.Collector.GetSnapshot().Count);
            fileLogger.Dispose();
            var diagnosticLog = await File.ReadAllTextAsync(fileLogger.LogFilePath);
            Assert.False(diagnosticLog.Contains(secretValue, StringComparison.Ordinal));
            await Verify(new { Output = outputWriter.Logs, ParserOutput = parserWriter.ToString(), Error = errorWriter.ToString() })
                .UseParameters(conflictingValue).ScrubRootCommandName();
        }
        finally
        {
            if (File.Exists(secretsPath))
            {
                File.Delete(secretsPath);
                Directory.Delete(Path.GetDirectoryName(secretsPath)!);
            }
        }
    }

    [Fact]
    public async Task SecretSetCommand_Help_DescribesBothValueSources()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.Path, "AppHost.csproj"));
        using var provider = CreateSecretTestServices(workspace, outputWriter, appHostFile, Guid.NewGuid().ToString("N"));
        using var helpWriter = new StringWriter();
        var result = provider.GetRequiredService<RootCommand>().Parse("secret set --help");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = helpWriter }).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        await Verify(helpWriter.ToString(), extension: "txt").ScrubRootCommandName();
    }

    [Fact]
    public async Task SecretPathCommand_PrintsSecretsPath_ForDotNetAppHost()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        var userSecretsId = Guid.NewGuid().ToString("N");
        var expectedPath = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, "Development");

        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");

        using var provider = CreateSecretTestServices(
            workspace,
            outputWriter,
            appHostFile,
            userSecretsId);

        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse($"secret path --apphost \"{appHostFile.FullName}\"");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains(expectedPath, outputWriter.Logs);
    }

    [Fact]
    public async Task SecretPathCommand_PrintsSecretsPath_ForGuestAppHost()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"));
        var userSecretsId = UserSecretsPathHelper.ComputeSyntheticUserSecretsId(appHostFile.FullName);
        var expectedPath = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, "Development");

        await File.WriteAllTextAsync(appHostFile.FullName, "export {};");

        using var provider = CreateSecretTestServices(
            workspace,
            outputWriter,
            appHostFile,
            userSecretsId);

        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse($"secret path --apphost \"{appHostFile.FullName}\"");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains(expectedPath, outputWriter.Logs);
    }

    [Fact]
    public async Task SecretCommands_UseEnvironmentSpecificAspireSecretsFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        var userSecretsId = Guid.NewGuid().ToString("N");
        var developmentPath = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, "Development");
        var productionPath = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, "Production");

        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");

        using var provider = CreateSecretTestServices(
            workspace,
            outputWriter,
            appHostFile,
            userSecretsId);

        var command = provider.GetRequiredService<RootCommand>();

        var setResult = command.Parse($"secret set --environment Production Parameters:api_key prod-secret --apphost \"{appHostFile.FullName}\"");
        var setExitCode = await setResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, setExitCode);
        Assert.False(File.Exists(developmentPath));
        Assert.True(File.Exists(productionPath));

        outputWriter.Logs.Clear();
        var getResult = command.Parse($"secret get --environment Production Parameters:api_key --apphost \"{appHostFile.FullName}\"");
        var getExitCode = await getResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, getExitCode);
        Assert.Contains("prod-secret", outputWriter.Logs);

        outputWriter.Logs.Clear();
        var listResult = command.Parse($"secret list --environment Production --format json --apphost \"{appHostFile.FullName}\"");
        var listExitCode = await listResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, listExitCode);
        Assert.Contains(outputWriter.Logs, line => line.Contains("Parameters:api_key", StringComparison.Ordinal));

        var defaultGetResult = command.Parse($"secret get Parameters:api_key --apphost \"{appHostFile.FullName}\"");
        var defaultGetExitCode = await defaultGetResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.ConfigNotFound, defaultGetExitCode);

        var deleteResult = command.Parse($"secret delete --environment Production Parameters:api_key --apphost \"{appHostFile.FullName}\"");
        var deleteExitCode = await deleteResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, deleteExitCode);

        var deletedGetResult = command.Parse($"secret get --environment Production Parameters:api_key --apphost \"{appHostFile.FullName}\"");
        var deletedGetExitCode = await deletedGetResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.ConfigNotFound, deletedGetExitCode);
    }

    [Fact]
    public async Task SecretCommands_PreserveDeploymentSecretsAfterDevelopmentInitializesUserSecrets()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        var userSecretsId = Guid.NewGuid().ToString("N");
        var project = new TestAppHostProject(userSecretsId, initializeOnDemand: true);
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        using var provider = CreateSecretTestServices(workspace, outputWriter, appHostFile, project);
        var command = provider.GetRequiredService<RootCommand>();
        var projectOption = $"--apphost \"{appHostFile.FullName}\"";
        var syntheticPath = AspireSecretsStoreResolver.GetSecretsFilePath(
            workspace.WorkspaceRoot, AspireSecretsPathHelper.ComputeSyntheticAppHostId(appHostFile.FullName), "Production");

        Assert.Equal(CliExitCodes.Success, await command.Parse($"secret set --environment Production Parameters:key prod-secret {projectOption}").InvokeAsync().DefaultTimeout());
        Assert.False(project.IsInitialized);
        Assert.True(File.Exists(syntheticPath));

        Assert.Equal(CliExitCodes.Success, await command.Parse($"secret set Parameters:key dev-secret {projectOption}").InvokeAsync().DefaultTimeout());
        Assert.True(project.IsInitialized);
        Assert.False(File.Exists(syntheticPath));
        Assert.False(File.Exists(UserSecretsPathHelper.GetSecretsPathFromSecretsId(userSecretsId)));

        outputWriter.Logs.Clear();
        Assert.Equal(CliExitCodes.Success, await command.Parse($"secret get --environment Production Parameters:key {projectOption}").InvokeAsync().DefaultTimeout());
        Assert.Contains("prod-secret", outputWriter.Logs);

        outputWriter.Logs.Clear();
        Assert.Equal(CliExitCodes.Success, await command.Parse($"secret list --all --format json {projectOption}").InvokeAsync().DefaultTimeout());
        Assert.Contains(outputWriter.Logs, line => line.Contains("prod-secret", StringComparison.Ordinal));
        Assert.Contains(outputWriter.Logs, line => line.Contains("dev-secret", StringComparison.Ordinal));

        Assert.Equal(CliExitCodes.Success, await command.Parse($"secret delete --environment Production Parameters:key {projectOption}").InvokeAsync().DefaultTimeout());
        Assert.Equal(CliExitCodes.ConfigNotFound, await command.Parse($"secret get --environment Production Parameters:key {projectOption}").InvokeAsync().DefaultTimeout());
    }

    [Fact]
    public async Task SecretListCommand_AllIncludesLowercaseDevelopmentEnvironment()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        var userSecretsId = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        using var provider = CreateSecretTestServices(workspace, outputWriter, appHostFile, userSecretsId);
        var command = provider.GetRequiredService<RootCommand>();
        var projectOption = $"--apphost \"{appHostFile.FullName}\"";

        Assert.Equal(CliExitCodes.Success, await command.Parse(
            $"secret set --environment development Parameters:key lowercase-secret {projectOption}").InvokeAsync().DefaultTimeout());
        outputWriter.Logs.Clear();

        Assert.Equal(CliExitCodes.Success, await command.Parse(
            $"secret list --all --format json {projectOption}").InvokeAsync().DefaultTimeout());
        Assert.Contains(outputWriter.Logs, line => line.Contains("\"development\"", StringComparison.Ordinal));
        Assert.Contains(outputWriter.Logs, line => line.Contains("\"lowercase-secret\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SecretListCommand_CanListAllEnvironmentSecrets()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        var userSecretsId = Guid.NewGuid().ToString("N");
        var developmentUserSecretsPath = UserSecretsPathHelper.GetSecretsPathFromSecretsId(userSecretsId);
        var developmentAspirePath = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, "Development");

        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");

        using var provider = CreateSecretTestServices(
            workspace,
            outputWriter,
            appHostFile,
            userSecretsId);

        var command = provider.GetRequiredService<RootCommand>();

        var setDevelopmentResult = command.Parse($"secret set Parameters:api_key dev-secret --apphost \"{appHostFile.FullName}\"");
        var setDevelopmentExitCode = await setDevelopmentResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, setDevelopmentExitCode);
        Assert.False(File.Exists(developmentUserSecretsPath));
        Assert.True(File.Exists(developmentAspirePath));

        var setProductionResult = command.Parse($"secret set --environment Production Azure:SubscriptionId prod-subscription --apphost \"{appHostFile.FullName}\"");
        var setProductionExitCode = await setProductionResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, setProductionExitCode);

        outputWriter.Logs.Clear();
        var listAllResult = command.Parse($"secret list --all --format json --apphost \"{appHostFile.FullName}\"");
        var listAllExitCode = await listAllResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, listAllExitCode);
        Assert.Contains(outputWriter.Logs, line => line.Contains("\"Development\"", StringComparison.Ordinal));
        Assert.Contains(outputWriter.Logs, line => line.Contains("\"Parameters:api_key\"", StringComparison.Ordinal));
        Assert.Contains(outputWriter.Logs, line => line.Contains("\"dev-secret\"", StringComparison.Ordinal));
        Assert.Contains(outputWriter.Logs, line => line.Contains("\"Production\"", StringComparison.Ordinal));
        Assert.Contains(outputWriter.Logs, line => line.Contains("\"Azure:SubscriptionId\"", StringComparison.Ordinal));
        Assert.Contains(outputWriter.Logs, line => line.Contains("\"prod-subscription\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SecretSet_DefaultDevelopmentImportsLegacyUserSecretsAndWritesAspireSecrets()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        var userSecretsId = Guid.NewGuid().ToString("N");
        var userSecretsPath = UserSecretsPathHelper.GetSecretsPathFromSecretsId(userSecretsId);
        var aspirePath = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, "Development");

        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        Directory.CreateDirectory(Path.GetDirectoryName(userSecretsPath)!);
        await File.WriteAllTextAsync(userSecretsPath, """
            {
              "Legacy:Value": "old"
            }
            """);

        try
        {
            using var provider = CreateSecretTestServices(
                workspace,
                outputWriter,
                appHostFile,
                userSecretsId);

            var command = provider.GetRequiredService<RootCommand>();

            var getLegacyResult = command.Parse($"secret get Legacy:Value --apphost \"{appHostFile.FullName}\"");
            var getLegacyExitCode = await getLegacyResult.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, getLegacyExitCode);
            Assert.Contains("old", outputWriter.Logs);

            var setResult = command.Parse($"secret set New:Value new --apphost \"{appHostFile.FullName}\"");
            var setExitCode = await setResult.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, setExitCode);

            var userSecretsStore = new SecretsStore(userSecretsPath);
            Assert.Equal("old", userSecretsStore.Get("Legacy:Value"));
            Assert.Null(userSecretsStore.Get("New:Value"));

            var aspireStore = new SecretsStore(aspirePath);
            Assert.Equal("old", aspireStore.Get("Legacy:Value"));
            Assert.Equal("new", aspireStore.Get("New:Value"));
        }
        finally
        {
            if (File.Exists(userSecretsPath))
            {
                File.Delete(userSecretsPath);
            }
        }
    }

    [Theory]
    [InlineData("set Parameters:key value")]
    [InlineData("get Parameters:key")]
    [InlineData("list")]
    [InlineData("delete Parameters:key")]
    [InlineData("path")]
    public void SecretCommands_RejectInvalidEnvironmentNamesBeforeExecution(string arguments)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        using var provider = CreateSecretTestServices(workspace, outputWriter, appHostFile, Guid.NewGuid().ToString("N"));
        var command = provider.GetRequiredService<RootCommand>();

        foreach (var environment in new[] { "prod/eu", "Production.", ".Production", "CON", "com1", new string('a', 81), "" })
        {
            var result = command.Parse($"secret {arguments} --environment \"{environment}\"");
            Assert.Contains(result.Errors, error => error.Message == Aspire.Cli.Resources.SecretCommandStrings.InvalidEnvironmentName);
        }

        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "secrets")));
    }

    [Theory]
    [InlineData("Production", "production")]
    [InlineData("production", "PRODUCTION")]
    [InlineData("prod-eu", "PROD-EU")]
    public async Task SecretCommands_CaseInsensitiveCrudPreservesEnvironmentSpelling(string originalEnvironment, string alternateEnvironment)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        var userSecretsId = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        using var provider = CreateSecretTestServices(workspace, outputWriter, appHostFile, userSecretsId);
        var command = provider.GetRequiredService<RootCommand>();
        var projectOption = $"--apphost \"{appHostFile.FullName}\"";

        Assert.Equal(CliExitCodes.Success, await command.Parse($"secret set Parameters:key initial -e {originalEnvironment} {projectOption}").InvokeAsync().DefaultTimeout());
        Assert.Equal(CliExitCodes.Success, await command.Parse($"secret set Parameters:key updated -e {alternateEnvironment} {projectOption}").InvokeAsync().DefaultTimeout());

        outputWriter.Logs.Clear();
        Assert.Equal(CliExitCodes.Success, await command.Parse($"secret get Parameters:key -e {alternateEnvironment} {projectOption}").InvokeAsync().DefaultTimeout());
        Assert.Contains("updated", outputWriter.Logs);

        outputWriter.Logs.Clear();
        Assert.Equal(CliExitCodes.Success, await command.Parse($"secret list --all --format json {projectOption}").InvokeAsync().DefaultTimeout());
        Assert.Contains(outputWriter.Logs, line => line.Contains($"\"{originalEnvironment}\"", StringComparison.Ordinal));
        var directory = AspireSecretsStoreResolver.GetSecretsDirectoryPath(workspace.WorkspaceRoot, userSecretsId);
        Assert.Equal($"{originalEnvironment}.json", Path.GetFileName(Assert.Single(Directory.EnumerateFiles(directory))));

        outputWriter.Logs.Clear();
        Assert.Equal(CliExitCodes.Success, await command.Parse($"secret path -e {alternateEnvironment} {projectOption}").InvokeAsync().DefaultTimeout());
        Assert.Contains(Path.Combine(directory, $"{originalEnvironment}.json"), outputWriter.Logs);

        Assert.Equal(CliExitCodes.Success, await command.Parse($"secret delete Parameters:key -e {alternateEnvironment} {projectOption}").InvokeAsync().DefaultTimeout());
        Assert.Equal(CliExitCodes.ConfigNotFound, await command.Parse($"secret get Parameters:key -e {originalEnvironment} {projectOption}").InvokeAsync().DefaultTimeout());
    }

    [Theory]
    [InlineData("set Parameters:new new-value", "Parameters:new", "new-value")]
    [InlineData("set parameters:TARGET updated-value", "Parameters:Target", "updated-value")]
    [InlineData("delete parameters:TARGET", "Parameters:Target", null)]
    public async Task SecretEditsPreserveUnrelatedDeploymentStateTypes(string arguments, string key, string? expectedValue)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        var userSecretsId = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        var path = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, "Production");
        SecretsStore.SaveJson(path, JsonNode.Parse("""
            {
              "Resource": {
                "Count": 42,
                "Enabled": true,
                "Ratio": 1.25,
                "Missing": null,
                "Text": "value"
              },
              "Parameters:Target": "original"
            }
            """)!.AsObject());
        using var provider = CreateSecretTestServices(workspace, outputWriter, appHostFile, userSecretsId);
        var command = provider.GetRequiredService<RootCommand>();

        var exitCode = await command.Parse($"secret {arguments} -e Production --apphost \"{appHostFile.FullName}\"")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.Equal(42, persisted["Resource:Count"]!.GetValue<int>());
        Assert.True(persisted["Resource:Enabled"]!.GetValue<bool>());
        Assert.Equal(1.25, persisted["Resource:Ratio"]!.GetValue<double>());
        Assert.True(persisted.ContainsKey("Resource:Missing"));
        Assert.Null(persisted["Resource:Missing"]);
        Assert.Equal("value", persisted["Resource:Text"]!.GetValue<string>());

        var store = new SecretsStore(path);
        Assert.Equal(expectedValue, store.Get(key));
        Assert.Equal(expectedValue is not null, store.ContainsKey(key));
    }

    private ServiceProvider CreateSecretTestServices(
        TemporaryWorkspace workspace,
        TestOutputTextWriter outputWriter,
        FileInfo appHostFile,
        string userSecretsId,
        IEnvironment? environment = null,
        Action<CliServiceCollectionTestOptions>? configure = null)
        => CreateSecretTestServices(workspace, outputWriter, appHostFile, new TestAppHostProject(userSecretsId), environment, configure);

    private ServiceProvider CreateSecretTestServices(
        TemporaryWorkspace workspace,
        TestOutputTextWriter outputWriter,
        FileInfo appHostFile,
        IAppHostProject project,
        IEnvironment? environment = null,
        Action<CliServiceCollectionTestOptions>? configure = null)
    {
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(
                workspace.WorkspaceRoot, homeDirectory: workspace.WorkspaceRoot);
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
            options.ProjectLocatorFactory = _ => new TestProjectLocator(appHostFile);
            configure?.Invoke(options);
        });

        if (environment is not null)
        {
            services.Replace(ServiceDescriptor.Singleton(environment));
        }
        services.Replace(ServiceDescriptor.Singleton<IAppHostProjectFactory>(
            new TestAppHostProjectFactory(project)));

        return services.BuildServiceProvider();
    }

    private sealed class TestProjectLocator(FileInfo appHostFile) : IProjectLocator
    {
        public int ResolveCallCount { get; private set; }
        public FileInfo? RequestedProjectFile { get; private set; }

        public Task<List<AppHostProjectCandidate>> FindAppHostProjectsAsync(DirectoryInfo searchDirectory, AppHostDiscoveryScope scope, CancellationToken cancellationToken)
            => Task.FromResult<List<AppHostProjectCandidate>>([new(appHostFile, "test")]);

        public Task<List<FileInfo>> FindAppHostProjectFilesAsync(DirectoryInfo searchDirectory, AppHostDiscoveryScope scope, CancellationToken cancellationToken)
            => Task.FromResult<List<FileInfo>>([appHostFile]);

        public Task<FileInfo?> GetAppHostFromSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<FileInfo?>(appHostFile);

        public Task<FileInfo?> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, bool createSettingsFile, CancellationToken cancellationToken)
            => Task.FromResult<FileInfo?>(projectFile ?? appHostFile);

        public Task<AppHostProjectSearchResult> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, MultipleAppHostProjectsFoundBehavior multipleAppHostProjectsFoundBehavior, bool createSettingsFile, CancellationToken cancellationToken = default)
        {
            ResolveCallCount++;
            RequestedProjectFile = projectFile;
            return Task.FromResult(new AppHostProjectSearchResult(projectFile ?? appHostFile, [projectFile ?? appHostFile]));
        }
    }

    private sealed class TestAppHostProjectFactory(IAppHostProject project) : IAppHostProjectFactory
    {
        public IAppHostProject GetProject(LanguageInfo language) => project;

        public IAppHostProject? TryGetProject(FileInfo appHostFile) => project;

        public IAppHostProject GetProject(FileInfo appHostFile) => project;
    }

    private sealed class TestAppHostProject(string userSecretsId, bool initializeOnDemand = false) : IAppHostProject
    {
        public int GetUserSecretsIdCallCount { get; private set; }
        public bool AutoInit { get; private set; }

        public bool IsInitialized { get; private set; } = !initializeOnDemand;
        public bool IsUnsupported { get; set; }
        public string LanguageId => "test";
        public string DisplayName => "Test";
        public bool SupportsLaunchProfiles => false;
        public string? AppHostFileName => null;

        public Task<bool> AddPackageAsync(AddPackageContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public bool CanHandle(FileInfo appHostFile) => true;
        public Task<RunningInstanceResult> FindAndStopRunningInstanceAsync(FileInfo appHostFile, DirectoryInfo homeDirectory, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string[]> GetDetectionPatternsAsync(CancellationToken cancellationToken = default) => Task.FromResult<string[]>([]);
        public Task<string?> GetUserSecretsIdAsync(FileInfo appHostFile, bool autoInit, CancellationToken cancellationToken)
        {
            GetUserSecretsIdCallCount++;
            AutoInit = autoInit;
            IsInitialized |= autoInit;
            return Task.FromResult(IsInitialized ? userSecretsId : null);
        }
        public bool IsUsingProjectReferences(FileInfo appHostFile) => false;
        public Task<int> PublishAsync(PublishContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> RunAsync(AppHostProjectContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UpdatePackagesResult> UpdatePackagesAsync(UpdatePackagesContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AppHostValidationResult> ValidateAppHostAsync(FileInfo appHostFile, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> GetAspireHostingVersionAsync(FileInfo appHostFile, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
