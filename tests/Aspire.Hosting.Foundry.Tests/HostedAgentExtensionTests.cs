// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Azure.AI.Projects.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Foundry.Tests;

public class HostedAgentExtensionTests
{
    [Fact]
    public void AsHostedAgent_InRunMode_AddsHttpEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var app = builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent();

        builder.Build();

        // In run mode, the resource should have an HTTP endpoint annotation
        Assert.True(app.Resource.TryGetEndpoints(out var endpoints));
        Assert.Contains(endpoints, e => e.Name == "http");
    }

    [Fact]
    public void AsHostedAgent_InRunMode_PreservesExistingHttpEndpointTargetPort()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var app = builder.AddPythonApp("agent", "./app.py", "main:app")
            .WithHttpEndpoint(targetPort: 5000)
            .AsHostedAgent();

        builder.Build();

        Assert.True(app.Resource.TryGetEndpoints(out var endpoints));
        var httpEndpoints = endpoints.Where(e => e.Name == "http").ToList();
        Assert.Single(httpEndpoints);
        Assert.Equal(5000, httpEndpoints[0].TargetPort);
        Assert.True(httpEndpoints[0].IsProxied);
    }

    [Fact]
    public void AsHostedAgent_InRunMode_DoesNotHardCodePort()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var app = builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent();

        builder.Build();

        Assert.True(app.Resource.TryGetEndpoints(out var endpoints));
        var httpEndpoint = endpoints.Single(e => e.Name == "http");
        Assert.Null(httpEndpoint.Port);
    }

    [Fact]
    public void AsHostedAgent_InRunMode_ConfiguresSendMessageCommand()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent();

        builder.Build();

        var resource = builder.Resources.Single(r => r.Name == "agent");
        var command = Assert.Single(resource.Annotations.OfType<ResourceCommandAnnotation>());
        Assert.Equal("Send Message", command.DisplayName);
        Assert.Equal("send-message", command.Name);
        Assert.Equal("ChatSparkle", command.IconName);
        Assert.Equal(IconVariant.Regular, command.IconVariant);
        Assert.True(command.IsHighlighted);
        var argument = Assert.Single(command.Arguments);
        Assert.Equal("message", argument.Name);
        Assert.Equal(InputType.Text, argument.InputType);
        Assert.Equal("Message", argument.Label);
        Assert.True(argument.Required);
        Assert.NotNull(command.ValidateArguments);
    }

    [Fact]
    public async Task AsHostedAgent_InRunMode_SendMessageCommandRejectsWhitespaceMessage()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent();

        using var app = builder.Build();

        var resource = builder.Resources.Single(r => r.Name == "agent");
        var command = Assert.Single(resource.Annotations.OfType<ResourceCommandAnnotation>());
        Assert.NotNull(command.ValidateArguments);
        var arguments = new InteractionInputCollection(
        [
            new InteractionInput
            {
                Name = "message",
                InputType = InputType.Text,
                Value = "   "
            }
        ]);

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource, command.Name, arguments);

        Assert.False(result.Success);
        Assert.Equal("Command argument validation failed.", result.Message);
    }

    [Fact]
    public async Task AsHostedAgent_InRunMode_WithInvocationsProtocol_ConfiguresEndpointAndCommand()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent(project, configuration =>
            {
                configuration.ContainerProtocolVersions.Clear();
                configuration.ContainerProtocolVersions.Add(new ProtocolVersionRecord(ProjectsAgentProtocol.Invocations, "1.0.0"));
            });

        using var app = builder.Build();

        var resource = builder.Resources.Single(r => r.Name == "agent");
        var command = Assert.Single(resource.Annotations.OfType<ResourceCommandAnnotation>());
        Assert.Equal("send-message", command.Name);

        var urlsCallback = Assert.Single(resource.Annotations.OfType<ResourceUrlsCallbackAnnotation>());
        var url = new ResourceUrlAnnotation
        {
            Url = "http://localhost:1234",
            Endpoint = ((IResourceWithEndpoints)resource).GetEndpoint("http")
        };
        var urls = new List<ResourceUrlAnnotation> { url };
        var context = new ResourceUrlsCallbackContext(
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            resource,
            urls);

        await urlsCallback.Callback(context);

        Assert.Equal("Invocations Endpoint", url.DisplayText);
        Assert.Equal("http://localhost:1234/invocations", url.Url);
    }

    [Fact]
    public void AsHostedAgent_InRunMode_WrapsConfigurationCallbackFailures()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            builder.AddPythonApp("agent", "./app.py", "main:app")
                .AsHostedAgent(configuration => configuration.Cpu = 4.0m));

        Assert.Contains("run mode", ex.Message);
        Assert.IsType<ArgumentException>(ex.InnerException);
    }

    [Fact]
    public void AsHostedAgent_InPublishMode_DoesNotValidateRegion()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        builder.Configuration["Azure:Location"] = "invalidregion";

        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var app = builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent(project);

        Assert.NotNull(app);
    }

    [Fact]
    public void AsHostedAgent_InPublishMode_AcceptsValidRegion()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        builder.Configuration["Azure:Location"] = "eastus";

        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var app = builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent(project);

        Assert.NotNull(app);
    }

    [Fact]
    public void AsHostedAgent_NoRegionConfig_DoesNotThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var app = builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent(project);

        Assert.NotNull(app);
    }

    [Fact]
    public void AsHostedAgent_InPublishMode_CreatesHostedAgentResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent(project);

        builder.Build();

        var hostedAgent = builder.Resources.OfType<AzureHostedAgentResource>().SingleOrDefault();
        Assert.NotNull(hostedAgent);
        Assert.Equal("agent-ha", hostedAgent.Name);
    }

    [Fact]
    public async Task AsHostedAgent_InPublishMode_AddsProjectReferenceToDeploymentTarget()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        builder.AddProject<Project>("agent", launchProfileName: null)
            .AsHostedAgent(project);

        builder.Build();

        var hostedAgent = Assert.Single(builder.Resources.OfType<AzureHostedAgentResource>());

        Assert.True(hostedAgent.Target.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        Assert.Contains(relationships, r =>
            r.Type == "Reference" &&
            ReferenceEquals(r.Resource, project.Resource));

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            hostedAgent.Target, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        Assert.Contains(envVars, kvp =>
            kvp.Key == "ConnectionStrings__my-project" &&
            kvp.Value == "{my-project.connectionString}");
        Assert.Contains(envVars, kvp =>
            kvp.Key == "MY_PROJECT_CONNECTIONSTRING" &&
            kvp.Value == "Endpoint={my-project.outputs.endpoint}");
        Assert.DoesNotContain(hostedAgent.Annotations.OfType<ResourceRelationshipAnnotation>(), r =>
            r.Type == "Reference" &&
            ReferenceEquals(r.Resource, project.Resource));
    }

    [Fact]
    public void AsHostedAgent_WithOptions_AppliesAllPropertiesToConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var options = new HostedAgentOptions
        {
            Description = "test description",
            Cpu = 1m,
            Memory = 2m,
            Metadata = { ["scenario"] = "unit-test" },
            EnvironmentVariables = { ["MY_VAR"] = "my-value" },
            Protocols =
            {
                new HostedAgentProtocolVersion
                {
                    Protocol = "invocations",
                    Version = "1.0.0"
                }
            }
        };

        builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgentForExport(project, options);

        builder.Build();

        var hostedAgent = Assert.Single(builder.Resources.OfType<AzureHostedAgentResource>());

        var configuration = new HostedAgentConfiguration("test-image");
        hostedAgent.Configure!(configuration);

        Assert.Equal("test description", configuration.Description);
        Assert.Equal(1m, configuration.Cpu);
        Assert.Equal(2m, configuration.Memory);
        Assert.Equal("unit-test", configuration.Metadata["scenario"]);
        Assert.Equal("my-value", configuration.EnvironmentVariables["MY_VAR"]);
        var protocol = Assert.Single(configuration.ContainerProtocolVersions);
        Assert.Equal(ProjectsAgentProtocol.Invocations, protocol.Protocol);
        Assert.Equal("1.0.0", protocol.Version);
    }

    [Fact]
    public async Task GetResolvedEnvironmentVariables_DoesNotForwardFoundryReservedTargetEnvironmentVariables()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var agent = builder.AddExecutable("agent", "python", ".")
            .WithEnvironment("PORT", "8000")
            .WithEnvironment("AGENT_NAME", "agent")
            .WithEnvironment("FOUNDRY_MODE", "hosted")
            .WithEnvironment("MY_VAR", "my-value");

        using var app = builder.Build();
        var hostedAgent = new AzureHostedAgentResource("agent-ha", agent.Resource);

        var envVars = await AzureHostedAgentResource.GetResolvedEnvironmentVariablesAsync(
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            hostedAgent,
            agent.Resource,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.DoesNotContain("PORT", envVars.Keys);
        Assert.DoesNotContain("AGENT_NAME", envVars.Keys);
        Assert.DoesNotContain("FOUNDRY_MODE", envVars.Keys);
        Assert.Equal("my-value", envVars["MY_VAR"]);
    }

    [Theory]
    [InlineData("", "1.0.0", nameof(HostedAgentProtocolVersion.Protocol))]
    [InlineData("invocations", "", nameof(HostedAgentProtocolVersion.Version))]
    public void AsHostedAgent_WithInvalidProtocolOptions_ThrowsWithPropertyName(string protocol, string version, string expectedParamName)
    {
        var options = new HostedAgentOptions
        {
            Protocols =
            {
                new HostedAgentProtocolVersion
                {
                    Protocol = protocol,
                    Version = version
                }
            }
        };

        var ex = Assert.Throws<ArgumentException>(() => options.ApplyTo(new HostedAgentConfiguration("test-image")));
        Assert.Equal(expectedParamName, ex.ParamName);
    }

    [Fact]
    public void GetAgentEndpointProtocols_MapsContainerProtocolsToEndpointProtocols()
    {
        var endpointProtocols = AzureHostedAgentResource.GetAgentEndpointProtocols(
            [
                new ProtocolVersionRecord(ProjectsAgentProtocol.Invocations, "1.0.0"),
                new ProtocolVersionRecord(ProjectsAgentProtocol.Responses, "1.0.0"),
                new ProtocolVersionRecord(ProjectsAgentProtocol.ActivityProtocol, "1.0.0"),
                new ProtocolVersionRecord(ProjectsAgentProtocol.Invocations, "1.1.0")
            ]);

        Assert.Collection(
            endpointProtocols,
            protocol => Assert.Equal(AgentEndpointProtocol.Invocations, protocol),
            protocol => Assert.Equal(AgentEndpointProtocol.Responses, protocol),
            protocol => Assert.Equal(AgentEndpointProtocol.Activity, protocol));
    }

    [Fact]
    public void AsHostedAgent_WithNullOptions_DoesNotSetConfigureCallback()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgentForExport(project, options: null);

        builder.Build();

        var hostedAgent = Assert.Single(builder.Resources.OfType<AzureHostedAgentResource>());
        Assert.Null(hostedAgent.Configure);
    }

    [Fact]
    public void AsHostedAgent_WithNullProject_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddPythonApp("agent", "./app.py", "main:app");

        Assert.Throws<ArgumentNullException>(() => app.AsHostedAgentForExport(project: null!));
    }

    [Fact]
    public void AsHostedAgent_WithoutProject_CreatesDefaultProject()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent();

        builder.Build();

        var project = builder.Resources.OfType<AzureCognitiveServicesProjectResource>().SingleOrDefault();
        Assert.NotNull(project);
    }

    [Fact]
    public void AsHostedAgent_InRunMode_WithProject_AddsProjectDependency()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var app = builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent(project);

        builder.Build();

        Assert.Contains(app.Resource.Annotations.OfType<WaitAnnotation>(), w => ReferenceEquals(w.Resource, project.Resource));
    }

    [Fact]
    public void AsHostedAgent_InRunMode_WithProject_DoesNotCreateDefaultContainerRegistryResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent(project);

        builder.Build();

        Assert.Null(project.Resource.DefaultContainerRegistry);
        Assert.DoesNotContain(builder.Resources, r => r.Name == "my-project-acr");
    }

    [Fact]
    public async Task AsHostedAgent_InRunMode_WithProject_ExecutesBeforeStartHooksWithoutContainerRegistry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent(project);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);
    }

    [Fact]
    public async Task FoundryProject_DefaultRegistryDoesNotAddGlobalRegistryTargets()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var registry = builder.AddAzureContainerRegistry("global");
        builder.AddFoundry("account")
            .AddProject("my-project");
        var container = builder.AddContainer("redis", "redis:latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, model));

        var registryTargets = container.Resource.Annotations.OfType<RegistryTargetAnnotation>().ToList();
        var registryTarget = Assert.Single(registryTargets);
        Assert.Same(registry.Resource, registryTarget.Registry);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);

    [Fact]
    public void AsHostedAgent_StampsReferenceRoleAssignmentAnnotationOnTarget_WithAzureAIUserRole()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent(project);

        var hostedAgent = Assert.Single(builder.Resources.OfType<AzureHostedAgentResource>());
        var account = Assert.Single(builder.Resources.OfType<FoundryResource>());

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates.
        var annotation = Assert.Single(hostedAgent.Target.Annotations.OfType<ReferenceRoleAssignmentAnnotation>());
        Assert.Same(account, annotation.Target);
        Assert.Contains(annotation.Roles, role =>
            string.Equals(role.Id, AzureHostedAgentResource.AzureAIUserRoleDefinitionId, StringComparison.OrdinalIgnoreCase));
