// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Resources;
using Aspire.Hosting.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Hosting.Tests.Dcp;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIRECERTIFICATES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

[Trait("Partition", "4")]
public sealed class DcpHostNotificationTests
{
    private static Locations CreateTestLocations()
    {
        var directoryService = new FileSystemService(new ConfigurationBuilder().Build());
        return new Locations(directoryService);
    }

    [Fact]
    public void DcpHost_WithIInteractionService_CanBeConstructed()
    {
        // Arrange
        var loggerFactory = new NullLoggerFactory();
        var dcpOptions = Options.Create(new DcpOptions());
        var dependencyCheckService = new TestDcpDependencyCheckService();
        var interactionService = new TestInteractionService();
        var locations = CreateTestLocations();
        var applicationModel = new DistributedApplicationModel(new ResourceCollection());
        var timeProvider = new FakeTimeProvider();

        var developerCertificateService = new TestDeveloperCertificateService([], false, false, false);
        var configuration = new ConfigurationBuilder().Build();

        // Act & Assert - should not throw
        var dcpHost = new DcpHost(
            loggerFactory,
            dcpOptions,
            dependencyCheckService,
            interactionService,
            locations,
            applicationModel,
            timeProvider,
            developerCertificateService,
            configuration);

        Assert.NotNull(dcpHost);
    }

    [Fact]
    public async Task DcpHost_WithUnhealthyContainerRuntime_ShowsNotification()
    {
        // Arrange
        using var app = CreateAppWithContainers();
        var applicationModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        
        var loggerFactory = new NullLoggerFactory();
        var dcpOptions = Options.Create(new DcpOptions { ContainerRuntime = "docker", CliPath = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh" });
        var dependencyCheckService = new TestDcpDependencyCheckService
        {
            // Container installed but not running - unhealthy state
            DcpInfoResult = new DcpInfo
            {
                Containers = new DcpContainersInfo
                {
                    Installed = true,
                    Running = false,
                    Error = "Docker daemon is not running"
                }
            }
        };
        var interactionService = new TestInteractionService { IsAvailable = true };
        var locations = CreateTestLocations();
        var timeProvider = new FakeTimeProvider();
        var developerCertificateService = new TestDeveloperCertificateService([], false, false, false);
        var configuration = new ConfigurationBuilder().Build();

        var dcpHost = new DcpHost(
            loggerFactory,
            dcpOptions,
            dependencyCheckService,
            interactionService,
            locations,
            applicationModel,
            timeProvider,
            developerCertificateService,
            configuration);

        // Act
        await dcpHost.EnsureDcpContainerRuntimeAsync(CancellationToken.None).DefaultTimeout();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var interaction = await interactionService.Interactions.Reader.ReadAsync(cts.Token);

        // Assert
        Assert.Equal(InteractionStrings.ContainerRuntimeUnhealthyTitle, interaction.Title);
        Assert.Contains("docker", interaction.Message);
        Assert.Contains(string.Format(CultureInfo.InvariantCulture, InteractionStrings.ContainerRuntimeUnhealthyMessage, "docker"), interaction.Message);
        var notificationOptions = Assert.IsType<NotificationInteractionOptions>(interaction.Options);
        Assert.Equal(MessageIntent.Error, notificationOptions.Intent);
    }

    [Fact]
    public async Task DcpHost_WithUntrustedDeveloperCertificate_ShowsNotificationAndLogsWarning()
    {
        // Arrange
        using var certificate = CreateUntrustedCertificate();
        var testSink = new TestSink();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new TestLoggerProvider(testSink));
        });
        var dcpOptions = Options.Create(new DcpOptions());
        var dependencyCheckService = new TestDcpDependencyCheckService();
        var interactionService = new TestInteractionService { IsAvailable = true };
        var locations = CreateTestLocations();
        var applicationModel = CreateApplicationModelWithHttpsEndpoint();
        var timeProvider = new FakeTimeProvider();
        var developerCertificateService = new TestDeveloperCertificateService([certificate], false, true, false, latestCertificateIsUntrusted: true);
        var configuration = new ConfigurationBuilder().Build();

