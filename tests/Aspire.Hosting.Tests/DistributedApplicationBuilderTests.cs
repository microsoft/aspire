// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREUSERSECRETS001

#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES004 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Devcontainers;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Pipelines.Internal;
using Aspire.Shared.UserSecrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class DistributedApplicationBuilderTests
{
    private static readonly ConstructorInfo s_userSecretsIdAttrCtor = typeof(UserSecretsIdAttribute).GetConstructor([typeof(string)])!;

    [Theory]
    [InlineData(new string[0], DistributedApplicationOperation.Run)]
    [InlineData(new string[] { "--publisher", "manifest" }, DistributedApplicationOperation.Publish)]
    public void BuilderExecutionContextExposesCorrectOperation(string[] args, DistributedApplicationOperation operation)
    {
        var builder = DistributedApplication.CreateBuilder(args);
        Assert.Equal(operation, builder.ExecutionContext.Operation);
    }

    [Fact]
    public void BuilderAddsDefaultServices()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.Services.Configure<DcpOptions>(o =>
        {
            o.DashboardPath = "dashboard";
            o.CliPath = "dcp";
        });

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        Assert.Empty(appModel.Resources);

        var eventingSubscribers = app.Services.GetServices<IDistributedApplicationEventingSubscriber>();
        Assert.Collection(
            eventingSubscribers,
            s => Assert.IsType<DashboardEventHandlers>(s),
            s => Assert.IsType<DevcontainerPortForwardingEventingSubscriber>(s),
            s => Assert.IsType<RequiredCommandValidationEventingSubscriber>(s),
            s => Assert.IsType<TerminalHostEventingSubscriber>(s)
        );

        var options = app.Services.GetRequiredService<IOptions<PipelineOptions>>();
        Assert.Null(options.Value.OutputPath);
    }

    [Fact]
    public void BuilderAddsResourceToAddModel()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddResource(new TestResource());
        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(appModel.Resources);
        Assert.IsType<TestResource>(resource);
    }

    [Fact]
    public void BuilderConfiguresPublishingOptionsFromCommandLine()
    {
        var appBuilder = DistributedApplication.CreateBuilder(["--publisher", "manifest", "--output-path", "/tmp/"]);
        using var app = appBuilder.Build();

        var pipelineOptions = app.Services.GetRequiredService<IOptions<PipelineOptions>>();
        Assert.Equal("/tmp/", pipelineOptions.Value.OutputPath);
    }

    [Fact]
    public void BuilderConfiguresPublishingOptionsFromConfig()
    {
        var appBuilder = DistributedApplication.CreateBuilder(["--publisher", "manifest", "--output-path", "/tmp/"]);
        appBuilder.Configuration["Pipeline:OutputPath"] = "/path/";
        using var app = appBuilder.Build();

        var pipelineOptions = app.Services.GetRequiredService<IOptions<PipelineOptions>>();
        Assert.Equal("/path/", pipelineOptions.Value.OutputPath);
    }

    [Fact]
    public void AppHostDirectoryAvailableViaConfig()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var appHostDirectory = appBuilder.AppHostDirectory;
        using var app = appBuilder.Build();

        var config = app.Services.GetRequiredService<IConfiguration>();
        Assert.Equal(appHostDirectory, config["AppHost:Directory"]);
    }

    [Fact]
    public void PipelineOutputServiceUsesAppHostDirectoryByDefault()
    {
        var projectDirectory = OperatingSystem.IsWindows() ? @"C:\projects\Tailspin" : "/projects/Tailspin";
        var appBuilder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = projectDirectory
        });
        using var app = appBuilder.Build();

        var outputService = app.Services.GetRequiredService<IPipelineOutputService>();
        Assert.Equal(Path.Combine(projectDirectory, "aspire-output"), outputService.GetOutputDirectory());
    }

    [Fact]
    public void PipelineOutputServiceIgnoresInvalidAppHostDirectoryWhenOutputPathSpecified()
    {
        var outputPath = OperatingSystem.IsWindows() ? @"C:\tmp\output" : "/tmp/output";
        var appBuilder = DistributedApplication.CreateBuilder(["--publisher", "manifest", "--output-path", outputPath]);
        appBuilder.Configuration["AppHost:Directory"] = "\0";
        using var app = appBuilder.Build();

        var outputService = app.Services.GetRequiredService<IPipelineOutputService>();
        Assert.Equal(Path.GetFullPath(outputPath), outputService.GetOutputDirectory());
    }

    [Fact]
    public void PipelineOutputServiceFallsBackToCurrentDirectoryWhenAppHostDirectoryIsInvalid()
    {
        var appBuilder = DistributedApplication.CreateBuilder(["--publisher", "manifest"]);
        appBuilder.Configuration["AppHost:Directory"] = "\0";
        using var app = appBuilder.Build();

        var outputService = app.Services.GetRequiredService<IPipelineOutputService>();
        Assert.Equal(Path.Combine(Environment.CurrentDirectory, "aspire-output"), outputService.GetOutputDirectory());
    }

    [Fact]
    public void ResourceServiceConfig_Secured()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        using var app = appBuilder.Build();

        var config = app.Services.GetRequiredService<IConfiguration>();
        Assert.Equal(nameof(ResourceServiceAuthMode.ApiKey), config["AppHost:ResourceService:AuthMode"]);
        Assert.False(string.IsNullOrEmpty(config["AppHost:ResourceService:ApiKey"]));
    }

    [Fact]
    public void AspireLogLevelOverridesConfiguredDefaultLogLevel()
    {
        var appBuilder = DistributedApplication.CreateBuilder(args: [$"{KnownConfigNames.AspireLogLevel}=Trace"]);
        appBuilder.Configuration["Logging:LogLevel:Default"] = "Information";

        using var app = appBuilder.Build();

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AspireLogLevelTest");
        Assert.True(logger.IsEnabled(LogLevel.Trace));
    }

    [Fact]
    public void PolyglotAppHostUsesAspireUserSecretsIdForUserSecretsManager()
    {
        var userSecretsId = Guid.NewGuid().ToString("N");
        var userSecretsPath = UserSecretsPathHelper.GetSecretsPathFromSecretsId(userSecretsId);

        if (File.Exists(userSecretsPath))
        {
            File.Delete(userSecretsPath);
        }

        var appBuilder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [$"{KnownConfigNames.AspireUserSecretsId}={userSecretsId}"],
            DisableDashboard = true,
        });

        Assert.True(appBuilder.UserSecretsManager.IsAvailable);
        Assert.Equal(userSecretsPath, appBuilder.UserSecretsManager.FilePath);
    }

    [Fact]
    public void AspireUserSecretsIdOverridesAppHostAssemblyUserSecretsId()
    {
        var assemblyUserSecretsId = Guid.NewGuid().ToString("N");
        var configuredUserSecretsId = Guid.NewGuid().ToString("N");
        var assemblyUserSecretsPath = UserSecretsPathHelper.GetSecretsPathFromSecretsId(assemblyUserSecretsId);
        var configuredUserSecretsPath = UserSecretsPathHelper.GetSecretsPathFromSecretsId(configuredUserSecretsId);

        DeleteUserSecretsFile(assemblyUserSecretsPath);
        DeleteUserSecretsFile(configuredUserSecretsPath);

        File.WriteAllText(assemblyUserSecretsPath, """
            {
              "AssemblyOnly": "assembly-only",
              "Probe": "assembly"
            }
            """);

        File.WriteAllText(configuredUserSecretsPath, """
            {
              "ConfiguredOnly": "configured-only",
              "Probe": "configured"
            }
            """);

        var testAssembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"TestAssembly{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.RunAndCollect,
            [new CustomAttributeBuilder(s_userSecretsIdAttrCtor, [assemblyUserSecretsId])]);

        try
        {
            var configuration = new ConfigurationManager();
            configuration.AddUserSecrets(testAssembly);
            configuration[KnownConfigNames.AspireUserSecretsId] = configuredUserSecretsId;

            Assert.Equal(configuredUserSecretsId, DistributedApplicationBuilder.ResolveUserSecretsId(testAssembly, configuration));

            DistributedApplicationBuilder.AddConfiguredUserSecrets(configuration, testAssembly, configuredUserSecretsId, isDevelopment: true);

            Assert.Null(configuration["AssemblyOnly"]);
            Assert.Equal("configured-only", configuration["ConfiguredOnly"]);
            Assert.Equal("configured", configuration["Probe"]);
        }
        finally
        {
            DeleteUserSecretsFile(assemblyUserSecretsPath);
            DeleteUserSecretsFile(configuredUserSecretsPath);
        }
    }

    [Fact]
    public void EmptyAspireUserSecretsIdFallsBackToAppHostAssemblyUserSecretsId()
    {
        var assemblyUserSecretsId = Guid.NewGuid().ToString("N");
        var testAssembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"TestAssembly{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.RunAndCollect,
            [new CustomAttributeBuilder(s_userSecretsIdAttrCtor, [assemblyUserSecretsId])]);

        var configuration = new ConfigurationManager();
        configuration[KnownConfigNames.AspireUserSecretsId] = "";

        Assert.Equal(assemblyUserSecretsId, DistributedApplicationBuilder.ResolveUserSecretsId(testAssembly, configuration));
    }

    [Theory]
    [InlineData(KnownConfigNames.DashboardUnsecuredAllowAnonymous)]
    [InlineData(KnownConfigNames.Legacy.DashboardUnsecuredAllowAnonymous)]
    public void ResourceServiceConfig_Unsecured(string dashboardUnsecuredAllowAnonymousKey)
    {
        var appBuilder = DistributedApplication.CreateBuilder(args: [$"{dashboardUnsecuredAllowAnonymousKey}=true"]);
        using var app = appBuilder.Build();

        var config = app.Services.GetRequiredService<IConfiguration>();
        Assert.Equal(nameof(ResourceServiceAuthMode.Unsecured), config["AppHost:ResourceService:AuthMode"]);
        Assert.True(string.IsNullOrEmpty(config["AppHost:ResourceService:ApiKey"]));
    }

    private static void DeleteUserSecretsFile(string userSecretsPath)
    {
        if (File.Exists(userSecretsPath))
        {
            File.Delete(userSecretsPath);
        }

        var directory = Path.GetDirectoryName(userSecretsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    [Fact]
    public void AddResource_DuplicateResourceNames_SameCasing_Error()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddResource(new ContainerResource("Test"));

        var ex = Assert.Throws<DistributedApplicationException>(() => appBuilder.AddResource(new ContainerResource("Test")));
        Assert.Equal("Cannot add resource of type 'Aspire.Hosting.ApplicationModel.ContainerResource' with name 'Test' because resource of type 'Aspire.Hosting.ApplicationModel.ContainerResource' with that name already exists. Resource names are case-insensitive.", ex.Message);
    }

    [Fact]
    public void AddResource_DuplicateResourceNames_MixedCasing_Error()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddResource(new ContainerResource("Test"));

        var ex = Assert.Throws<DistributedApplicationException>(() => appBuilder.AddResource(new ContainerResource("TEST")));
        Assert.Equal("Cannot add resource of type 'Aspire.Hosting.ApplicationModel.ContainerResource' with name 'TEST' because resource of type 'Aspire.Hosting.ApplicationModel.ContainerResource' with that name already exists. Resource names are case-insensitive.", ex.Message);
    }

    [Fact]
    public void AppHostIdentitiesAreAvailable()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var pathSha = appBuilder.Configuration["AppHost:PathSha256"];
        var deploymentStatePathSha = appBuilder.Configuration["AppHost:DeploymentStatePathSha256"];
        var projectNameSha = appBuilder.Configuration["AppHost:ProjectNameSha256"];
        var legacySha = appBuilder.Configuration["AppHost:Sha256"];

        Assert.NotNull(pathSha);
        Assert.NotNull(deploymentStatePathSha);
        Assert.NotNull(projectNameSha);
        Assert.NotNull(legacySha);

        Assert.False(appBuilder.ExecutionContext.IsPublishMode);
        Assert.Equal(pathSha, legacySha);
    }

    [Fact]
    public void PathShaDiffersForDifferentPaths()
    {
        var options1 = new DistributedApplicationOptions
        {
            ProjectDirectory = "/home/user/project1",
            ProjectName = "TestApp",
            Args = []
        };

        var options2 = new DistributedApplicationOptions
        {
            ProjectDirectory = "/home/user/project2",
            ProjectName = "TestApp", // Same name, different path
            Args = []
        };

        var builder1 = (DistributedApplicationBuilder)DistributedApplication.CreateBuilder(options1);
        var builder2 = (DistributedApplicationBuilder)DistributedApplication.CreateBuilder(options2);

        var pathSha1 = builder1.Configuration["AppHost:PathSha256"];
        var pathSha2 = builder2.Configuration["AppHost:PathSha256"];
        var projectNameSha1 = builder1.Configuration["AppHost:ProjectNameSha256"];
        var projectNameSha2 = builder2.Configuration["AppHost:ProjectNameSha256"];

        // PathSha should differ for different paths
        Assert.NotEqual(pathSha1, pathSha2);

        // ProjectNameSha should be the same for same project name
        Assert.Equal(projectNameSha1, projectNameSha2);
    }

    [Fact]
    public void DeploymentStatePathShaDiffersForPolyglotAppHostFilesInSameDirectory()
    {
        var options1 = new DistributedApplicationOptions
        {
            ProjectDirectory = "/home/user/project",
            ProjectName = "Aspire.Hosting.RemoteHost",
            AppHostFilePath = "/home/user/project/first.ts",
            Args = []
        };

        var options2 = new DistributedApplicationOptions
        {
            ProjectDirectory = "/home/user/project",
            ProjectName = "Aspire.Hosting.RemoteHost",
            AppHostFilePath = "/home/user/project/second.ts",
            Args = []
        };

        var builder1 = (DistributedApplicationBuilder)DistributedApplication.CreateBuilder(options1);
        var builder2 = (DistributedApplicationBuilder)DistributedApplication.CreateBuilder(options2);

        Assert.Equal(
            builder1.Configuration["AppHost:PathSha256"],
            builder2.Configuration["AppHost:PathSha256"]);
        Assert.NotEqual(
            builder1.Configuration["AppHost:DeploymentStatePathSha256"],
            builder2.Configuration["AppHost:DeploymentStatePathSha256"]);
        Assert.Equal(
            builder1.Configuration["AppHost:LegacyDeploymentStatePathSha256"],
            builder2.Configuration["AppHost:LegacyDeploymentStatePathSha256"]);
        Assert.NotNull(builder1.Configuration["AppHost:LegacyDeploymentStatePathSha256"]);
        Assert.Equal(
            builder1.Configuration["AppHost:ProjectNameSha256"],
            builder2.Configuration["AppHost:ProjectNameSha256"]);
        Assert.Equal(
            builder1.Configuration["AppHost:PathSha256"],
            builder1.Configuration["AppHost:Sha256"]);
        Assert.Equal(
            builder1.Configuration["AppHost:LegacyDeploymentStatePathSha256"],
            builder1.Configuration["AppHost:Sha256"]);
    }

    [Fact]
    public void PolyglotAppHostPathIdentityPreservesFilesystemCaseSemantics()
    {
        var projectDirectory = Directory.CreateTempSubdirectory("aspire-path-case-");
        try
        {
            var appHostPath = Path.Combine(projectDirectory.FullName, "apphost.ts");
            File.WriteAllText(appHostPath, string.Empty);
            var lowerCaseOptions = new DistributedApplicationOptions
            {
                ProjectDirectory = projectDirectory.FullName,
                ProjectName = "Aspire.Hosting.RemoteHost",
                AppHostFilePath = appHostPath,
                Args = []
            };
            var upperCaseOptions = new DistributedApplicationOptions
            {
                ProjectDirectory = projectDirectory.FullName,
                ProjectName = "Aspire.Hosting.RemoteHost",
                AppHostFilePath = Path.Combine(projectDirectory.FullName, "AppHost.ts"),
                Args = []
            };

            var lowerCaseBuilder = (DistributedApplicationBuilder)DistributedApplication.CreateBuilder(lowerCaseOptions);
            var upperCaseBuilder = (DistributedApplicationBuilder)DistributedApplication.CreateBuilder(upperCaseOptions);

            if (File.Exists(upperCaseOptions.AppHostFilePath))
            {
                Assert.Equal(
                    lowerCaseBuilder.Configuration["AppHost:DeploymentStatePathSha256"],
                    upperCaseBuilder.Configuration["AppHost:DeploymentStatePathSha256"]);
            }
            else
            {
                Assert.NotEqual(
                    lowerCaseBuilder.Configuration["AppHost:DeploymentStatePathSha256"],
                    upperCaseBuilder.Configuration["AppHost:DeploymentStatePathSha256"]);
            }
        }
        finally
        {
            projectDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void PolyglotAppHostLoadsLegacyDeploymentConfiguration()
    {
        var projectDirectory = Directory.CreateTempSubdirectory("aspire-polyglot-");
        const string projectName = "Aspire.Hosting.RemoteHost";
        var legacyAppHostPath = Path.GetFullPath(Path.Join(projectDirectory.FullName, projectName));
        var legacySha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacyAppHostPath.ToLowerInvariant())));
        const string environment = "production";
        var legacyDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aspire",
            "deployments",
            legacySha);
        var legacyStatePath = Path.Combine(legacyDirectory, $"{environment}.json");
        string? canonicalDirectory = null;

        try
        {
            Directory.CreateDirectory(legacyDirectory);
            File.WriteAllText(legacyStatePath, """{"MigratedValue":"loaded"}""");

            var options = new DistributedApplicationOptions
            {
                ProjectDirectory = projectDirectory.FullName,
                ProjectName = projectName,
                AppHostFilePath = Path.Combine(projectDirectory.FullName, "apphost.ts"),
                Args = ["--publisher", "manifest"]
            };

            var builder = (DistributedApplicationBuilder)DistributedApplication.CreateBuilder(options);
            canonicalDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".aspire",
                "deployments",
                builder.Configuration["AppHost:DeploymentStatePathSha256"]!);

            Assert.True(builder.ExecutionContext.IsPublishMode);
            Assert.Equal(legacySha, builder.Configuration["AppHost:LegacyDeploymentStatePathSha256"]);
            Assert.Equal("loaded", builder.Configuration["MigratedValue"]);
        }
        finally
        {
            if (File.Exists(legacyStatePath))
            {
                File.Delete(legacyStatePath);
            }
            if (Directory.Exists(legacyDirectory) && !Directory.EnumerateFileSystemEntries(legacyDirectory).Any())
            {
                Directory.Delete(legacyDirectory);
            }
            if (canonicalDirectory is not null &&
                Directory.Exists(canonicalDirectory) &&
                !Directory.EnumerateFileSystemEntries(canonicalDirectory).Any())
            {
                Directory.Delete(canonicalDirectory);
            }
            projectDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void SourceAppHostWithInvalidEnvironmentNameDoesNotThrowInPublishMode()
    {
        var projectDirectory = Directory.CreateTempSubdirectory("aspire-invalid-env-");
        try
        {
            // An environment name outside [a-zA-Z0-9_-] makes deployment-state path resolution throw
            // ArgumentException. Best-effort state loading must swallow that so publish-mode builder
            // construction succeeds instead of failing outright.
            var options = new DistributedApplicationOptions
            {
                ProjectDirectory = projectDirectory.FullName,
                ProjectName = "Aspire.Hosting.RemoteHost",
                AppHostFilePath = Path.Combine(projectDirectory.FullName, "apphost.ts"),
                Args = ["--publisher", "manifest", "--environment", "Invalid.Env"]
            };

            var builder = (DistributedApplicationBuilder)DistributedApplication.CreateBuilder(options);

            Assert.True(builder.ExecutionContext.IsPublishMode);
            Assert.Equal("Invalid.Env", builder.Environment.EnvironmentName);
        }
        finally
        {
            projectDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void PolyglotAppHostDoesNotLoadLegacyDeploymentConfigurationAfterClear()
    {
        var projectDirectory = Directory.CreateTempSubdirectory("aspire-polyglot-");
        const string projectName = "Aspire.Hosting.RemoteHost";
        const string environment = "production";
        var options = new DistributedApplicationOptions
        {
            ProjectDirectory = projectDirectory.FullName,
            ProjectName = projectName,
            AppHostFilePath = Path.Combine(projectDirectory.FullName, "apphost.ts"),
            Args = ["--publisher", "manifest"]
        };
        var probeBuilder = (DistributedApplicationBuilder)DistributedApplication.CreateBuilder(options);
        var currentSha = probeBuilder.Configuration["AppHost:DeploymentStatePathSha256"]!;
        var legacySha = probeBuilder.Configuration["AppHost:LegacyDeploymentStatePathSha256"]!;
        var deploymentsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aspire",
            "deployments");
        var currentDirectory = Path.Combine(deploymentsDirectory, currentSha);
        var legacyDirectory = Path.Combine(deploymentsDirectory, legacySha);
        var currentStatePath = Path.Combine(currentDirectory, $"{environment}.json");
        var legacyStatePath = Path.Combine(legacyDirectory, $"{environment}.json");
        var migrationStatePath = FileDeploymentStateManager.GetMigrationStatePath(currentStatePath);

        try
        {
            Directory.CreateDirectory(currentDirectory);
            Directory.CreateDirectory(legacyDirectory);
            File.WriteAllText(legacyStatePath, """{"MigratedValue":"loaded"}""");
            File.WriteAllText(migrationStatePath, """{"LegacyFallbackDisabled":true}""");

            var builder = (DistributedApplicationBuilder)DistributedApplication.CreateBuilder(options);

            Assert.Null(builder.Configuration["MigratedValue"]);
        }
        finally
        {
            if (File.Exists(migrationStatePath))
            {
                File.Delete(migrationStatePath);
            }
            if (File.Exists(legacyStatePath))
            {
                File.Delete(legacyStatePath);
            }
            if (Directory.Exists(currentDirectory) && !Directory.EnumerateFileSystemEntries(currentDirectory).Any())
            {
                Directory.Delete(currentDirectory);
            }
            if (Directory.Exists(legacyDirectory) && !Directory.EnumerateFileSystemEntries(legacyDirectory).Any())
            {
                Directory.Delete(legacyDirectory);
            }
            projectDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void PolyglotAppHostLoadsEffectiveDeploymentConfigurationAfterCanonicalSave()
    {
        var projectDirectory = Directory.CreateTempSubdirectory("aspire-polyglot-");
        const string projectName = "Aspire.Hosting.RemoteHost";
        const string environment = "production";
        var options = new DistributedApplicationOptions
        {
            ProjectDirectory = projectDirectory.FullName,
            ProjectName = projectName,
            AppHostFilePath = Path.Combine(projectDirectory.FullName, "apphost.ts"),
            Args = ["--publisher", "manifest"]
        };
        var probeBuilder = (DistributedApplicationBuilder)DistributedApplication.CreateBuilder(options);
        var currentSha = probeBuilder.Configuration["AppHost:DeploymentStatePathSha256"]!;
        var legacySha = probeBuilder.Configuration["AppHost:LegacyDeploymentStatePathSha256"]!;
        var deploymentsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aspire",
            "deployments");
        var currentDirectory = Path.Combine(deploymentsDirectory, currentSha);
        var legacyDirectory = Path.Combine(deploymentsDirectory, legacySha);
        var currentStatePath = Path.Combine(currentDirectory, $"{environment}.json");
        var legacyStatePath = Path.Combine(legacyDirectory, $"{environment}.json");

        try
        {
            Directory.CreateDirectory(currentDirectory);
            Directory.CreateDirectory(legacyDirectory);
            File.WriteAllText(currentStatePath, """{"CurrentValue":"current"}""");
            File.WriteAllText(legacyStatePath, """{"LegacyValue":"legacy"}""");

            var builder = (DistributedApplicationBuilder)DistributedApplication.CreateBuilder(options);

            Assert.Equal("current", builder.Configuration["CurrentValue"]);
            Assert.Equal("legacy", builder.Configuration["LegacyValue"]);
        }
        finally
        {
            if (File.Exists(currentStatePath))
            {
                File.Delete(currentStatePath);
            }
            if (File.Exists(legacyStatePath))
            {
                File.Delete(legacyStatePath);
            }
            if (Directory.Exists(currentDirectory) && !Directory.EnumerateFileSystemEntries(currentDirectory).Any())
            {
                Directory.Delete(currentDirectory);
            }
            if (Directory.Exists(legacyDirectory) && !Directory.EnumerateFileSystemEntries(legacyDirectory).Any())
            {
                Directory.Delete(legacyDirectory);
            }
            projectDirectory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"LegacyFallbackDisabled":"invalid"}""")]
    [InlineData("""{"LegacyState":"invalid"}""")]
    [InlineData("""{"ClaimedSections":"null"}""")]
    [InlineData("""{"ClaimedSections":"[null]"}""")]
    public void PolyglotAppHostIgnoresLegacyDeploymentConfigurationWhenMigrationStateIsMalformed(string migrationState)
    {
        var projectDirectory = Directory.CreateTempSubdirectory("aspire-polyglot-");
        const string projectName = "Aspire.Hosting.RemoteHost";
        const string environment = "production";
        var options = new DistributedApplicationOptions
        {
            ProjectDirectory = projectDirectory.FullName,
            ProjectName = projectName,
            AppHostFilePath = Path.Combine(projectDirectory.FullName, "apphost.ts"),
            Args = ["--publisher", "manifest"]
        };
        var probeBuilder = (DistributedApplicationBuilder)DistributedApplication.CreateBuilder(options);
        var currentSha = probeBuilder.Configuration["AppHost:DeploymentStatePathSha256"]!;
        var legacySha = probeBuilder.Configuration["AppHost:LegacyDeploymentStatePathSha256"]!;
        var deploymentsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aspire",
            "deployments");
        var currentDirectory = Path.Combine(deploymentsDirectory, currentSha);
        var legacyDirectory = Path.Combine(deploymentsDirectory, legacySha);
        var currentStatePath = Path.Combine(currentDirectory, $"{environment}.json");
        var legacyStatePath = Path.Combine(legacyDirectory, $"{environment}.json");
        var migrationStatePath = FileDeploymentStateManager.GetMigrationStatePath(currentStatePath);

        try
        {
            Directory.CreateDirectory(currentDirectory);
            Directory.CreateDirectory(legacyDirectory);
            File.WriteAllText(legacyStatePath, """{"MigratedValue":"loaded"}""");
            File.WriteAllText(migrationStatePath, migrationState);

            var builder = (DistributedApplicationBuilder)DistributedApplication.CreateBuilder(options);

            Assert.Null(builder.Configuration["MigratedValue"]);
        }
        finally
        {
            if (File.Exists(migrationStatePath))
            {
                File.Delete(migrationStatePath);
            }
            if (File.Exists(legacyStatePath))
            {
                File.Delete(legacyStatePath);
            }
            if (Directory.Exists(currentDirectory) && !Directory.EnumerateFileSystemEntries(currentDirectory).Any())
            {
                Directory.Delete(currentDirectory);
            }
            if (Directory.Exists(legacyDirectory) && !Directory.EnumerateFileSystemEntries(legacyDirectory).Any())
            {
                Directory.Delete(legacyDirectory);
            }
            projectDirectory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"Key":"one","key":"two"}""")]
    public void PolyglotAppHostIgnoresInvalidDeploymentConfiguration(string deploymentState)
    {
        var projectDirectory = Directory.CreateTempSubdirectory("aspire-polyglot-");
        const string projectName = "Aspire.Hosting.RemoteHost";
        const string environment = "production";
        var options = new DistributedApplicationOptions
        {
            ProjectDirectory = projectDirectory.FullName,
            ProjectName = projectName,
            AppHostFilePath = Path.Combine(projectDirectory.FullName, "apphost.ts"),
            Args = ["--publisher", "manifest"]
        };
        var probeBuilder = (DistributedApplicationBuilder)DistributedApplication.CreateBuilder(options);
        var currentSha = probeBuilder.Configuration["AppHost:DeploymentStatePathSha256"]!;
        var currentDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aspire",
            "deployments",
            currentSha);
        var currentStatePath = Path.Combine(currentDirectory, $"{environment}.json");

        try
        {
            Directory.CreateDirectory(currentDirectory);
            File.WriteAllText(currentStatePath, deploymentState);

            var builder = (DistributedApplicationBuilder)DistributedApplication.CreateBuilder(options);

            Assert.Null(builder.Configuration["MigratedValue"]);
        }
        finally
        {
            if (File.Exists(currentStatePath))
            {
                File.Delete(currentStatePath);
            }
            if (Directory.Exists(currentDirectory) && !Directory.EnumerateFileSystemEntries(currentDirectory).Any())
            {
                Directory.Delete(currentDirectory);
            }
            projectDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void LegacyShaUsesProjectNameShaInPublishMode()
    {
        var appBuilder = DistributedApplication.CreateBuilder(["--publisher", "manifest"]);

        var pathSha = appBuilder.Configuration["AppHost:PathSha256"];
        var projectNameSha = appBuilder.Configuration["AppHost:ProjectNameSha256"];
        var legacySha = appBuilder.Configuration["AppHost:Sha256"];

        // Verify all three SHA values are available
        Assert.NotNull(pathSha);
        Assert.NotNull(projectNameSha);
        Assert.NotNull(legacySha);

        // In publish mode, legacy SHA should equal ProjectNameSha
        Assert.True(appBuilder.ExecutionContext.IsPublishMode);
        Assert.Equal(projectNameSha, legacySha);
    }

    [Fact]
    public void AddResource_InvalidName_Error()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var longName = new string('a', 65);
        var resource = new ContainerResource(longName);

        var ex = Assert.Throws<ArgumentException>(() => appBuilder.AddResource(resource));
        Assert.Equal($"Resource name '{longName}' is invalid. Name must be between 1 and 64 characters long. (Parameter 'name')", ex.Message);
    }

    [Fact]
    public void AddResource_InvalidNameWithExcludeAnnotation_Success()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var longName = new string('a', 65);
        var resource = new ContainerResource(longName);
        resource.Annotations.Add(NameValidationPolicyAnnotation.None);

        appBuilder.AddResource(resource);

        Assert.Contains(appBuilder.Resources, r => r.Name == longName);
    }

    [Fact]
    public void Build_InvalidName_Error()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var longName = new string('a', 65);
        appBuilder.Resources.Add(new ContainerResource(longName));

        var ex = Assert.Throws<ArgumentException>(appBuilder.Build);
        Assert.Equal($"Resource name '{longName}' is invalid. Name must be between 1 and 64 characters long. (Parameter 'name')", ex.Message);
    }

    [Fact]
    public void Build_InvalidNameWithExcludeAnnotation_Success()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var longName = new string('a', 65);
        var resource = new ContainerResource(longName);
        resource.Annotations.Add(NameValidationPolicyAnnotation.None);
        appBuilder.Resources.Add(resource);

        var app = appBuilder.Build();

        Assert.NotNull(app);
    }

    private sealed class TestResource : IResource
    {
        public string Name => nameof(TestResource);

        public ResourceAnnotationCollection Annotations { get; } = new();
    }
}
