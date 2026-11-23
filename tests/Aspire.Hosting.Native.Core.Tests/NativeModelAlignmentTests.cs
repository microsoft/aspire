// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Model;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeModelAlignmentTests
{
    [Fact]
    public async Task EnvironmentAssignmentRetainsNativeIdentityWithoutReadinessDependency()
    {
        var session = new NativeApplicationServer().OpenApplication();
        var composition = session.StartGeneration();
        var first = composition.AddComputeEnvironment("first", "docker-compose/environment");
        var second = composition.AddComputeEnvironment("second", "example/custom-target");
        var workload = composition.AddContainer("api", "nginx:alpine")
            .WithEndpoint(new() { Name = "http", Protocol = "TCP", UriScheme = "http", TargetPort = 80, HostPort = 0, EnvironmentVariable = "" })
            .WithEndpoint(new() { Name = "tcp", Protocol = "TCP", UriScheme = "tcp", TargetPort = 90, HostPort = 0, EnvironmentVariable = "" })
            .WithExternalHttpEndpoints();
        workload.WithComputeEnvironment(first);
        workload.WithComputeEnvironment(second);
        var snapshot = composition.Model.ReadResource(workload.Handle);
        Assert.Equal(second.Handle, snapshot.ComputeEnvironment);
        Assert.Empty(snapshot.Dependencies);
        var exported = JsonNode.Parse(composition.ExportPublishModel())!;
        var api = exported["resources"]!.AsArray().Single(resource => resource!["name"]!.GetValue<string>() == "api")!;
        Assert.Equal("second", api["computeEnvironment"]!.GetValue<string>());
        Assert.Equal("second", api["configuration"]!["publish.computeEnvironment"]!.GetValue<string>());
        Assert.Collection(api["endpoints"]!.AsArray(),
            endpoint => Assert.True(endpoint!["isExternal"]!.GetValue<bool>()),
            endpoint => Assert.False(endpoint!["isExternal"]!.GetValue<bool>()));
        await session.RetireGeneration(composition);
    }

    [Fact]
    public async Task EnvironmentAssignmentRejectsCrossCompositionAndForgedConfiguration()
    {
        var server = new NativeApplicationServer();
        var first = server.OpenApplication();
        var second = server.OpenApplication();
        var model = first.StartGeneration();
        var other = second.StartGeneration();
        var api = model.AddContainer("api", "nginx:alpine");
        var invalid = model.AddResource("ordinary", "docker-compose/environment");
        var environment = other.AddComputeEnvironment("compose", "docker-compose/environment");
        Assert.Throws<ArgumentException>(() => api.WithComputeEnvironment(environment));
        Assert.Throws<ArgumentException>(() => api.WithComputeEnvironment(invalid));
        Assert.Throws<ArgumentException>(() => api.WithComputeEnvironment(api));
        Assert.Throws<ArgumentException>(() => api.SetResourceConfiguration(new()
        {
            Properties = [new() { Name = "publish.computeEnvironment", Value = "ordinary" }]
        }));
        await first.RetireGeneration(model);
        await second.RetireGeneration(other);
    }

    [Fact]
    public async Task PublishingParametersRemainUnboundAndOnlyNonsecretDefaultsAreExported()
    {
        var session = new NativeApplicationServer().OpenApplication();
        var composition = session.StartGeneration();
        var required = composition.AddConfiguredParameter("required", false, true);
        var password = composition.AddConfiguredParameter("password", true, true);
        var defaults = composition.AddParameterValue("workers", "3", false, true);
        var secret = composition.AddParameterValue("explicit-secret", "must-not-be-exported", true, false);
        Assert.False(required.Value.IsSecret);
        Assert.False(required.Value.HasValue);
        Assert.True(password.Value.IsSecret);
        Assert.False(password.Value.HasValue);
        Assert.Empty(password.Value.SecretRedactions);
        Assert.Throws<SymbolicValueException>(() => required.Value.Resolve(ValueNetworkContext.Host, (_, _, _) => ""));
        Assert.Throws<ArgumentException>(() => composition.AddParameterValue("invalid-secret-default", "not-exported", true, true));
        composition.AddContainer("api", "nginx:alpine")
            .WithEnvironment("WORKERS", defaults)
            .WithEnvironment("REQUIRED", required)
            .WithEnvironment("PASSWORD", password)
            .WithEnvironment("SECRET", secret);
        await Verifier.Verify(JsonNode.Parse(composition.ExportPublishModel())!.ToJsonString(
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), "json").UseDirectory("Snapshots");
        await session.RetireGeneration(composition);
    }

    [Fact]
    public async Task OrdinaryRunParametersRequireConfigurationRatherThanGeneratingValues()
    {
        var session = new NativeApplicationServer().OpenApplication();
        var composition = session.StartGeneration();
        var name = "missing-" + Guid.NewGuid().ToString("N");
        var exception = Assert.Throws<InvalidOperationException>(() => composition.AddConfiguredParameter(name, true, false));
        Assert.Contains($"ASPIRE_PARAMETER_{name.Replace('-', '_').ToUpperInvariant()}", exception.Message);
        Assert.Empty(composition.Model.Inspect().SecretParameters);
        await session.RetireGeneration(composition);
    }

    [Fact]
    public async Task GeneratedParametersRequireExplicitAuthoringAndHonorCharacterRequirements()
    {
        var session = new NativeApplicationServer().OpenApplication();
        var composition = session.StartGeneration();
        var parameter = composition.AddGeneratedParameter("generated",
            """{"minLength":24,"lower":true,"upper":true,"numeric":true,"special":false,"minUpper":2,"minNumeric":2}""", true);
        var value = parameter.Value.Resolve(ValueNetworkContext.Host, (_, _, _) => "");
        Assert.Equal(24, value.Length);
        Assert.All(value, character => Assert.True(char.IsAsciiLetterOrDigit(character)));
        Assert.True(value.Count(char.IsAsciiLetterUpper) >= 2);
        Assert.True(value.Count(char.IsAsciiDigit) >= 2);
        Assert.Contains(value, char.IsAsciiLetterLower);
        Assert.Throws<ArgumentException>(() => composition.AddGeneratedParameter("invalid",
            """{"minLength":1,"minUpper":2}""", true));
        await session.RetireGeneration(composition);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishDeclarationsDoNotRequireTargetPathsButLocalExecutionStillDoes(bool bindMount)
    {
        var executor = new WorkloadTestExecutor();
        var session = new NativeApplicationServer(executor).OpenApplication();
        var composition = session.StartGeneration();
        var missing = Path.Combine(AppContext.BaseDirectory, "missing-" + Guid.NewGuid().ToString("N"));
        if (bindMount)
        {
            composition.AddContainer("api", "nginx:alpine").WithBindMount(missing, "/data", true);
        }
        else
        {
            composition.AddExecutable("tool", Path.Combine(missing, "tool"), missing);
        }
        var exported = JsonNode.Parse(composition.ExportPublishModel())!;
        Assert.Single(exported["resources"]!.AsArray());
        var execution = composition.StartApplication();
        await Assert.ThrowsAsync<ArgumentException>(() => execution.WaitApplicationReady(1000));
        Assert.Empty(executor.Prepared);
        await session.RetireGeneration(composition);
    }
}