        var dcpHost = new DcpHost(
            loggerFactory,
            dcpOptions,
            dependencyCheckService,
            interactionService,
            locations,
            applicationModel,
            timeProvider,
            developerCertificateService,
            configuration);

        // Act
        await dcpHost.EnsureDevelopmentCertificateTrustAsync(CancellationToken.None).DefaultTimeout();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var interaction = await interactionService.Interactions.Reader.ReadAsync(cts.Token);

        // Assert
        Assert.Equal(InteractionStrings.DeveloperCertificateNotFullyTrustedTitle, interaction.Title);
        Assert.Equal(InteractionStrings.DeveloperCertificateNotFullyTrustedMessage, interaction.Message);
        var notificationOptions = Assert.IsType<NotificationInteractionOptions>(interaction.Options);
        Assert.Equal(MessageIntent.Error, notificationOptions.Intent);
        Assert.Contains(testSink.Writes, w => w.LogLevel == LogLevel.Warning && w.Message is not null && w.Message.Contains("aka.ms/aspire/devcerts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DcpHost_WithHealthyContainerRuntime_DoesNotShowNotification()
    {
        // Arrange
        using var app = CreateAppWithContainers();
        var applicationModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        
        var loggerFactory = new NullLoggerFactory();
        var dcpOptions = Options.Create(new DcpOptions { ContainerRuntime = "docker", CliPath = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh" });
        var dependencyCheckService = new TestDcpDependencyCheckService
        {
            // Container installed and running - healthy state
            DcpInfoResult = new DcpInfo
            {
                Containers = new DcpContainersInfo
                {
                    Installed = true,
                    Running = true,
                    Error = null
                }
            }
        };
        var interactionService = new TestInteractionService { IsAvailable = true };
        var locations = CreateTestLocations();
        var timeProvider = new FakeTimeProvider();
        var developerCertificateService = new TestDeveloperCertificateService([], false, false, false);
        var configuration = new ConfigurationBuilder().Build();

        var dcpHost = new DcpHost(
            loggerFactory,
            dcpOptions,
            dependencyCheckService,
            interactionService,
            locations,
            applicationModel,
            timeProvider,
            developerCertificateService,
            configuration);

        // Act
        await dcpHost.EnsureDcpContainerRuntimeAsync(CancellationToken.None).DefaultTimeout();

        // Use a short timeout to check that no notification is sent
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var hasInteraction = false;
        try
        {
            await interactionService.Interactions.Reader.ReadAsync(cts.Token);
            hasInteraction = true;
        }
        catch (OperationCanceledException)
        {
            // Expected - no notification should be sent
        }

        // Assert - no notification should be shown for healthy runtime
        Assert.False(hasInteraction);
    }

    [Fact]
    public async Task DcpHost_WithDashboardDisabled_DoesNotShowNotification()
    {
        // Arrange
        using var app = CreateAppWithContainers();
        var applicationModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        
        var loggerFactory = new NullLoggerFactory();
        var dcpOptions = Options.Create(new DcpOptions { ContainerRuntime = "docker", CliPath = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh" });
        var dependencyCheckService = new TestDcpDependencyCheckService
        {
            // Container installed but not running - unhealthy state
            DcpInfoResult = new DcpInfo
            {
                Containers = new DcpContainersInfo
                {
                    Installed = true,
                    Running = false,
                    Error = "Docker daemon is not running"
                }
            }
        };
        var interactionService = new TestInteractionService { IsAvailable = false }; // Dashboard disabled
        var locations = CreateTestLocations();
        var timeProvider = new FakeTimeProvider();
        var developerCertificateService = new TestDeveloperCertificateService([], false, false, false);
        var configuration = new ConfigurationBuilder().Build();

        var dcpHost = new DcpHost(
            loggerFactory,
            dcpOptions,
            dependencyCheckService,
            interactionService,
            locations,
            applicationModel,
            timeProvider,
            developerCertificateService,
            configuration);

        // Act
        await dcpHost.EnsureDcpContainerRuntimeAsync(CancellationToken.None).DefaultTimeout();

        // Use a short timeout to check that no notification is sent
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var hasInteraction = false;
        try
        {
            await interactionService.Interactions.Reader.ReadAsync(cts.Token);
            hasInteraction = true;
        }
        catch (OperationCanceledException)
        {
            // Expected - no notification should be sent when dashboard is disabled
        }

        // Assert - no notification should be shown when dashboard is disabled
        Assert.False(hasInteraction);
    }

    [Fact]
    public async Task DcpHost_WithPodmanUnhealthy_ShowsCorrectMessage()
    {
        // Arrange
        using var app = CreateAppWithContainers();
        var applicationModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        
        var loggerFactory = new NullLoggerFactory();
        var dcpOptions = Options.Create(new DcpOptions { ContainerRuntime = "podman", CliPath = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh" });
        var dependencyCheckService = new TestDcpDependencyCheckService
        {
            // Container installed but not running - unhealthy state
            DcpInfoResult = new DcpInfo
            {
                Containers = new DcpContainersInfo
                {
                    Installed = true,
                    Running = false,
                    Error = "Podman is not running"
                }
            }
        };
        var interactionService = new TestInteractionService { IsAvailable = true };
        var locations = CreateTestLocations();
        var timeProvider = new FakeTimeProvider();
        var developerCertificateService = new TestDeveloperCertificateService([], false, false, false);
        var configuration = new ConfigurationBuilder().Build();

        var dcpHost = new DcpHost(
            loggerFactory,
            dcpOptions,
            dependencyCheckService,
            interactionService,
            locations,
            applicationModel,
            timeProvider,
            developerCertificateService,
            configuration);

        // Act
        await dcpHost.EnsureDcpContainerRuntimeAsync(CancellationToken.None).DefaultTimeout();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var interaction = await interactionService.Interactions.Reader.ReadAsync(cts.Token);

        // Assert
        Assert.Equal(InteractionStrings.ContainerRuntimeUnhealthyTitle, interaction.Title);
        Assert.Contains("podman", interaction.Message);
        Assert.Contains(InteractionStrings.ContainerRuntimePodmanAdvice, interaction.Message);
        var notificationOptions = Assert.IsType<NotificationInteractionOptions>(interaction.Options);
        Assert.Equal(MessageIntent.Error, notificationOptions.Intent);
        Assert.Null(notificationOptions.LinkUrl); // No specific link for Podman
    }

    [Fact]
    public async Task DcpHost_WithUnhealthyContainerRuntime_NotificationCancelledWhenRuntimeBecomesHealthy()
    {
        // Arrange - this test verifies that the notification is cancelled when runtime becomes healthy
        using var app = CreateAppWithContainers();
        var applicationModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        
        var loggerFactory = new NullLoggerFactory();
        var dcpOptions = Options.Create(new DcpOptions { ContainerRuntime = "docker", CliPath = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh" });
        var dependencyCheckService = new TestDcpDependencyCheckService
        {
            // Initially unhealthy
            DcpInfoResult = new DcpInfo
            {
                Containers = new DcpContainersInfo
                {
                    Installed = true,
                    Running = false,
                    Error = "Docker daemon is not running"
                }
            }
        };
        var interactionService = new TestInteractionService { IsAvailable = true };
        var locations = CreateTestLocations();
        var timeProvider = new FakeTimeProvider();
        var developerCertificateService = new TestDeveloperCertificateService([], false, false, false);
        var configuration = new ConfigurationBuilder().Build();

        var dcpHost = new DcpHost(
            loggerFactory,
            dcpOptions,
            dependencyCheckService,
            interactionService,
            locations,
            applicationModel,
            timeProvider,
            developerCertificateService,
            configuration);

        // Act
        await dcpHost.EnsureDcpContainerRuntimeAsync(CancellationToken.None).DefaultTimeout();

        // Use ReadAsync with timeout to wait for the notification
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var interaction = await interactionService.Interactions.Reader.ReadAsync(cts.Token);
        
        // Assert - Verify notification was shown initially
        Assert.Equal(InteractionStrings.ContainerRuntimeUnhealthyTitle, interaction.Title);
        Assert.False(interaction.CancellationToken.IsCancellationRequested); // Should not be cancelled yet

        // Simulate container runtime becoming healthy
        dependencyCheckService.DcpInfoResult = new DcpInfo
        {
            Containers = new DcpContainersInfo
            {
                Installed = true,
                Running = true,
                Error = null
            }
        };

        // Advance time by 5 seconds to trigger the next polling cycle
        timeProvider.Advance(TimeSpan.FromSeconds(5));

        // Assert - The notification should now be cancelled
        using (var testTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(interaction.CancellationToken, testTimeoutCts.Token))
        {
            await Assert.ThrowsAsync<TaskCanceledException>(() => Task.Delay(-1, linkedCts.Token));
        }
    }

    [Fact]
    public async Task DcpHost_WithContainerRuntimeNotInstalled_ShowsNotificationWithoutPolling()
    {
        // Arrange
        using var app = CreateAppWithContainers();
        var applicationModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        
        var loggerFactory = new NullLoggerFactory();
        var dcpOptions = Options.Create(new DcpOptions { ContainerRuntime = "docker", CliPath = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh" });
        var dependencyCheckService = new TestDcpDependencyCheckService
        {
            // Container not installed
            DcpInfoResult = new DcpInfo
            {
                Containers = new DcpContainersInfo
                {
                    Installed = false,
                    Running = false,
                    Error = "No container runtime found"
                }
            }
        };
        var interactionService = new TestInteractionService { IsAvailable = true };
        var locations = CreateTestLocations();
        var timeProvider = new FakeTimeProvider();
        var developerCertificateService = new TestDeveloperCertificateService([], false, false, false);
        var configuration = new ConfigurationBuilder().Build();

        var dcpHost = new DcpHost(
            loggerFactory,
            dcpOptions,
            dependencyCheckService,
            interactionService,
            locations,
            applicationModel,
            timeProvider,
            developerCertificateService,
            configuration);

        // Act
        await dcpHost.EnsureDcpContainerRuntimeAsync(CancellationToken.None).DefaultTimeout();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var interaction = await interactionService.Interactions.Reader.ReadAsync(cts.Token);

        // Assert
        Assert.Equal(InteractionStrings.ContainerRuntimeNotInstalledTitle, interaction.Title);
        Assert.Contains(InteractionStrings.ContainerRuntimeNotInstalledMessage, interaction.Message);
        Assert.Contains("https://aka.ms/aspire/containers", interaction.Message);
        var notificationOptions = Assert.IsType<NotificationInteractionOptions>(interaction.Options);
        Assert.Equal(MessageIntent.Error, notificationOptions.Intent);
        Assert.Equal(InteractionStrings.ContainerRuntimeLinkText, notificationOptions.LinkText);
        Assert.Equal("https://aka.ms/aspire/containers", notificationOptions.LinkUrl);

        // Verify that no polling is started by ensuring the cancellation token is not cancelled after a delay
        // This tests that the function returns immediately and doesn't start the polling task
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.False(interaction.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void CreateDcpProcessSpec_WithoutTlsCertThumbprint_DoesNotIncludeThumbprintArgument()
    {
        // Arrange
        var dcpHost = CreateDcpHostForProcessSpecTests();
        var locations = CreateTestLocations();

        // Act
        var processSpec = dcpHost.CreateDcpProcessSpec(locations);

        // Assert
        Assert.DoesNotContain("--tls-cert-thumbprint", processSpec.Arguments);
    }

    [Fact]
    public async Task CreateDcpProcessSpec_WithTlsCertThumbprint_IncludesThumbprintArgument()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Developer certificate thumbprint is only supported on Windows.");

        // Arrange
        using var certificate = CreateUntrustedCertificate();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KnownConfigNames.DcpDeveloperCertificate] = "true"
            })
            .Build();
        var dcpHost = CreateDcpHostForProcessSpecTests(
            developerCertificateService: new TestDeveloperCertificateService([certificate], false, false, false),
            configuration: configuration);
        var locations = CreateTestLocations();

        await dcpHost.PrepareDcpTlsCertificateAsync(CancellationToken.None);

        // Act
        var processSpec = dcpHost.CreateDcpProcessSpec(locations);

        // Assert
        Assert.Contains($"--tls-cert-thumbprint \"{certificate.Thumbprint}\"", processSpec.Arguments);
    }

    [Fact]
    public async Task CreateDcpProcessSpec_WithDcpDeveloperCertificateDisabled_DoesNotIncludeThumbprintArgument()
    {
        // Arrange - config not set, so PrepareDcpTlsCertificateAsync should not set the thumbprint
        using var certificate = CreateUntrustedCertificate();
        var dcpHost = CreateDcpHostForProcessSpecTests(
            developerCertificateService: new TestDeveloperCertificateService([certificate], false, false, false));
        var locations = CreateTestLocations();

        await dcpHost.PrepareDcpTlsCertificateAsync(CancellationToken.None);

        // Act
        var processSpec = dcpHost.CreateDcpProcessSpec(locations);

        // Assert - thumbprint should not appear because config is not enabled
        Assert.DoesNotContain("--tls-cert-thumbprint", processSpec.Arguments);
    }

    [Fact]
    public void CreateDcpProcessSpec_WithContainerRuntime_IncludesContainerRuntimeArgument()
    {
        // Arrange
        var resolvedOptions = GetDcpOptions();
        resolvedOptions.ContainerRuntime = "podman";
        var dcpHost = CreateDcpHostForProcessSpecTests(dcpOptions: resolvedOptions);
        var locations = CreateTestLocations();

        // Act
        var processSpec = dcpHost.CreateDcpProcessSpec(locations);

        // Assert
        Assert.Contains("--container-runtime \"podman\"", processSpec.Arguments);
    }

    private static DcpHost CreateDcpHostForProcessSpecTests(
        IDeveloperCertificateService? developerCertificateService = null,
        IConfiguration? configuration = null,
        DcpOptions? dcpOptions = null)
    {
        return new DcpHost(
            new NullLoggerFactory(),
            Options.Create(dcpOptions ?? GetDcpOptions()),
            new TestDcpDependencyCheckService(),
            new TestInteractionService(),
            CreateTestLocations(),
            new DistributedApplicationModel(new ResourceCollection()),
            new FakeTimeProvider(),
            developerCertificateService ?? new TestDeveloperCertificateService([], false, false, false),
            configuration ?? new ConfigurationBuilder().Build());
    }

    private static DcpOptions GetDcpOptions()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            DisableDashboard = true
        });
        using var host = builder.Build();
        return host.Services.GetRequiredService<IOptions<DcpOptions>>().Value;
    }

    private static DistributedApplication CreateAppWithContainers()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.AddContainer("test-container", "nginx:latest");
        return builder.Build();
    }

    private static DistributedApplicationModel CreateApplicationModelWithHttpsEndpoint()
    {
        var resource = new ContainerResource("test-resource");
        resource.Annotations.Add(new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "https", name: "https"));
        return new DistributedApplicationModel(new ResourceCollection([resource]));
    }

    private static DistributedApplicationModel CreateApplicationModelWithHttpEndpoint()
    {
        var resource = new ContainerResource("test-resource");
        resource.Annotations.Add(new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http"));
        return new DistributedApplicationModel(new ResourceCollection([resource]));
    }

    private static X509Certificate2 CreateUntrustedCertificate()
    {
        var searchPaths = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "tests", "Shared", "TestCertificates", "testCert.pfx"),
            Path.Combine(AppContext.BaseDirectory, "shared", "TestCertificates", "testCert.pfx"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "Shared", "TestCertificates", "testCert.pfx"))
        };

        foreach (var path in searchPaths)
        {
            if (File.Exists(path))
            {
                return new X509Certificate2(path, "testPassword");
            }
        }

        throw new FileNotFoundException("Could not locate test certificate file 'testCert.pfx' in expected locations.");
    }

    [Fact]
    public async Task DcpHost_WithNoHttpsResources_DoesNotShowCertificateWarning()
    {
        // Arrange - only HTTP endpoints, no HTTPS
        using var certificate = CreateUntrustedCertificate();
        var testSink = new TestSink();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new TestLoggerProvider(testSink));
        });
        var dcpOptions = Options.Create(new DcpOptions());
        var dependencyCheckService = new TestDcpDependencyCheckService();
        var interactionService = new TestInteractionService { IsAvailable = true };
        var locations = CreateTestLocations();
        var applicationModel = CreateApplicationModelWithHttpEndpoint();
        var timeProvider = new FakeTimeProvider();
        var developerCertificateService = new TestDeveloperCertificateService([certificate], false, true, false);
        var configuration = new ConfigurationBuilder().Build();

        var dcpHost = new DcpHost(
            loggerFactory,
            dcpOptions,
            dependencyCheckService,
            interactionService,
            locations,
            applicationModel,
            timeProvider,
            developerCertificateService,
            configuration);

        // Act
        await dcpHost.EnsureDevelopmentCertificateTrustAsync(CancellationToken.None).DefaultTimeout();

        // Assert - no notification or warning should be shown
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var hasInteraction = false;
        try
        {
            await interactionService.Interactions.Reader.ReadAsync(cts.Token);
            hasInteraction = true;
        }
        catch (OperationCanceledException)
        {
            // Expected - no notification
        }

        Assert.False(hasInteraction);
        Assert.DoesNotContain(testSink.Writes, w => w.LogLevel == LogLevel.Warning);
    }

