// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Aspire.Hosting.ConsoleLogs;
using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Devcontainers.Codespaces;
using Aspire.Hosting.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Tests.Dashboard;

public class DashboardLifecycleHookTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [MemberData(nameof(Data))]
    public async Task WatchDashboardLogs_WrittenToHostLoggerFactory(DateTime? timestamp, string logMessage, string expectedMessage, string expectedCategory, LogLevel expectedLevel)
    {
        // Arrange
        var testSink = new TestSink();
        var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(new TestLoggerProvider(testSink));
            b.AddXunit(testOutputHelper);
        });
        var logChannel = Channel.CreateUnbounded<WriteContext>();
        testSink.MessageLogged += c => logChannel.Writer.TryWrite(c);

        var resourceLoggerService = new ResourceLoggerService();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create(logger: factory.CreateLogger<ResourceNotificationService>());
        var configuration = new ConfigurationBuilder().Build();
        var hook = CreateHook(resourceLoggerService, resourceNotificationService, configuration, loggerFactory: factory);

        var model = new DistributedApplicationModel(new ResourceCollection());
        await hook.OnBeforeStartAsync(new BeforeStartEvent(new TestServiceProvider(), model), CancellationToken.None).DefaultTimeout();

        await resourceNotificationService.PublishUpdateAsync(model.Resources.Single(), s => s).DefaultTimeout();

        string resourceId = default!;
        await foreach (var item in resourceLoggerService.WatchAnySubscribersAsync().DefaultTimeout())
        {
            if (item.Name.StartsWith(KnownResourceNames.AspireDashboard) && item.AnySubscribers)
            {
                resourceId = item.Name;
                break;
            }
        }

        // Act
        var dashboardLoggerState = resourceLoggerService.GetResourceLoggerState(resourceId);
        dashboardLoggerState.AddLog(LogEntry.Create(timestamp, logMessage, isErrorMessage: false), inMemorySource: true);

        // Assert
        while (true)
        {
            var logContext = await logChannel.Reader.ReadAsync().DefaultTimeout();
            if (logContext.LoggerName == expectedCategory)
            {
                Assert.Equal(expectedMessage, logContext.Message);
                Assert.Equal(expectedLevel, logContext.LogLevel);
                break;
            }
        }
    }

    [Fact]
    public async Task BeforeStartAsync_ExcludeLifecycleCommands_CommandsNotAddedToDashboard()
    {
        // Arrange
        var resourceLoggerService = new ResourceLoggerService();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var configuration = new ConfigurationBuilder().Build();
        var hook = CreateHook(resourceLoggerService, resourceNotificationService, configuration);

        var model = new DistributedApplicationModel(new ResourceCollection());

        // Act
        await hook.OnBeforeStartAsync(new BeforeStartEvent(new TestServiceProvider(), model), CancellationToken.None).DefaultTimeout();
        var dashboardResource = model.Resources.Single(r => string.Equals(r.Name, KnownResourceNames.AspireDashboard, StringComparisons.ResourceName));
        dashboardResource.AddLifeCycleCommands();

        // Assert
        Assert.Single(dashboardResource.Annotations.OfType<ExcludeLifecycleCommandsAnnotation>());
        Assert.Empty(dashboardResource.Annotations.OfType<ResourceCommandAnnotation>());
    }

    [Theory]
    [InlineData("localhost:8080", 8080, "1234", "cert", true)]
    [InlineData("localhost:8080", 8080, "1234", "cert", false)]
    [InlineData(null, null, null, null, null)]
    public async Task BeforeStartAsync_DashboardContainsDebugSessionInfo(string? debugSessionPort, int? expectedDebugSessionPort, string? debugSessionToken, string? debugSessionCert, bool? telemetryEnabled)
    {
        // Arrange
        var resourceLoggerService = new ResourceLoggerService();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var configurationBuilder = new ConfigurationBuilder();

        if (debugSessionPort is not null)
        {
            configurationBuilder.AddInMemoryCollection([new KeyValuePair<string, string?>("DEBUG_SESSION_PORT", debugSessionPort)]);
        }

        if (debugSessionToken is not null)
        {
            configurationBuilder.AddInMemoryCollection([new KeyValuePair<string, string?>("DEBUG_SESSION_TOKEN", debugSessionToken)]);
        }

        if (debugSessionCert is not null)
        {
            configurationBuilder.AddInMemoryCollection([new KeyValuePair<string, string?>("DEBUG_SESSION_SERVER_CERTIFICATE", debugSessionCert)]);
        }

        var configuration = configurationBuilder.Build();
        var dashboardOptions = Options.Create(new DashboardOptions
        {
            TelemetryOptOut = telemetryEnabled,
            DashboardPath = "test.dll",
            DashboardUrl = "http://localhost:8080",
            OtlpGrpcEndpointUrl = "http://localhost:4317"
        });
        var hook = CreateHook(resourceLoggerService, resourceNotificationService, configuration, dashboardOptions: dashboardOptions);

        var model = new DistributedApplicationModel(new ResourceCollection());

        // Act
        await hook.OnBeforeStartAsync(new BeforeStartEvent(new TestServiceProvider(), model), CancellationToken.None).DefaultTimeout();
        var dashboardResource = (IResourceWithEndpoints)model.Resources.Single(r => string.Equals(r.Name, KnownResourceNames.AspireDashboard, StringComparisons.ResourceName));

        var httpEndpoint = new EndpointReference(dashboardResource, "http");
        httpEndpoint.EndpointAnnotation.AllocatedEndpoint = new(httpEndpoint.EndpointAnnotation, "localhost", 8080);
        var otlpGrpcEndpoint = new EndpointReference(dashboardResource, DashboardEventHandlers.OtlpGrpcEndpointName);
        otlpGrpcEndpoint.EndpointAnnotation.AllocatedEndpoint = new(otlpGrpcEndpoint.EndpointAnnotation, "localhost", 4317);

        var context = new DistributedApplicationExecutionContext(new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run) { ServiceProvider = TestServiceProvider.Instance });
        var dashboardEnvironmentVariables = new ConcurrentDictionary<string, string?>();

        var dashboardEnvironment = await ExecutionConfigurationBuilder.Create(dashboardResource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(context, new FakeLogger(), CancellationToken.None)
            .DefaultTimeout();

        var environmentVariables = dashboardEnvironment.EnvironmentVariables.ToDictionary();

        // Assert
        Assert.Equal(expectedDebugSessionPort?.ToString(), environmentVariables.GetValueOrDefault(DashboardConfigNames.DebugSessionPortName.EnvVarName));
        Assert.Equal(debugSessionToken, environmentVariables.GetValueOrDefault(DashboardConfigNames.DebugSessionTokenName.EnvVarName));
        Assert.Equal(debugSessionCert, environmentVariables.GetValueOrDefault(DashboardConfigNames.DebugSessionServerCertificateName.EnvVarName));
        Assert.Equal(telemetryEnabled, bool.TryParse(environmentVariables.GetValueOrDefault(DashboardConfigNames.DebugSessionTelemetryOptOutName.EnvVarName), out var b) ? b : null);
    }

    [Fact]
    public async Task ConfigureEnvironmentVariables_HasAspireDashboardEnvVars_CopiedToDashboard()
    {
        // Arrange
        var resourceLoggerService = new ResourceLoggerService();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var configurationBuilder = new ConfigurationBuilder();
        configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            { "ASPIRE_DASHBOARD_PURPLE_MONKEY_DISHWASHER", "true" }
        });
        var configuration = configurationBuilder.Build();
        var dashboardOptions = Options.Create(new DashboardOptions
        {
            DashboardPath = "test.dll",
            DashboardUrl = "http://localhost:8080",
            OtlpGrpcEndpointUrl = "http://localhost:4317",
        });
        var hook = CreateHook(resourceLoggerService, resourceNotificationService, configuration, dashboardOptions: dashboardOptions);

        var envVars = new Dictionary<string, object>();

        var dashboardResource = new ExecutableResource("aspire-dashboard", "dashboard.exe", ".");

        // Act
        await hook.ConfigureEnvironmentVariables(new EnvironmentCallbackContext(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), environmentVariables: envVars, resource: dashboardResource));

        // Assert
        Assert.Equal("true", envVars.Single(e => e.Key == "ASPIRE_DASHBOARD_PURPLE_MONKEY_DISHWASHER").Value);
    }

    [Fact]
    public async Task AddDashboardResource_CreatesExecutableResourceWithCustomRuntimeConfig()
    {
        // Arrange
        var resourceLoggerService = new ResourceLoggerService();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var configuration = new ConfigurationBuilder().Build();

        // Create a temporary test dashboard directory with a dll and runtimeconfig.json
        var tempDir = Path.GetTempFileName();
        File.Delete(tempDir);
        Directory.CreateDirectory(tempDir);

        try
        {
            var dashboardDll = Path.Combine(tempDir, "Aspire.Dashboard.dll");
            var runtimeConfig = Path.Combine(tempDir, "Aspire.Dashboard.runtimeconfig.json");

            // Create a mock DLL file
            File.WriteAllText(dashboardDll, "mock dll content");

            // Create a mock runtime config similar to the real one
            var originalConfig = new
            {
                runtimeOptions = new
                {
                    tfm = "net8.0",
                    rollForward = "Major",
                    frameworks = new[]
                    {
                        new { name = "Microsoft.NETCore.App", version = "8.0.0" },
                        new { name = "Microsoft.AspNetCore.App", version = "8.0.0" }
                    },
                    configProperties = new
                    {
                        SystemGCServer = true,
                        SystemGCDynamicAdaptationMode = 1,
                        SystemRuntimeSerializationEnableUnsafeBinaryFormatterSerialization = false
                    }
                }
            };

            File.WriteAllText(runtimeConfig, JsonSerializer.Serialize(originalConfig, new JsonSerializerOptions { WriteIndented = true }));

            var dashboardOptions = Options.Create(new DashboardOptions { DashboardPath = dashboardDll });
            var hook = CreateHook(resourceLoggerService, resourceNotificationService, configuration, dashboardOptions: dashboardOptions);

            var model = new DistributedApplicationModel(new ResourceCollection());

            // Act
            await hook.OnBeforeStartAsync(new BeforeStartEvent(new TestServiceProvider(), model), CancellationToken.None);

            // Assert
            var dashboardResource = Assert.Single(model.Resources);
            Assert.Equal(KnownResourceNames.AspireDashboard, dashboardResource.Name);

            var executableResource = Assert.IsType<ExecutableResource>(dashboardResource);
            Assert.Equal("dotnet", executableResource.Command);

            // Verify the command line arguments include exec --runtimeconfig
            var argsAnnotation = executableResource.Annotations.OfType<CommandLineArgsCallbackAnnotation>().Single();
            var args = new List<object>();
            await argsAnnotation.Callback(new CommandLineArgsCallbackContext(args));

            Assert.Equal(4, args.Count);
            Assert.Equal("exec", args[0]);
            Assert.Equal("--runtimeconfig", args[1]);
            Assert.True(File.Exists((string)args[2]), "Custom runtime config file should exist");
            Assert.Equal(dashboardDll, args[3]);

            // Verify that the custom runtime config has been updated with current framework versions
            var customConfigContent = File.ReadAllText((string)args[2]);
            var customConfig = JsonSerializer.Deserialize<JsonElement>(customConfigContent);

            var frameworks = customConfig.GetProperty("runtimeOptions").GetProperty("frameworks").EnumerateArray().ToArray();
            var netCoreFramework = frameworks.First(f => f.GetProperty("name").GetString() == "Microsoft.NETCore.App");
            var aspNetCoreFramework = frameworks.First(f => f.GetProperty("name").GetString() == "Microsoft.AspNetCore.App");

            // The versions should be updated to match the AppHost's target framework versions
            // In the test environment, the AppHost targets .NET 8.0, so the versions should be "8.0.0"
            Assert.Equal("8.0.0", netCoreFramework.GetProperty("version").GetString());
            Assert.Equal("8.0.0", aspNetCoreFramework.GetProperty("version").GetString());
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AddDashboardResource_WithExecutablePath_CreatesCorrectArguments()
    {
        // Arrange
        var resourceLoggerService = new ResourceLoggerService();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var configuration = new ConfigurationBuilder().Build();

        // Create a temporary test dashboard directory with exe, dll and runtimeconfig.json
        var tempDir = Path.GetTempFileName();
        File.Delete(tempDir);
        Directory.CreateDirectory(tempDir);

        try
        {
            var dashboardExe = Path.Combine(tempDir, "Aspire.Dashboard.exe");
            var dashboardDll = Path.Combine(tempDir, "Aspire.Dashboard.dll");
            var runtimeConfig = Path.Combine(tempDir, "Aspire.Dashboard.runtimeconfig.json");

            // Create mock files
            File.WriteAllText(dashboardExe, "mock exe content");
            File.WriteAllText(dashboardDll, "mock dll content");

            var originalConfig = new
            {
                runtimeOptions = new
                {
                    tfm = "net8.0",
                    rollForward = "Major",
                    frameworks = new[]
                    {
                        new { name = "Microsoft.NETCore.App", version = "8.0.0" },
                        new { name = "Microsoft.AspNetCore.App", version = "8.0.0" }
                    }
                }
            };

            File.WriteAllText(runtimeConfig, JsonSerializer.Serialize(originalConfig, new JsonSerializerOptions { WriteIndented = true }));

            var dashboardOptions = Options.Create(new DashboardOptions { DashboardPath = dashboardExe });
            var hook = CreateHook(resourceLoggerService, resourceNotificationService, configuration, dashboardOptions: dashboardOptions);

            var model = new DistributedApplicationModel(new ResourceCollection());

            // Act
            await hook.OnBeforeStartAsync(new BeforeStartEvent(new TestServiceProvider(), model), CancellationToken.None);

            // Assert
            var dashboardResource = Assert.Single(model.Resources);
            var executableResource = Assert.IsType<ExecutableResource>(dashboardResource);
            Assert.Equal("dotnet", executableResource.Command);

            var argsAnnotation = executableResource.Annotations.OfType<CommandLineArgsCallbackAnnotation>().Single();
            var args = new List<object>();
            await argsAnnotation.Callback(new CommandLineArgsCallbackContext(args));

            Assert.Equal(4, args.Count);
            Assert.Equal("exec", args[0]);
            Assert.Equal("--runtimeconfig", args[1]);
            Assert.True(File.Exists((string)args[2]), "Custom runtime config file should exist");
            Assert.Equal(dashboardDll, args[3]); // Should point to the DLL, not the EXE
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AddDashboardResource_WithUnixExecutablePath_CreatesCorrectArguments()
    {
        // Arrange
        var resourceLoggerService = new ResourceLoggerService();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var configuration = new ConfigurationBuilder().Build();

        // Create a temporary test dashboard directory with Unix executable (no extension), dll and runtimeconfig.json
        var tempDir = Path.GetTempFileName();
        File.Delete(tempDir);
        Directory.CreateDirectory(tempDir);

        try
        {
            var dashboardExe = Path.Combine(tempDir, "Aspire.Dashboard"); // No extension for Unix
            var dashboardDll = Path.Combine(tempDir, "Aspire.Dashboard.dll");
            var runtimeConfig = Path.Combine(tempDir, "Aspire.Dashboard.runtimeconfig.json");

            // Create mock files
            File.WriteAllText(dashboardExe, "mock exe content");
            File.WriteAllText(dashboardDll, "mock dll content");

            var originalConfig = new
            {
                runtimeOptions = new
                {
                    tfm = "net8.0",
                    rollForward = "Major",
                    frameworks = new[]
                    {
                        new { name = "Microsoft.NETCore.App", version = "8.0.0" },
                        new { name = "Microsoft.AspNetCore.App", version = "8.0.0" }
                    }
                }
            };

            File.WriteAllText(runtimeConfig, JsonSerializer.Serialize(originalConfig, new JsonSerializerOptions { WriteIndented = true }));

            var dashboardOptions = Options.Create(new DashboardOptions { DashboardPath = dashboardExe });
            var hook = CreateHook(resourceLoggerService, resourceNotificationService, configuration, dashboardOptions: dashboardOptions);

            var model = new DistributedApplicationModel(new ResourceCollection());

            // Act
            await hook.OnBeforeStartAsync(new BeforeStartEvent(new TestServiceProvider(), model), CancellationToken.None);

            // Assert
            var dashboardResource = Assert.Single(model.Resources);
            var executableResource = Assert.IsType<ExecutableResource>(dashboardResource);
            Assert.Equal("dotnet", executableResource.Command);

            var argsAnnotation = executableResource.Annotations.OfType<CommandLineArgsCallbackAnnotation>().Single();
            var args = new List<object>();
            await argsAnnotation.Callback(new CommandLineArgsCallbackContext(args));

            Assert.Equal(4, args.Count);
            Assert.Equal("exec", args[0]);
            Assert.Equal("--runtimeconfig", args[1]);
            Assert.True(File.Exists((string)args[2]), "Custom runtime config file should exist");
            Assert.Equal(dashboardDll, args[3]); // Should point to the DLL, not the EXE
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AddDashboardResource_WithDirectDllPath_CreatesCorrectArguments()
    {
        // Arrange
        var resourceLoggerService = new ResourceLoggerService();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var configuration = new ConfigurationBuilder().Build();

        // Create a temporary test dashboard directory with direct dll and runtimeconfig.json
        var tempDir = Path.GetTempFileName();
        File.Delete(tempDir);
        Directory.CreateDirectory(tempDir);

        try
        {
            var dashboardDll = Path.Combine(tempDir, "Aspire.Dashboard.dll");
            var runtimeConfig = Path.Combine(tempDir, "Aspire.Dashboard.runtimeconfig.json");

            // Create mock files
            File.WriteAllText(dashboardDll, "mock dll content");

            var originalConfig = new
            {
                runtimeOptions = new
                {
                    tfm = "net8.0",
                    rollForward = "Major",
                    frameworks = new[]
                    {
                        new { name = "Microsoft.NETCore.App", version = "8.0.0" },
                        new { name = "Microsoft.AspNetCore.App", version = "8.0.0" }
                    }
                }
            };

            File.WriteAllText(runtimeConfig, JsonSerializer.Serialize(originalConfig, new JsonSerializerOptions { WriteIndented = true }));

            var dashboardOptions = Options.Create(new DashboardOptions { DashboardPath = dashboardDll });
            var hook = CreateHook(resourceLoggerService, resourceNotificationService, configuration, dashboardOptions: dashboardOptions);

            var model = new DistributedApplicationModel(new ResourceCollection());

            // Act
            await hook.OnBeforeStartAsync(new BeforeStartEvent(new TestServiceProvider(), model), CancellationToken.None);

            // Assert
            var dashboardResource = Assert.Single(model.Resources);
            var executableResource = Assert.IsType<ExecutableResource>(dashboardResource);
            Assert.Equal("dotnet", executableResource.Command);

            var argsAnnotation = executableResource.Annotations.OfType<CommandLineArgsCallbackAnnotation>().Single();
            var args = new List<object>();
            await argsAnnotation.Callback(new CommandLineArgsCallbackContext(args));

            Assert.Equal(4, args.Count);
            Assert.Equal("exec", args[0]);
            Assert.Equal("--runtimeconfig", args[1]);
            Assert.True(File.Exists((string)args[2]), "Custom runtime config file should exist");
            Assert.Equal(dashboardDll, args[3]); // Should point to the same DLL, not modify it
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static DashboardEventHandlers CreateHook(
        ResourceLoggerService resourceLoggerService,
        ResourceNotificationService resourceNotificationService,
        IConfiguration configuration,
        ILoggerFactory? loggerFactory = null,
        IOptions<CodespacesOptions>? codespacesOptions = null,
        IOptions<DashboardOptions>? dashboardOptions = null
        )
    {
        codespacesOptions ??= Options.Create(new CodespacesOptions());
        dashboardOptions ??= Options.Create(new DashboardOptions { DashboardPath = "test.dll" });
        var rewriter = new CodespacesUrlRewriter(codespacesOptions);

        return new DashboardEventHandlers(
            configuration,
            dashboardOptions,
            NullLogger<DistributedApplication>.Instance,
            new TestDashboardEndpointProvider(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            resourceNotificationService,
            resourceLoggerService,
            loggerFactory ?? NullLoggerFactory.Instance,
            new DcpNameGenerator(configuration, Options.Create(new DcpOptions())),
            new TestHostApplicationLifetime(),
            new Hosting.Eventing.DistributedApplicationEventing(),
            rewriter,
            new FileSystemService(configuration)
            );
    }

    public static IEnumerable<object?[]> Data()
    {
        var timestamp = new DateTime(2001, 12, 29, 23, 59, 59, DateTimeKind.Utc);
        var message = new DashboardLogMessage
        {
            LogLevel = LogLevel.Error,
            Category = "TestCategory",
            Message = "Hello world",
            Timestamp = timestamp.ToString(KnownFormats.ConsoleLogsTimestampFormat, CultureInfo.InvariantCulture),
        };
        var messageJson = JsonSerializer.Serialize(message, DashboardLogMessageContext.Default.DashboardLogMessage);

        yield return new object?[]
        {
            DateTime.UtcNow,
            messageJson,
            "Hello world",
            "Aspire.Hosting.Dashboard.TestCategory",
            LogLevel.Error
        };
        yield return new object?[]
        {
            null,
            messageJson,
            "Hello world",
            "Aspire.Hosting.Dashboard.TestCategory",
            LogLevel.Error
        };

        message = new DashboardLogMessage
        {
            LogLevel = LogLevel.Critical,
            Category = "TestCategory.TestSubCategory",
            Message = "Error message",
            Exception = new InvalidOperationException("Error!").ToString(),
            Timestamp = timestamp.ToString(KnownFormats.ConsoleLogsTimestampFormat, CultureInfo.InvariantCulture),
        };
        messageJson = JsonSerializer.Serialize(message, DashboardLogMessageContext.Default.DashboardLogMessage);

        yield return new object?[]
        {
            null,
            messageJson,
            $"Error message{Environment.NewLine}System.InvalidOperationException: Error!",
            "Aspire.Hosting.Dashboard.TestCategory.TestSubCategory",
            LogLevel.Critical
        };
    }

    private sealed class TestDashboardEndpointProvider : IDashboardEndpointProvider
    {
        public Task<string> GetResourceServiceUriAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult("http://localhost:1010");
        }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted { get; }
        public CancellationToken ApplicationStopped { get; }
        public CancellationToken ApplicationStopping { get; }

        public void StopApplication()
        {
        }
    }
}