#pragma warning restore ASPIREAZURE003
    }

    [Fact]
    public void AsHostedAgent_ReferenceRoleAssignmentAnnotation_GrantsOnlyAzureAIUserRole()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent(project);

        var hostedAgent = Assert.Single(builder.Resources.OfType<AzureHostedAgentResource>());
        var account = Assert.Single(builder.Resources.OfType<FoundryResource>());
        Assert.True(account.TryGetLastAnnotation<DefaultRoleAssignmentsAnnotation>(out var defaults));

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates.
        var annotation = Assert.Single(hostedAgent.Target.Annotations.OfType<ReferenceRoleAssignmentAnnotation>());

        // The implied grant is least-privilege: only "Azure AI User" is required to invoke the agent.
        var role = Assert.Single(annotation.Roles);
        Assert.Equal(AzureHostedAgentResource.AzureAIUserRoleDefinitionId, role.Id, ignoreCase: true);

        // The account's default data-plane roles must NOT be folded in here. A consumer that references
        // the account directly still receives them via the preparer's normal walk, and a consumer that
        // explicitly suppresses them must keep them suppressed.
        foreach (var defaultRole in defaults.Roles)
        {
            Assert.DoesNotContain(annotation.Roles, r => string.Equals(r.Id, defaultRole.Id, StringComparison.OrdinalIgnoreCase));
        }
#pragma warning restore ASPIREAZURE003
    }

    [Fact]
    public void AsHostedAgent_MultipleHostedAgents_EachTargetCarriesItsOwnReferenceRoleAssignment()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var otherProject = builder.AddFoundry("account2")
            .AddProject("other-project");

        builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent(project);
        builder.AddPythonApp("agent2", "./app.py", "main:app")
            .AsHostedAgent(otherProject);

        var hostedAgents = builder.Resources.OfType<AzureHostedAgentResource>().ToList();
        Assert.Equal(2, hostedAgents.Count);

        var account = Assert.Single(builder.Resources.OfType<FoundryResource>(), r => r.Name == "account");
        var account2 = Assert.Single(builder.Resources.OfType<FoundryResource>(), r => r.Name == "account2");

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates.
        var targets = hostedAgents
            .Select(a => Assert.Single(a.Target.Annotations.OfType<ReferenceRoleAssignmentAnnotation>()).Target)
            .ToList();

        Assert.Contains(account, targets);
        Assert.Contains(account2, targets);
#pragma warning restore ASPIREAZURE003
    }

    private sealed class Project : IProjectMetadata
    {
        public string ProjectPath => "project";
    }
}
