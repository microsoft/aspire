// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECERTIFICATES001

using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Dcp;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Keycloak.Tests;

public class KeycloakResourceBuilderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingAdminParametersReportUnresolvedState(bool customUserName)
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestDcpExecutor(options => options.DisableDashboard = false);
        var userName = customUserName ? builder.AddParameter("username") : null;
        var password = builder.AddParameter("password", secret: true);
        var keycloak = builder.AddKeycloak("keycloak", adminUsername: userName, adminPassword: password);
        using var app = builder.Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var orchestrator = app.Services.GetRequiredService<ApplicationOrchestratorProxy>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();

        Assert.True(app.Services.GetRequiredService<IInteractionService>().IsAvailable);
        await orchestrator.PublishResourcesPreparedAsync(cts.Token);
        await orchestrator.PublishResourceStartingAsync(keycloak.Resource, cts.Token);

        Assert.True(notifications.TryGetCurrentState(keycloak.Resource.Name, out var unresolved));
        Assert.Equal(KnownResourceStates.UnresolvedParameters, unresolved.Snapshot.State?.Text);
        Assert.Equal(
            customUserName ? ["password", "username"] : ["password"],
            Assert.IsType<string[]>(Assert.Single(unresolved.Snapshot.Properties, p => p.Name == "resource.unresolvedParameters").Value));

        var configurationTask = ExecutionConfigurationBuilder.Create(keycloak.Resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(builder.ExecutionContext, NullLogger.Instance, cts.Token);
        Assert.False(configurationTask.IsCompleted);

        if (userName is not null)
        {
            await userName.Resource.SetValueAsync("admin-user", cts.Token);
            await notifications.WaitForResourceAsync(keycloak.Resource.Name, e =>
                e.Snapshot.Properties.Any(p => p.Name == "resource.unresolvedParameters" &&
                    p.Value is string[] names && names.SequenceEqual(["password"])), cts.Token);
            Assert.False(configurationTask.IsCompleted);
        }

        await password.Resource.SetValueAsync("admin-password", cts.Token);
        var configuration = await configurationTask;
        Assert.Null(configuration.Exception);
        var environment = configuration.EnvironmentVariables.ToDictionary();
        Assert.Equal(customUserName ? "admin-user" : "admin", environment["KC_BOOTSTRAP_ADMIN_USERNAME"]);
        Assert.Equal("admin-password", environment["KC_BOOTSTRAP_ADMIN_PASSWORD"]);
        var starting = await notifications.WaitForResourceAsync(keycloak.Resource.Name, e => e.Snapshot.State?.Text == KnownResourceStates.Starting, cts.Token);
        Assert.Empty(starting.Snapshot.Properties.Where(p => p.Name == "resource.unresolvedParameters"));
    }

    [Fact]
    public void AddKeycloakWithDefaultsAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var resourceName = "keycloak";
        appBuilder.AddKeycloak(resourceName);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<KeycloakResource>());
        Assert.Equal(resourceName, containerResource.Name);

        const string defaultEndpointName = "http";

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == defaultEndpointName);
        Assert.Equal(8080, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal(defaultEndpointName, endpoint.Name);
        Assert.Null(endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("http", endpoint.Transport);
        Assert.Equal("http", endpoint.UriScheme);

        const string managementEndpointName = "management";

        var healthEndpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == managementEndpointName);
        Assert.Equal(9000, healthEndpoint.TargetPort);
        Assert.False(healthEndpoint.IsExternal);
        Assert.Equal(managementEndpointName, healthEndpoint.Name);
        Assert.Null(healthEndpoint.Port);
        Assert.Equal(ProtocolType.Tcp, healthEndpoint.Protocol);
        Assert.Equal("http", healthEndpoint.Transport);
        Assert.Equal("http", healthEndpoint.UriScheme);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(KeycloakContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(KeycloakContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(KeycloakContainerImageTags.Registry, containerAnnotation.Registry);
    }

    [Fact]
    public async Task AddKeycloakSwitchesPrimaryEndpointToHttpsWhenCertificateAvailable()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.Services.AddSingleton<IDeveloperCertificateService>(new TestDeveloperCertificateService(
            new List<X509Certificate2>(),
            supportsContainerTrust: true,
            trustCertificate: true,
            tlsTerminate: false));

        var keycloak = builder.AddKeycloak("keycloak", port: 8080);
        keycloak.WithAnnotation(new HttpsCertificateAnnotation
        {
            UseDeveloperCertificate = true
        }, ResourceAnnotationMutationBehavior.Replace);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var beforeStartEvent = new BeforeStartEvent(app.Services, model);
        await builder.Eventing.PublishAsync(beforeStartEvent);

        var endpoints = keycloak.Resource.Annotations.OfType<EndpointAnnotation>().ToArray();

        var primaryEndpoint = Assert.Single(endpoints, e => e.Name == "http");
        Assert.Equal("https", primaryEndpoint.UriScheme);
        Assert.True(primaryEndpoint.TlsEnabled);
        Assert.Equal(8443, primaryEndpoint.TargetPort);
        Assert.Equal(8080, primaryEndpoint.Port);
        Assert.DoesNotContain(endpoints, e => e.Name == "https");

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(keycloak.Resource, serviceProvider: app.Services);
        Assert.Equal("8443", env["KC_HTTPS_PORT"]);
    }

    [Fact]
    public void WithDataVolumeAddsVolumeAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var resourceName = "keycloak";
        var keycloak = builder.AddKeycloak(resourceName)
                              .WithDataVolume();

        var volumeAnnotation = keycloak.Resource.Annotations.OfType<ContainerMountAnnotation>().Single();

        Assert.Equal($"{builder.GetVolumePrefix()}-{resourceName}-data", volumeAnnotation.Source);
        Assert.Equal("/opt/keycloak/data", volumeAnnotation.Target);
        Assert.Equal(ContainerMountType.Volume, volumeAnnotation.Type);
        Assert.False(volumeAnnotation.IsReadOnly);
    }

    [Fact]
    public void WithDataBindMountAddsMountAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var keycloak = builder.AddKeycloak("keycloak")
                              .WithDataBindMount("mydata");

        var volumeAnnotation = keycloak.Resource.Annotations.OfType<ContainerMountAnnotation>().Single();

        Assert.Equal(Path.Combine(builder.AppHostDirectory, "mydata"), volumeAnnotation.Source);
        Assert.Equal("/opt/keycloak/data", volumeAnnotation.Target);
        Assert.Equal(ContainerMountType.BindMount, volumeAnnotation.Type);
        Assert.False(volumeAnnotation.IsReadOnly);
    }

    [Fact]
    public void AddAddKeycloakAddsGeneratedPasswordParameterWithUserSecretsParameterDefaultInRunMode()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var rmq = appBuilder.AddKeycloak("keycloak");

        Assert.Equal("Aspire.Hosting.ApplicationModel.UserSecretsParameterDefault", rmq.Resource.AdminPasswordParameter.Default?.GetType().FullName);
    }

    [Fact]
    public void AddAddKeycloakDoesNotAddGeneratedPasswordParameterWithUserSecretsParameterDefaultInPublishMode()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var rmq = appBuilder.AddKeycloak("keycloak");

        Assert.NotEqual("Aspire.Hosting.ApplicationModel.UserSecretsParameterDefault", rmq.Resource.AdminPasswordParameter.Default?.GetType().FullName);
    }

    [Fact]
    public async Task VerifyManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var keycloak = builder.AddKeycloak("keycloak");

        var manifest = await ManifestUtils.GetManifest(keycloak.Resource);

        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "image": "{{KeycloakContainerImageTags.Registry}}/{{KeycloakContainerImageTags.Image}}:{{KeycloakContainerImageTags.Tag}}",
              "args": [
                "start-dev",
                "--import-realm"
              ],
              "env": {
                "KC_BOOTSTRAP_ADMIN_USERNAME": "admin",
                "KC_BOOTSTRAP_ADMIN_PASSWORD": "{keycloak-password.value}",
                "KC_HEALTH_ENABLED": "true",
                "KC_FEATURES": "opentelemetry"
              },
              "bindings": {
                "http": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "targetPort": 8080
                },
                "management": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "targetPort": 9000
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());
    }
}
