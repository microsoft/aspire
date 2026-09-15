// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
    [Fact]
    public async Task SecretPathCommand_PrintsSecretsPath_ForDotNetAppHost()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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

    private ServiceProvider CreateSecretTestServices(
        TemporaryWorkspace workspace,
        TestOutputTextWriter outputWriter,
        FileInfo appHostFile,
        string userSecretsId)
        => CreateSecretTestServices(workspace, outputWriter, appHostFile, new TestAppHostProject(userSecretsId));

    private ServiceProvider CreateSecretTestServices(
        TemporaryWorkspace workspace,
        TestOutputTextWriter outputWriter,
        FileInfo appHostFile,
        IAppHostProject project)
    {
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
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
