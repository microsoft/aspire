// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.Cli.Projects;
using Aspire.Cli.Secrets;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared.UserSecrets;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Secrets;

public class SecretStoreResolverTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ResolveAppHostIdAsync_DoesNotInitializeAndMigratesAfterExternalInitialization()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        var userSecretsId = Guid.NewGuid().ToString("N");
        using var provider = CreateDotNetServices(workspace, userSecretsId);
        var resolver = provider.GetRequiredService<AspireSecretsStoreResolver>();
        var project = provider.GetRequiredService<DotNetAppHostProject>();
        var syntheticId = AspireSecretsPathHelper.ComputeSyntheticAppHostId(appHostFile.FullName);

        Assert.Equal(syntheticId, await resolver.ResolveAppHostIdAsync(appHostFile, project, CancellationToken.None).DefaultTimeout());
        Assert.Null(XDocument.Load(appHostFile.FullName).Descendants("UserSecretsId").SingleOrDefault());
        Assert.False(Directory.Exists(AspireSecretsStoreResolver.GetSecretsDirectoryPath(workspace.WorkspaceRoot, syntheticId)));

        var source = new SecretsStore(AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, syntheticId, "Production"));
        source.Set("Parameters:key", "production");
        source.Save();
        Assert.Equal(userSecretsId, await project.GetUserSecretsIdAsync(appHostFile, autoInit: true, CancellationToken.None).DefaultTimeout());

        Assert.Equal(userSecretsId, await resolver.ResolveAppHostIdAsync(appHostFile, project, CancellationToken.None).DefaultTimeout());
        Assert.False(File.Exists(source.FilePath));
        var destination = new SecretsStore(AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, "Production"));
        Assert.Equal("production", destination.Get("Parameters:key"));
        Assert.False(File.Exists(AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, "Development")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResolveAsync_DotNetInitializationMigratesAllSyntheticEnvironments(bool initializeDuringResolve)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        var userSecretsId = Guid.NewGuid().ToString("N");
        using var provider = CreateDotNetServices(workspace, userSecretsId);
        var resolver = provider.GetRequiredService<AspireSecretsStoreResolver>();
        var project = provider.GetRequiredService<DotNetAppHostProject>();
        string[] environments = ["Development", "Production", "prod/eu"];
        var sourcePaths = new List<string>();

        foreach (var environment in environments)
        {
            // Prompted deployments can persist values through a resolver that does not initialize
            // user secrets, including Development values before the first `secret set`.
            var result = await resolver.ResolveAsync(appHostFile, project, environment, CancellationToken.None).DefaultTimeout();
            Assert.NotNull(result);
            result.AspireStore.Set("Parameters:key", environment);
            result.AspireStore.Save();
            sourcePaths.Add(result.AspireSecretsFilePath);
        }

        var beforeInitialization = await resolver.ResolveAllExistingAsync(appHostFile, project, CancellationToken.None).DefaultTimeout();
        Assert.Equal(environments.Length, beforeInitialization.Count);
        Assert.Null(XDocument.Load(appHostFile.FullName).Descendants("UserSecretsId").SingleOrDefault());

        if (!initializeDuringResolve)
        {
            Assert.Equal(userSecretsId, await project.GetUserSecretsIdAsync(appHostFile, autoInit: true, CancellationToken.None).DefaultTimeout());
        }

        var development = await resolver.ResolveAsync(
            appHostFile, project, "Development", autoInitDevelopmentUserSecrets: initializeDuringResolve, CancellationToken.None).DefaultTimeout();
        Assert.NotNull(development);
        Assert.Equal(UserSecretsPathHelper.GetSecretsPathFromSecretsId(userSecretsId), development.LegacyUserSecretsFilePath);
        Assert.Equal(userSecretsId, XDocument.Load(appHostFile.FullName).Descendants("UserSecretsId").Single().Value);

        foreach (var environment in environments)
        {
            var path = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, environment);
            Assert.Equal(environment, new SecretsStore(path).Get("Parameters:key"));
        }

        Assert.All(sourcePaths, path => Assert.False(File.Exists(path)));
        Assert.False(File.Exists(development.LegacyUserSecretsFilePath));
    }

    [Fact]
    public async Task ResolveAllExistingAsync_MigratesSyntheticSecretsOnceAndPreservesCanonicalValues()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        var userSecretsId = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(appHostFile.FullName, $"<Project><PropertyGroup><UserSecretsId>{userSecretsId}</UserSecretsId></PropertyGroup></Project>");
        var originalProject = await File.ReadAllTextAsync(appHostFile.FullName);
        using var provider = CreateDotNetServices(workspace, userSecretsId);
        var resolver = provider.GetRequiredService<AspireSecretsStoreResolver>();
        var project = provider.GetRequiredService<DotNetAppHostProject>();
        var sourcePath = AspireSecretsStoreResolver.GetSecretsFilePath(
            workspace.WorkspaceRoot, AspireSecretsPathHelper.ComputeSyntheticAppHostId(appHostFile.FullName), "Production");
        var destinationPath = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, "Production");
        var source = new SecretsStore(sourcePath);
        source.Set("Parameters:collision", "old");
        source.Set("Parameters:imported", "imported");
        source.Save();
        var destination = new SecretsStore(destinationPath);
        destination.Set("parameters:COLLISION", "canonical");
        destination.Set("Parameters:existing", "existing");
        destination.Save();

        var results = await resolver.ResolveAllExistingAsync(appHostFile, project, CancellationToken.None).DefaultTimeout();
        var production = Assert.Single(results);
        Assert.Equal(destinationPath, production.AspireSecretsFilePath);
        Assert.Equal("canonical", production.AspireStore.Get("Parameters:collision"));
        Assert.Equal("imported", production.AspireStore.Get("Parameters:imported"));
        Assert.Equal("existing", production.AspireStore.Get("Parameters:existing"));
        Assert.False(File.Exists(sourcePath));

        production.AspireStore.Clear();
        production.AspireStore.Save();
        results = await resolver.ResolveAllExistingAsync(appHostFile, project, CancellationToken.None).DefaultTimeout();
        Assert.Equal(0, Assert.Single(results).AspireStore.Count);
        Assert.Equal(originalProject, await File.ReadAllTextAsync(appHostFile.FullName));
    }

    [Fact]
    public async Task ResolveAsync_PartialMigrationFailurePreservesUnmigratedFilesAndCanRetry()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        var userSecretsId = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(appHostFile.FullName, $"<Project><PropertyGroup><UserSecretsId>{userSecretsId}</UserSecretsId></PropertyGroup></Project>");
        using var provider = CreateDotNetServices(workspace, userSecretsId);
        var resolver = provider.GetRequiredService<AspireSecretsStoreResolver>();
        var project = provider.GetRequiredService<DotNetAppHostProject>();
        var syntheticId = AspireSecretsPathHelper.ComputeSyntheticAppHostId(appHostFile.FullName);
        var firstSourcePath = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, syntheticId, "A");
        var secondSourcePath = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, syntheticId, "B");
        foreach (var path in new[] { firstSourcePath, secondSourcePath })
        {
            var source = new SecretsStore(path);
            source.Set("Parameters:key", "value");
            source.Save();
        }

        var blockedPath = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, "B");
        Directory.CreateDirectory(blockedPath);

        var exception = await Record.ExceptionAsync(() => resolver.ResolveAsync(
            appHostFile, project, "A", CancellationToken.None).DefaultTimeout());
        Assert.True(exception is IOException or UnauthorizedAccessException, exception?.ToString());
        Assert.False(File.Exists(firstSourcePath));
        Assert.Equal("value", new SecretsStore(secondSourcePath).Get("Parameters:key"));
        var firstDestination = new SecretsStore(AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, "A"));
        Assert.Equal("value", firstDestination.Get("Parameters:key"));
        firstDestination.Clear();
        firstDestination.Save();

        Directory.Delete(blockedPath);
        var results = await resolver.ResolveAllExistingAsync(appHostFile, project, CancellationToken.None).DefaultTimeout();
        Assert.Equal(0, Assert.Single(results, result => result.EnvironmentName == "A").AspireStore.Count);
        Assert.Equal("value", Assert.Single(results, result => result.EnvironmentName == "B").AspireStore.Get("Parameters:key"));
        Assert.False(File.Exists(secondSourcePath));
    }

    [Fact]
    public void ComputeSyntheticUserSecretsId_IsDeterministic()
    {
        var path = "/home/user/projects/myapp/apphost.ts";
        var id1 = UserSecretsPathHelper.ComputeSyntheticUserSecretsId(path);
        var id2 = UserSecretsPathHelper.ComputeSyntheticUserSecretsId(path);

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void ComputeSyntheticUserSecretsId_DifferentPaths_ProduceDifferentIds()
    {
        var id1 = UserSecretsPathHelper.ComputeSyntheticUserSecretsId("/home/user/project1/apphost.ts");
        var id2 = UserSecretsPathHelper.ComputeSyntheticUserSecretsId("/home/user/project2/apphost.ts");

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void ComputeSyntheticUserSecretsId_StartsWithAspirePrefix()
    {
        var id = UserSecretsPathHelper.ComputeSyntheticUserSecretsId("/some/path/apphost.ts");

        Assert.StartsWith("aspire-", id, StringComparison.Ordinal);
    }

    [Fact]
    public void ComputeSyntheticUserSecretsId_IsCaseInsensitive()
    {
        var id1 = UserSecretsPathHelper.ComputeSyntheticUserSecretsId("/Home/User/Project/apphost.ts");
        var id2 = UserSecretsPathHelper.ComputeSyntheticUserSecretsId("/home/user/project/apphost.ts");

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void GetSecretsFilePath_UsesReadableEnvironmentFileNames()
    {
        var homeDirectory = new DirectoryInfo("/home/user");

        var path = AspireSecretsStoreResolver.GetSecretsFilePath(homeDirectory, "apphost-id", "Production");

        Assert.Equal(Path.Combine(homeDirectory.FullName, ".aspire", "secrets", "apphost-id", "Production.json"), path);
    }

    [Fact]
    public void GetSecretsFilePath_SanitizesUnsafeEnvironmentFileNames()
    {
        var homeDirectory = new DirectoryInfo("/home/user");

        var path = AspireSecretsStoreResolver.GetSecretsFilePath(homeDirectory, "apphost-id", "prod/eu");

        Assert.StartsWith(Path.Combine(homeDirectory.FullName, ".aspire", "secrets", "apphost-id", "prod_eu-"), path, StringComparison.Ordinal);
        Assert.EndsWith(".json", path, StringComparison.Ordinal);
    }

    private ServiceProvider CreateDotNetServices(TemporaryWorkspace workspace, string userSecretsId)
    {
        // Exercise the real .NET project, runner, and metadata cache. Only process execution is
        // simulated: user-secrets init edits the project and MSBuild reports the resulting ID.
        var executionFactory = new TestProcessExecutionFactory
        {
            CreateExecutionCallback = (args, env, directory, options) => new TestProcessExecution(
                "dotnet", args, env, options,
                (_, _, _) =>
                {
                    var projectPath = Path.Combine(directory.FullName, "AppHost.csproj");
                    var document = XDocument.Load(projectPath);
                    if (args[0] == "user-secrets")
                    {
                        Assert.Equal("init", args[1]);
                        var previousWriteTime = File.GetLastWriteTimeUtc(projectPath);
                        document.Root!.Add(new XElement("PropertyGroup", new XElement("UserSecretsId", userSecretsId)));
                        document.Save(projectPath);
                        File.SetLastWriteTimeUtc(projectPath, previousWriteTime.AddSeconds(1));
                        return Task.FromResult((0, (string?)null));
                    }

                    Assert.Equal("msbuild", args[0]);
                    var id = document.Descendants("UserSecretsId").SingleOrDefault()?.Value;
                    return Task.FromResult((0, (string?)$$"""
                        {
                          "Properties": {
                            "UserSecretsId": "{{id}}"
                          },
                          "Items": {}
                        }
                        """));
                },
                () => 1)
        };
        return CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetCliExecutionFactoryFactory = _ => executionFactory;
        }).BuildServiceProvider();
    }
}
