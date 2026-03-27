// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Projects;
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
        var expectedPath = UserSecretsPathHelper.GetSecretsPathFromSecretsId(userSecretsId);

        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");

        var command = CreateRootCommand(
            workspace,
            outputWriter,
            appHostFile,
            userSecretsId);

        var result = command.Parse($"secret path --apphost \"{appHostFile.FullName}\"");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Contains(expectedPath, outputWriter.Logs);
    }

    [Fact]
    public async Task SecretPathCommand_PrintsSecretsPath_ForGuestAppHost()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"));
        var userSecretsId = UserSecretsPathHelper.ComputeSyntheticUserSecretsId(appHostFile.FullName);
        var expectedPath = UserSecretsPathHelper.GetSecretsPathFromSecretsId(userSecretsId);

        await File.WriteAllTextAsync(appHostFile.FullName, "export {};");

        var command = CreateRootCommand(
            workspace,
            outputWriter,
            appHostFile,
            userSecretsId);

        var result = command.Parse($"secret path --apphost \"{appHostFile.FullName}\"");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Contains(expectedPath, outputWriter.Logs);
    }

    private RootCommand CreateRootCommand(
        TemporaryWorkspace workspace,
        TestOutputTextWriter outputWriter,
        FileInfo appHostFile,
        string userSecretsId)
    {
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
            options.ProjectLocatorFactory = _ => new TestProjectLocator(appHostFile);
        });

        services.Replace(ServiceDescriptor.Singleton<IAppHostProjectFactory>(
            new TestAppHostProjectFactory(new TestAppHostProject(userSecretsId))));

        return services.BuildServiceProvider().GetRequiredService<RootCommand>();
    }

    private sealed class TestProjectLocator(FileInfo appHostFile) : IProjectLocator
    {
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

    private sealed class TestAppHostProject(string userSecretsId) : IAppHostProject
    {
        public bool IsUnsupported { get; set; }
        public string LanguageId => "test";
        public string DisplayName => "Test";
        public string? AppHostFileName => null;

        public Task<bool> AddPackageAsync(AddPackageContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public bool CanHandle(FileInfo appHostFile) => true;
        public Task<RunningInstanceResult> FindAndStopRunningInstanceAsync(FileInfo appHostFile, DirectoryInfo homeDirectory, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string[]> GetDetectionPatternsAsync(CancellationToken cancellationToken = default) => Task.FromResult<string[]>([]);
        public Task<string?> GetUserSecretsIdAsync(FileInfo appHostFile, bool autoInit, CancellationToken cancellationToken) => Task.FromResult<string?>(userSecretsId);
        public bool IsUsingProjectReferences(FileInfo appHostFile) => false;
        public Task<int> PublishAsync(PublishContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> RunAsync(AppHostProjectContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UpdatePackagesResult> UpdatePackagesAsync(UpdatePackagesContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AppHostValidationResult> ValidateAppHostAsync(FileInfo appHostFile, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
