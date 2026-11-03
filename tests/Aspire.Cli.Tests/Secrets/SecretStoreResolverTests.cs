// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
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
        string[] environments = ["Development", "Production", "prod-eu"];
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
        var destinationPath = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, "production");
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
        Assert.Equal("production", production.EnvironmentName);
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
    public async Task ResolveAsync_MigrationPreservesTypedValuesAndCanonicalNulls()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        var userSecretsId = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(appHostFile.FullName, $"<Project><PropertyGroup><UserSecretsId>{userSecretsId}</UserSecretsId></PropertyGroup></Project>");
        var sourcePath = AspireSecretsStoreResolver.GetSecretsFilePath(
            workspace.WorkspaceRoot, AspireSecretsPathHelper.ComputeSyntheticAppHostId(appHostFile.FullName), "Production");
        var destinationPath = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, userSecretsId, "production");
        SecretsStore.SaveJson(sourcePath, JsonNode.Parse("""
            {
              "Resource": { "Count": 42, "Enabled": true, "Ratio": 1.25, "Missing": null },
              "canonical": { "count": 1, "enabled": true, "missing": "replace" }
            }
            """)!.AsObject());
        SecretsStore.SaveJson(destinationPath, JsonNode.Parse("""
            {
              "Canonical": { "Count": 7, "Enabled": false, "Missing": null }
            }
            """)!.AsObject());
        using var provider = CreateDotNetServices(workspace, userSecretsId);
        var resolver = provider.GetRequiredService<AspireSecretsStoreResolver>();
        var project = provider.GetRequiredService<DotNetAppHostProject>();

        var result = await resolver.ResolveAsync(appHostFile, project, "Production", CancellationToken.None).DefaultTimeout();

        Assert.NotNull(result);
        Assert.False(File.Exists(sourcePath));
        Assert.Equal(destinationPath, result.AspireSecretsFilePath);
        result.AspireStore.Save();
        result.AspireStore.Save();
        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(destinationPath))!.AsObject();
        Assert.Equal(42, persisted["Resource:Count"]!.GetValue<int>());
        Assert.True(persisted["Resource:Enabled"]!.GetValue<bool>());
        Assert.Equal(1.25, persisted["Resource:Ratio"]!.GetValue<double>());
        Assert.True(persisted.ContainsKey("Resource:Missing"));
        Assert.Null(persisted["Resource:Missing"]);
        Assert.Equal(7, persisted["Canonical:Count"]!.GetValue<int>());
        Assert.False(persisted["Canonical:Enabled"]!.GetValue<bool>());
        Assert.True(persisted.ContainsKey("Canonical:Missing"));
        Assert.Null(persisted["Canonical:Missing"]);

        await resolver.ResolveAsync(appHostFile, project, "Production", CancellationToken.None).DefaultTimeout();
        Assert.Null(JsonNode.Parse(await File.ReadAllTextAsync(destinationPath))!["Canonical:Missing"]);
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
    public void ComputeSyntheticAppHostId_RespectsPlatformPathCasing()
    {
        var upperPath = Path.Combine("work", "Foo", "AppHost.csproj");
        var lowerPath = Path.Combine("work", "foo", "AppHost.csproj");
        var upperId = AspireSecretsPathHelper.ComputeSyntheticAppHostId(upperPath);
        var lowerId = AspireSecretsPathHelper.ComputeSyntheticAppHostId(lowerPath);

        Assert.StartsWith("apphost-", upperId, StringComparison.Ordinal);
        Assert.Equal(upperId, AspireSecretsPathHelper.ComputeSyntheticAppHostId(upperPath));
        Assert.Equal(OperatingSystem.IsWindows(), upperId == lowerId);
    }

    [Fact]
    public void GetSecretsFilePath_UsesReadableEnvironmentFileNames()
    {
        var homeDirectory = new DirectoryInfo("/home/user");

        var path = AspireSecretsStoreResolver.GetSecretsFilePath(homeDirectory, "apphost-id", "Production");

        Assert.Equal(Path.Combine(homeDirectory.FullName, ".aspire", "secrets", "apphost-id", "Production.json"), path);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("prod/eu")]
    [InlineData("prod\\eu")]
    [InlineData(".Production")]
    [InlineData("Production.")]
    [InlineData("prod.eu")]
    [InlineData("-Production")]
    [InlineData("_Production")]
    [InlineData("prod eu")]
    [InlineData("pröd")]
    [InlineData("CON")]
    [InlineData("prn")]
    [InlineData("Aux")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("LPT1")]
    [InlineData("lpt9")]
    public void GetSecretsFilePath_RejectsInvalidEnvironmentNames(string environment)
    {
        var homeDirectory = new DirectoryInfo("/home/user");

        Assert.False(AspireSecretsPathHelper.IsValidEnvironmentName(environment));
        Assert.Throws<ArgumentException>(() => AspireSecretsStoreResolver.GetSecretsFilePath(homeDirectory, "apphost-id", environment));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("production")]
    [InlineData("prod-eu")]
    [InlineData("prod_eu")]
    [InlineData("1")]
    [InlineData("COM10")]
    [InlineData("LPT0")]
    public void GetSecretsFilePath_PreservesValidEnvironmentNames(string environment)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        Assert.True(AspireSecretsPathHelper.IsValidEnvironmentName(environment));
        Assert.Equal(
            Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "secrets", "apphost-id", $"{environment}.json"),
            AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, "apphost-id", environment));
    }

    [Fact]
    public void GetSecretsFilePath_EnforcesEnvironmentNameLengthLimit()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var environment = new string('a', 80);

        Assert.True(AspireSecretsPathHelper.IsValidEnvironmentName(environment));
        Assert.EndsWith($"{environment}.json", AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, "apphost-id", environment), StringComparison.Ordinal);
        Assert.False(AspireSecretsPathHelper.IsValidEnvironmentName(environment + "a"));
        Assert.Throws<ArgumentException>(() => AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, "apphost-id", environment + "a"));
    }

    [Fact]
    public void GetSecretsFilePath_ReusesExistingEnvironmentSpelling()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, "apphost-id", "Production");
        var store = new SecretsStore(path);
        store.Set("Parameters:key", "value");
        store.Save();

        Assert.Equal(path, AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, "apphost-id", "production"));
        Assert.Equal(path, AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, "apphost-id", "PRODUCTION"));
    }

    [Fact]
    public void GetSecretsFilePath_RejectsAmbiguousExistingEnvironmentFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var directory = AspireSecretsStoreResolver.GetSecretsDirectoryPath(workspace.WorkspaceRoot, "apphost-id");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Production.json"), "{}");
        File.WriteAllText(Path.Combine(directory, "production.json"), "{}");

        // Only case-sensitive filesystems can contain both files. On other filesystems
        // the second write replaces the same file and resolution must still succeed.
        if (Directory.EnumerateFiles(directory).Count() == 2)
        {
            Assert.Throws<InvalidOperationException>(() =>
                AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, "apphost-id", "PRODUCTION"));
        }
        else
        {
            Assert.True(File.Exists(AspireSecretsStoreResolver.GetSecretsFilePath(workspace.WorkspaceRoot, "apphost-id", "PRODUCTION")));
        }
    }

    [Fact]
    public void SafeAppHostSegments_PreserveIdentityWhenTrimmingDots()
    {
        Assert.NotEqual(
            AspireSecretsPathHelper.CreateSafePathSegment("apphost"),
            AspireSecretsPathHelper.CreateSafePathSegment(".apphost"));
        Assert.NotEqual(
            AspireSecretsPathHelper.CreateSafePathSegment("apphost"),
            AspireSecretsPathHelper.CreateSafePathSegment("apphost."));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("Con.txt")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("COM9.txt")]
    [InlineData("LPT1")]
    [InlineData("LPT9.ext")]
    public void SafeAppHostSegments_EscapeWindowsReservedNames(string appHostId)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var segment = AspireSecretsPathHelper.CreateSafePathSegment(appHostId);
        Assert.StartsWith("_", segment, StringComparison.Ordinal);
        Assert.NotEqual(AspireSecretsPathHelper.CreateSafePathSegment($"_{appHostId}"), segment);
        var path = AspireSecretsPathHelper.GetSecretsFilePath(workspace.WorkspaceRoot, appHostId, "Production");
        var store = new SecretsStore(path);
        store.Set("Parameters:key", "value");
        store.Save();

        Assert.Equal(segment, Path.GetFileName(Path.GetDirectoryName(path)));
        Assert.Equal("value", new SecretsStore(path).Get("Parameters:key"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavingSecretsPreservesExistingParentPermissions(bool saveState)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file permissions are not supported on Windows.");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var directory = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "shared"));
        var sharedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(directory.FullName, sharedMode);
        var path = Path.Combine(directory.FullName, "secrets.json");

        for (var i = 0; i < 2; i++)
        {
            if (saveState)
            {
                SecretsStore.SaveJson(path, new System.Text.Json.Nodes.JsonObject { ["Count"] = i });
            }
            else
            {
                var store = new SecretsStore(path);
                store.Set("Parameters:key", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                store.Save();
            }

            Assert.Equal(sharedMode, File.GetUnixFileMode(directory.FullName));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }

        Assert.Single(Directory.EnumerateFiles(directory.FullName));
    }

    [Fact]
    public void SavingSecretsPreservesExistingWindowsFilePermissions()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows file permissions are not supported on this platform.");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, "secrets.json");
        var store = new SecretsStore(path);
        store.Set("Parameters:key", "initial");
        store.Save();

        using var identity = WindowsIdentity.GetCurrent();
        Assert.NotNull(identity.User);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(identity.User, FileSystemRights.FullControl, AccessControlType.Allow));
        var file = new FileInfo(path);
        file.SetAccessControl(security);
        var originalPermissions = file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var parentPermissions = workspace.WorkspaceRoot.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        store.Set("Parameters:key", "updated");
        store.Save();
        Assert.Equal("updated", new SecretsStore(path).Get("Parameters:key"));
        Assert.Equal(originalPermissions, file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));

        SecretsStore.SaveJson(path, new JsonObject { ["Count"] = 42 });
        Assert.Equal(42, JsonNode.Parse(File.ReadAllText(path))!["Count"]!.GetValue<int>());
        Assert.Equal(originalPermissions, file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        Assert.Equal(parentPermissions, workspace.WorkspaceRoot.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        Assert.Empty(Directory.EnumerateFiles(workspace.WorkspaceRoot.FullName, ".secrets.json.*.tmp"));
    }

    [Fact]
    public void SavingSecretsCreatesPrivateDirectories()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file permissions are not supported on Windows.");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var directory = Path.Combine(workspace.WorkspaceRoot.FullName, "new", "nested");
        var path = Path.Combine(directory, "secrets.json");
        var store = new SecretsStore(path);
        store.Set("Parameters:key", "value");
        store.Save();

        var privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Assert.Equal(privateMode, File.GetUnixFileMode(directory));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
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
            options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(
                workspace.WorkspaceRoot, homeDirectory: workspace.WorkspaceRoot);
            options.DotNetCliExecutionFactoryFactory = _ => executionFactory;
        }).BuildServiceProvider();
    }
}
