// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Commands;
using Aspire.Cli.Projects;
using Aspire.Cli.Secrets;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared.UserSecrets;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Cli.Tests.Commands;

public class SecretCommandTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("get ConnectionStrings:db", "Development")]
    [InlineData("set Parameters:other synthetic", "Development")]
    [InlineData("list --format json", "Development")]
    [InlineData("list --all --format json", "Development")]
    [InlineData("delete ConnectionStrings:db", "Development")]
    [InlineData("get ConnectionStrings:db", "Production")]
    [InlineData("set Parameters:other synthetic", "Production")]
    [InlineData("list --format json", "Production")]
    [InlineData("list --all --format json", "Production")]
    [InlineData("delete ConnectionStrings:db", "Production")]
    [InlineData("get ConnectionStrings:db", "legacy")]
    [InlineData("set Parameters:other synthetic", "legacy")]
    [InlineData("list --format json", "legacy")]
    [InlineData("list --all --format json", "legacy")]
    [InlineData("delete ConnectionStrings:db", "legacy")]
    [InlineData("get ConnectionStrings:db", "synthetic")]
    [InlineData("set Parameters:other synthetic", "synthetic")]
    [InlineData("list --format json", "synthetic")]
    [InlineData("list --all --format json", "synthetic")]
    [InlineData("delete ConnectionStrings:db", "synthetic")]
    public async Task SecretCommands_InvalidJsonReportsFileAndPreservesContent(string arguments, string location)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        using var errorWriter = new StringWriter();
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        var userSecretsId = Guid.NewGuid().ToString("N");
        var environment = location is "Production" ? "Production" : "Development";
        var aspirePath = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, environment);
        var path = location switch
        {
            "legacy" => UserSecretsPathHelper.GetSecretsPathFromSecretsId(userSecretsId),
            "synthetic" => AspireSecretsStoreResolver.GetSecretsFilePath(
                workspace.WorkspaceRoot, AspireSecretsPathHelper.ComputeSyntheticAppHostId(appHostFile.FullName), environment),
            _ => aspirePath
        };
        const string malformedJson = """{"ConnectionStrings:db":"Data Source=0.0.0.0\sql;Database=Synthetic"}""";
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, malformedJson);

        try
        {
            using var provider = CreateSecretTestServices(workspace, outputWriter, appHostFile, new TestAppHostProject(userSecretsId), errorWriter);
            var command = provider.GetRequiredService<RootCommand>();

            var environmentOption = arguments.Contains("--all", StringComparison.Ordinal) ? "" : $"-e {environment}";
            var exitCode = await command.Parse($"secret {arguments} {environmentOption} --apphost \"{appHostFile.FullName}\"")
                .InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
            var error = errorWriter.ToString();
            Assert.Contains(path, error);
            Assert.Contains("invalid JSON (line 1, byte position 46)", error);
            Assert.Contains("Fix the JSON", error);
            Assert.Contains(@"server\\sql", error);
            Assert.Empty(outputWriter.Logs);
            Assert.Equal(malformedJson, await File.ReadAllTextAsync(path));
            Assert.Equal(location is "Development" or "Production", File.Exists(aspirePath));
        }
        finally
        {
            if (location is "legacy")
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task SecretDeleteCommand_MalformedLegacyWithCanonicalStorePreservesBothFiles()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        using var errorWriter = new StringWriter();
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        var userSecretsId = Guid.NewGuid().ToString("N");
        var aspirePath = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, "Development");
        var legacyPath = UserSecretsPathHelper.GetSecretsPathFromSecretsId(userSecretsId);
        const string canonicalJson = """{"ConnectionStrings:db":"Data Source=0.0.0.0\\sql;Database=Synthetic"}""";
        const string malformedJson = """{"ConnectionStrings:db":"Data Source=0.0.0.0\sql;Database=Synthetic"}""";
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        Directory.CreateDirectory(Path.GetDirectoryName(aspirePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        await File.WriteAllTextAsync(aspirePath, canonicalJson);
        await File.WriteAllTextAsync(legacyPath, malformedJson);

        try
        {
            using var provider = CreateSecretTestServices(workspace, outputWriter, appHostFile, new TestAppHostProject(userSecretsId), errorWriter);
            var command = provider.GetRequiredService<RootCommand>();

            var exitCode = await command.Parse($"secret delete ConnectionStrings:db --apphost \"{appHostFile.FullName}\"")
                .InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
            var error = errorWriter.ToString();
            Assert.Contains(legacyPath, error);
            Assert.Contains("invalid JSON (line 1, byte position 46)", error);
            Assert.Contains("Fix the JSON", error);
            Assert.Empty(outputWriter.Logs);
            Assert.Equal(canonicalJson, await File.ReadAllTextAsync(aspirePath));
            Assert.Equal(malformedJson, await File.ReadAllTextAsync(legacyPath));
        }
        finally
        {
            File.Delete(legacyPath);
        }
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task SecretPathCommand_DoesNotLoadImportOrMigrateSecrets(string environment)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        var userSecretsId = Guid.NewGuid().ToString("N");
        var expectedPath = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, environment);
        var legacyPath = UserSecretsPathHelper.GetSecretsPathFromSecretsId(userSecretsId);
        var syntheticPath = AspireSecretsStoreResolver.GetSecretsFilePath(
            workspace.WorkspaceRoot, AspireSecretsPathHelper.ComputeSyntheticAppHostId(appHostFile.FullName), environment);
        const string malformedJson = """{"ConnectionStrings:db":"Data Source=0.0.0.0\sql"}""";
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        foreach (var path in new[] { legacyPath, syntheticPath })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, malformedJson);
        }

        try
        {
            using var provider = CreateSecretTestServices(workspace, outputWriter, appHostFile, userSecretsId);
            var command = provider.GetRequiredService<RootCommand>();
            var arguments = $"secret path -e {environment} --apphost \"{appHostFile.FullName}\"";

            Assert.Equal(CliExitCodes.Success, await command.Parse(arguments).InvokeAsync().DefaultTimeout());
            Assert.Contains(expectedPath, outputWriter.Logs);
            Assert.False(File.Exists(expectedPath));

            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            await File.WriteAllTextAsync(expectedPath, malformedJson);
            outputWriter.Logs.Clear();

            Assert.Equal(CliExitCodes.Success, await command.Parse(arguments).InvokeAsync().DefaultTimeout());
            Assert.Contains(expectedPath, outputWriter.Logs);
            foreach (var path in new[] { legacyPath, syntheticPath, expectedPath })
            {
                Assert.Equal(malformedJson, await File.ReadAllTextAsync(path));
            }
        }
        finally
        {
            File.Delete(legacyPath);
        }
    }

    [Fact]
    public async Task SecretPathCommand_DoesNotInitializeUserSecrets()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        var project = new TestAppHostProject(Guid.NewGuid().ToString("N"), initializeOnDemand: true);
        var expectedPath = AspireSecretsStoreResolver.GetSecretsFilePath(
            workspace.WorkspaceRoot, AspireSecretsPathHelper.ComputeSyntheticAppHostId(appHostFile.FullName), "Development");
        using var provider = CreateSecretTestServices(workspace, outputWriter, appHostFile, project, errorWriter: null);
        var command = provider.GetRequiredService<RootCommand>();

        Assert.Equal(CliExitCodes.Success, await command.Parse($"secret path --apphost \"{appHostFile.FullName}\"").InvokeAsync().DefaultTimeout());
        Assert.Contains(expectedPath, outputWriter.Logs);
        Assert.False(project.IsInitialized);
        Assert.False(Directory.Exists(Path.GetDirectoryName(expectedPath)));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task SecretCommands_RoundTripEscapedBackslashes(string environment)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        var userSecretsId = Guid.NewGuid().ToString("N");
        var path = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, environment);
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """{"ConnectionStrings:db":"Data Source=0.0.0.0\\sql;Database=Synthetic"}""");
        using var provider = CreateSecretTestServices(workspace, outputWriter, appHostFile, userSecretsId);
        var command = provider.GetRequiredService<RootCommand>();
        var options = $"-e {environment} --apphost \"{appHostFile.FullName}\"";
        const string original = @"Data Source=0.0.0.0\sql;Database=Synthetic";
        const string updated = @"Data Source=0.0.0.0\sql;Database=Updated";

        Assert.Equal(CliExitCodes.Success, await command.Parse($"secret get ConnectionStrings:db {options}").InvokeAsync().DefaultTimeout());
        Assert.Contains(original, outputWriter.Logs);
        Assert.Equal(CliExitCodes.Success, await command.Parse($"secret set Parameters:other synthetic {options}").InvokeAsync().DefaultTimeout());
        Assert.Equal(original, new SecretsStore(path).Get("ConnectionStrings:db"));
        Assert.Equal(CliExitCodes.Success, await command.Parse(["secret", "set", "ConnectionStrings:db", updated, "-e", environment, "--apphost", appHostFile.FullName]).InvokeAsync().DefaultTimeout());
        Assert.Equal(updated, new SecretsStore(path).Get("ConnectionStrings:db"));
        outputWriter.Logs.Clear();
        Assert.Equal(CliExitCodes.Success, await command.Parse($"secret list --format json {options}").InvokeAsync().DefaultTimeout());
        var listed = JsonNode.Parse(string.Join(Environment.NewLine, outputWriter.Logs))!;
        Assert.Equal(updated, listed["ConnectionStrings:db"]!.GetValue<string>());
        Assert.Equal("synthetic", listed["Parameters:other"]!.GetValue<string>());
        Assert.Equal(CliExitCodes.Success, await command.Parse($"secret delete ConnectionStrings:db {options}").InvokeAsync().DefaultTimeout());
        Assert.Equal([KeyValuePair.Create("Parameters:other", "synthetic")], new SecretsStore(path).ToList());
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
        using var provider = CreateSecretTestServices(workspace, outputWriter, appHostFile, project, errorWriter: null);
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
        string userSecretsId)
        => CreateSecretTestServices(workspace, outputWriter, appHostFile, new TestAppHostProject(userSecretsId), errorWriter: null);

    private ServiceProvider CreateSecretTestServices(
        TemporaryWorkspace workspace,
        TestOutputTextWriter outputWriter,
        FileInfo appHostFile,
        IAppHostProject project,
        StringWriter? errorWriter)
    {
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(
                workspace.WorkspaceRoot, homeDirectory: workspace.WorkspaceRoot);
            options.OutputTextWriter = outputWriter;
            options.ErrorTextWriter = errorWriter;
            options.DisableAnsi = true;
            options.ProjectLocatorFactory = _ => new TestProjectLocator(appHostFile);
        });

        services.Replace(ServiceDescriptor.Singleton<IAppHostProjectFactory>(
            new TestAppHostProjectFactory(project)));

        return services.BuildServiceProvider();
    }

    private sealed class TestProjectLocator(FileInfo appHostFile) : IProjectLocator
    {
        public Task<List<AppHostProjectCandidate>> FindAppHostProjectsAsync(DirectoryInfo searchDirectory, AppHostDiscoveryScope scope, CancellationToken cancellationToken)
            => Task.FromResult<List<AppHostProjectCandidate>>([new(appHostFile, "test")]);

        public Task<List<FileInfo>> FindAppHostProjectFilesAsync(DirectoryInfo searchDirectory, AppHostDiscoveryScope scope, CancellationToken cancellationToken)
            => Task.FromResult<List<FileInfo>>([appHostFile]);

        public Task<FileInfo?> GetAppHostFromSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<FileInfo?>(appHostFile);

        public Task<FileInfo?> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, bool createSettingsFile, CancellationToken cancellationToken)
            => Task.FromResult<FileInfo?>(projectFile ?? appHostFile);

        public Task<AppHostProjectSearchResult> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, MultipleAppHostProjectsFoundBehavior multipleAppHostProjectsFoundBehavior, bool createSettingsFile, CancellationToken cancellationToken = default)
            => Task.FromResult(new AppHostProjectSearchResult(projectFile ?? appHostFile, [projectFile ?? appHostFile]));
    }

    private sealed class TestAppHostProjectFactory(IAppHostProject project) : IAppHostProjectFactory
    {
        public IAppHostProject GetProject(LanguageInfo language) => project;

        public IAppHostProject? TryGetProject(FileInfo appHostFile) => project;

        public IAppHostProject GetProject(FileInfo appHostFile) => project;
    }

    private sealed class TestAppHostProject(string userSecretsId, bool initializeOnDemand = false) : IAppHostProject
    {
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