    [Fact]
    public async Task DcpHost_WithNoResources_DoesNotShowCertificateWarning()
    {
        // Arrange - empty resource model
        using var certificate = CreateUntrustedCertificate();
        var testSink = new TestSink();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new TestLoggerProvider(testSink));
        });
        var dcpOptions = Options.Create(new DcpOptions());
        var dependencyCheckService = new TestDcpDependencyCheckService();
        var interactionService = new TestInteractionService { IsAvailable = true };
        var locations = CreateTestLocations();
        var applicationModel = new DistributedApplicationModel(new ResourceCollection());
        var timeProvider = new FakeTimeProvider();
        var developerCertificateService = new TestDeveloperCertificateService([certificate], false, true, false);
        var configuration = new ConfigurationBuilder().Build();

        var dcpHost = new DcpHost(
            loggerFactory,
            dcpOptions,
            dependencyCheckService,
            interactionService,
            locations,
            applicationModel,
            timeProvider,
            developerCertificateService,
            configuration);

        // Act
        await dcpHost.EnsureDevelopmentCertificateTrustAsync(CancellationToken.None).DefaultTimeout();

        // Assert - no notification or warning should be shown for empty model
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var hasInteraction = false;
        try
        {
            await interactionService.Interactions.Reader.ReadAsync(cts.Token);
            hasInteraction = true;
        }
        catch (OperationCanceledException)
        {
            // Expected - no notification
        }

        Assert.False(hasInteraction);
        Assert.DoesNotContain(testSink.Writes, w => w.LogLevel == LogLevel.Warning);
    }

    [Fact]
    public async Task DcpHost_WithHttpsCertificateConfigCallback_ShowsCertificateWarning()
    {
        // Arrange - resource has HttpsCertificateConfigurationCallbackAnnotation but no HTTPS endpoint
        using var certificate = CreateUntrustedCertificate();
        var testSink = new TestSink();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new TestLoggerProvider(testSink));
        });
        var dcpOptions = Options.Create(new DcpOptions());
        var dependencyCheckService = new TestDcpDependencyCheckService();
        var interactionService = new TestInteractionService { IsAvailable = true };
        var locations = CreateTestLocations();

        var resource = new ContainerResource("test-resource");
        resource.Annotations.Add(new HttpsCertificateConfigurationCallbackAnnotation(_ => Task.CompletedTask));
        var applicationModel = new DistributedApplicationModel(new ResourceCollection([resource]));

        var timeProvider = new FakeTimeProvider();
        var developerCertificateService = new TestDeveloperCertificateService([certificate], false, true, false, latestCertificateIsUntrusted: true);
        var appHostDirectory = Path.Combine(Path.GetTempPath(), "aspire-apphost-test");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppHost:Directory"] = appHostDirectory
            })
            .Build();

        var dcpHost = new DcpHost(
            loggerFactory,
            dcpOptions,
            dependencyCheckService,
            interactionService,
            locations,
            applicationModel,
            timeProvider,
            developerCertificateService,
            configuration);

        // Act
        await dcpHost.EnsureDevelopmentCertificateTrustAsync(CancellationToken.None).DefaultTimeout();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var interaction = await interactionService.Interactions.Reader.ReadAsync(cts.Token);

        // Assert - warning should be shown because HttpsCertificateConfigurationCallbackAnnotation indicates TLS
        Assert.Equal(InteractionStrings.DeveloperCertificateNotFullyTrustedTitle, interaction.Title);
        Assert.Contains(testSink.Writes, w => w.LogLevel == LogLevel.Warning);
    }

    [Fact]
    public async Task DcpHost_WithHttpsCertificateConfigCallbackDisabledByWithoutHttpsCertificate_DoesNotShowCertificateWarning()
    {
        // Arrange - resource has HttpsCertificateConfigurationCallbackAnnotation but disabled by WithoutHttpsCertificate
        using var certificate = CreateUntrustedCertificate();
        var testSink = new TestSink();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new TestLoggerProvider(testSink));
        });
        var dcpOptions = Options.Create(new DcpOptions());
        var dependencyCheckService = new TestDcpDependencyCheckService();
        var interactionService = new TestInteractionService { IsAvailable = true };
        var locations = CreateTestLocations();

        var resource = new ContainerResource("test-resource");
        resource.Annotations.Add(new HttpsCertificateConfigurationCallbackAnnotation(_ => Task.CompletedTask));
        // This is the state set by WithoutHttpsCertificate()
        resource.Annotations.Add(new HttpsCertificateAnnotation
        {
            Certificate = null,
            UseDeveloperCertificate = false,
        });
        var applicationModel = new DistributedApplicationModel(new ResourceCollection([resource]));

        var timeProvider = new FakeTimeProvider();
        var developerCertificateService = new TestDeveloperCertificateService([certificate], false, true, false);
        var configuration = new ConfigurationBuilder().Build();

        var dcpHost = new DcpHost(
            loggerFactory,
            dcpOptions,
            dependencyCheckService,
            interactionService,
            locations,
            applicationModel,
            timeProvider,
            developerCertificateService,
            configuration);

        // Act
        await dcpHost.EnsureDevelopmentCertificateTrustAsync(CancellationToken.None).DefaultTimeout();

        // Assert - no warning because TLS was explicitly disabled
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var hasInteraction = false;
        try
        {
            await interactionService.Interactions.Reader.ReadAsync(cts.Token);
            hasInteraction = true;
        }
        catch (OperationCanceledException)
        {
            // Expected - no notification
        }

        Assert.False(hasInteraction);
        Assert.DoesNotContain(testSink.Writes, w => w.LogLevel == LogLevel.Warning);
    }

    [Fact]
    public async Task DcpHost_WithHttpsCertificateAnnotationOnly_DoesNotShowCertificateWarning()
    {
        // Arrange - resource has HttpsCertificateAnnotation but NO HttpsCertificateConfigurationCallbackAnnotation
        // HttpsCertificateAnnotation alone has no effect without the callback annotation
        using var certificate = CreateUntrustedCertificate();
        var testSink = new TestSink();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new TestLoggerProvider(testSink));
        });
        var dcpOptions = Options.Create(new DcpOptions());
        var dependencyCheckService = new TestDcpDependencyCheckService();
        var interactionService = new TestInteractionService { IsAvailable = true };
        var locations = CreateTestLocations();

        var resource = new ContainerResource("test-resource");
        resource.Annotations.Add(new HttpsCertificateAnnotation
        {
            UseDeveloperCertificate = true,
        });
        var applicationModel = new DistributedApplicationModel(new ResourceCollection([resource]));

        var timeProvider = new FakeTimeProvider();
        var developerCertificateService = new TestDeveloperCertificateService([certificate], false, true, false);
        var configuration = new ConfigurationBuilder().Build();

        var dcpHost = new DcpHost(
            loggerFactory,
            dcpOptions,
            dependencyCheckService,
            interactionService,
            locations,
            applicationModel,
            timeProvider,
            developerCertificateService,
            configuration);

        // Act
        await dcpHost.EnsureDevelopmentCertificateTrustAsync(CancellationToken.None).DefaultTimeout();

        // Assert - no warning because HttpsCertificateAnnotation alone has no effect
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var hasInteraction = false;
        try
        {
            await interactionService.Interactions.Reader.ReadAsync(cts.Token);
            hasInteraction = true;
        }
        catch (OperationCanceledException)
        {
            // Expected - no notification
        }

        Assert.False(hasInteraction);
        Assert.DoesNotContain(testSink.Writes, w => w.LogLevel == LogLevel.Warning);
    }

    [Fact]
    public async Task DcpHost_WithHttpsCertificateConfigCallbackAndDevCert_ShowsCertificateWarning()
    {
        // Arrange - resource has both HttpsCertificateConfigurationCallbackAnnotation and HttpsCertificateAnnotation
        // with UseDeveloperCertificate = true (not disabled)
        using var certificate = CreateUntrustedCertificate();
        var testSink = new TestSink();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new TestLoggerProvider(testSink));
        });
        var dcpOptions = Options.Create(new DcpOptions());
        var dependencyCheckService = new TestDcpDependencyCheckService();
        var interactionService = new TestInteractionService { IsAvailable = true };
        var locations = CreateTestLocations();

        var resource = new ContainerResource("test-resource");
        resource.Annotations.Add(new HttpsCertificateConfigurationCallbackAnnotation(_ => Task.CompletedTask));
        resource.Annotations.Add(new HttpsCertificateAnnotation
        {
            UseDeveloperCertificate = true,
        });
        var applicationModel = new DistributedApplicationModel(new ResourceCollection([resource]));

        var timeProvider = new FakeTimeProvider();
        var developerCertificateService = new TestDeveloperCertificateService([certificate], false, true, false, latestCertificateIsUntrusted: true);
        var appHostDirectory = Path.Combine(Path.GetTempPath(), "aspire-apphost-test");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppHost:Directory"] = appHostDirectory
            })
            .Build();

        var dcpHost = new DcpHost(
            loggerFactory,
            dcpOptions,
            dependencyCheckService,
            interactionService,
            locations,
            applicationModel,
            timeProvider,
            developerCertificateService,
            configuration);

        // Act
        await dcpHost.EnsureDevelopmentCertificateTrustAsync(CancellationToken.None).DefaultTimeout();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var interaction = await interactionService.Interactions.Reader.ReadAsync(cts.Token);

        // Assert - warning should be shown because TLS is active
        Assert.Equal(InteractionStrings.DeveloperCertificateNotFullyTrustedTitle, interaction.Title);
        Assert.Contains(testSink.Writes, w => w.LogLevel == LogLevel.Warning);
    }

    private sealed class TestDcpDependencyCheckService : IDcpDependencyCheckService
    {
        public DcpInfo? DcpInfoResult { get; set; } = new DcpInfo
        {
            VersionString = DcpVersion.Dev.ToString(),
            Version = DcpVersion.Dev,
            Containers = new DcpContainersInfo
            {
                Runtime = "docker",
                Installed = true,
                Running = true
            }
        };

        public Task<DcpInfo?> GetDcpInfoAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(DcpInfoResult);
        }
    }
}

#pragma warning restore ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning restore ASPIRECERTIFICATES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
