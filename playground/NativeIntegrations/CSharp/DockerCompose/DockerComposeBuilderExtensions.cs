// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Authoring;
using static NativeIntegrations.DockerCompose.DockerComposeGenerator;

namespace NativeIntegrations.DockerCompose;

public sealed class DockerComposeEnvironmentResource(string name) : ComputeEnvironmentResource(name, "docker-compose/environment")
{
    public string? DefaultNetworkName { get; set; }
    public bool DashboardEnabled { get; set; } = true;
    internal int? DashboardHostPort { get; set; }
    internal bool DashboardForwardedHeaders { get; set; }
    internal List<Func<ComposeFileBuilder, Task>> FileCallbacks { get; } = [];
    internal List<Action<IReadOnlyDictionary<string, CapturedEnvironmentVariable>>> EnvironmentCallbacks { get; } = [];

    internal string Configuration() => new JsonObject
    {
        ["defaultNetworkName"] = DefaultNetworkName, ["dashboardEnabled"] = DashboardEnabled,
        ["dashboardHostPort"] = DashboardHostPort, ["dashboardForwardedHeaders"] = DashboardForwardedHeaders
    }.ToJsonString();
}

public sealed class DockerComposeServiceResource(
    string name, IResourceBuilder<DockerComposeEnvironmentResource> parent, DockerComposePlan plan, PublishingContext context)
{
    public string Name { get; } = name;
    public IResourceBuilder<DockerComposeEnvironmentResource> Parent { get; } = parent;

    public string Parameter(IResourceBuilder<ParameterResource> parameter)
    {
        if (!ReferenceEquals(Parent.ApplicationBuilder, parameter.ApplicationBuilder) ||
            !Strings(context.Model["parameters"]).Contains(parameter.Resource.Name, StringComparer.Ordinal))
        {
            throw new ArgumentException("Compose placeholders require a declared parameter from the same builder.", nameof(parameter));
        }
        var variable = ParameterVariable(parameter.Resource.Name);
        var parameters = plan.Inputs["parameters"]!.AsArray();
        if (Objects(parameters).Any(entry => Text(entry, "environmentVariable") == variable && Text(entry, "name") != parameter.Resource.Name))
        {
            throw new InvalidOperationException("Parameter names collide after environment-variable normalization.");
        }
        if (!Objects(parameters).Any(entry => Text(entry, "name") == parameter.Resource.Name))
        {
            if (plan.Inputs["environment"]?.AsObject().ContainsKey(variable) == true)
            {
                throw new InvalidOperationException("Parameter collides with a captured Compose deployment input.");
            }
            parameters.Add(new JsonObject { ["name"] = parameter.Resource.Name, ["environmentVariable"] = variable });
            plan.Inputs["parameters"] = new JsonArray(Objects(parameters).OrderBy(entry => Text(entry, "name"), StringComparer.Ordinal)
                .Select(entry => entry.DeepClone()).ToArray());
        }

        return Placeholder(variable);
    }
}

public sealed class ComposeDashboardBuilder(IResourceBuilder<DockerComposeEnvironmentResource> environment)
{
    public string Name => environment.Resource.Name + "-dashboard";
    public ComposeDashboardBuilder WithHostPort(int? port)
    {
        if (port is { } value)
        {
            ComposeJson.Port(value);
        }
        environment.Resource.DashboardHostPort = port;

        return this;
    }
    public ComposeDashboardBuilder WithForwardedHeaders(bool enabled = true)
    {
        environment.Resource.DashboardForwardedHeaders = enabled;

        return this;
    }
    public Uri PrimaryEndpoint => new($"http://{(environment.Resource.DashboardHostPort is null ? ServiceName(Name) : "127.0.0.1")}:{environment.Resource.DashboardHostPort ?? 18888}");
    public Uri OtlpGrpcEndpoint => new($"http://{ServiceName(Name)}:18889");
}

public static class DockerComposeBuilderExtensions
{
    private static readonly ConditionalWeakTable<DistributedApplicationBuilder, ComposeAuthoringState> s_states = new();

    public static IResourceBuilder<DockerComposeEnvironmentResource> AddDockerComposeEnvironment(
        this DistributedApplicationBuilder builder, string name)
    {
        var result = builder.AddComputeEnvironment(new DockerComposeEnvironmentResource(ComposeJson.Text(name)));
        result.WithPublishConfiguration("dockerComposeEnvironment", result.Resource.Configuration);
        if (!builder.IsPublishMode)
        {
            return result;
        }
        var state = Infrastructure(builder);
        var target = new DockerComposeEnvironment(name, new(), result, state);
        state.Targets.Add(result.Resource, target);
        Step("validate-" + name, async ctx => { await target.PlanAsync(ctx.PublishingContext).ConfigureAwait(false); },
            [], ["before-start", "publish-prereq", "deploy-prereq"]);
        Step("prepare-" + name, async ctx => { await target.PlanAsync(ctx.PublishingContext).ConfigureAwait(false); },
            ["validate-" + name], ["publish-" + name]);
        Step("publish-" + name, ctx => target.PublishAsync(ctx.PublishingContext), ["prepare-" + name], ["publish"]);
        Step("deploy-" + name, ctx => target.DeployAsync(ctx.PublishingContext), ["publish-" + name], ["deploy"]);

        return result;

        void Step(string stepName, Func<NativePipelineStepContext, Task> action, string[] dependencies, string[] requiredBy)
        {
            var step = builder.Pipeline.AddStep(stepName, action);
            step.DependsOn.AddRange(dependencies);
            step.RequiredBy.AddRange(requiredBy);
            step.Resource = name;
        }
    }

    public static IResourceBuilder<T> PublishAsDockerComposeService<T>(
        this IResourceBuilder<T> builder, DockerComposeServiceOptions options) where T : Resource
    {
        if (builder.ApplicationBuilder.IsPublishMode)
        {
            Infrastructure(builder.ApplicationBuilder);
            var json = options.ToJson();
            ValidateOptions(json);
            builder.WithPublishConfiguration("dockerCompose", () => json.ToJsonString());
        }

        return builder;
    }

    public static IResourceBuilder<T> PublishAsDockerComposeService<T>(
        this IResourceBuilder<T> builder, Action<DockerComposeServiceResource, ComposeServiceBuilder> configure) where T : Resource =>
        builder.PublishAsDockerComposeService((resource, service) => { configure(resource, service); return Task.CompletedTask; });

    public static IResourceBuilder<T> PublishAsDockerComposeService<T>(
        this IResourceBuilder<T> builder, Func<DockerComposeServiceResource, ComposeServiceBuilder, Task> configure) where T : Resource
    {
        if (builder.ApplicationBuilder.IsPublishMode)
        {
            var state = Infrastructure(builder.ApplicationBuilder);
            if (!state.ServiceCallbacks.TryGetValue(builder.Resource, out var callbacks))
            {
                state.ServiceCallbacks.Add(builder.Resource, callbacks = []);
            }
            callbacks.Add(configure);
        }

        return builder;
    }

    public static IResourceBuilder<DockerComposeEnvironmentResource> WithProperties(
        this IResourceBuilder<DockerComposeEnvironmentResource> builder, Action<DockerComposeEnvironmentResource> configure)
    {
        configure(builder.Resource);

        return builder;
    }

    public static IResourceBuilder<DockerComposeEnvironmentResource> WithDashboard(
        this IResourceBuilder<DockerComposeEnvironmentResource> builder, bool enabled = true)
    {
        builder.Resource.DashboardEnabled = enabled;

        return builder;
    }

    public static IResourceBuilder<DockerComposeEnvironmentResource> ConfigureDashboard(
        this IResourceBuilder<DockerComposeEnvironmentResource> builder, Action<ComposeDashboardBuilder> configure)
    {
        builder.Resource.DashboardEnabled = true;
        configure(new(builder));

        return builder;
    }

    public static IResourceBuilder<DockerComposeEnvironmentResource> ConfigureComposeFile(
        this IResourceBuilder<DockerComposeEnvironmentResource> builder, Action<ComposeFileBuilder> configure) =>
        builder.ConfigureComposeFile(file => { configure(file); return Task.CompletedTask; });

    public static IResourceBuilder<DockerComposeEnvironmentResource> ConfigureComposeFile(
        this IResourceBuilder<DockerComposeEnvironmentResource> builder, Func<ComposeFileBuilder, Task> configure)
    {
        builder.Resource.FileCallbacks.Add(configure);

        return builder;
    }

    public static IResourceBuilder<DockerComposeEnvironmentResource> ConfigureEnvFile(
        this IResourceBuilder<DockerComposeEnvironmentResource> builder,
        Action<IReadOnlyDictionary<string, CapturedEnvironmentVariable>> configure)
    {
        builder.Resource.EnvironmentCallbacks.Add(configure);

        return builder;
    }

    public static IResourceBuilder<DockerComposeEnvironmentResource> WithRunner(
        this IResourceBuilder<DockerComposeEnvironmentResource> builder, DockerCommandRunner runner)
    {
        if (builder.ApplicationBuilder.IsPublishMode)
        {
            Infrastructure(builder.ApplicationBuilder).Targets[builder.Resource].WithRunner(runner);
        }

        return builder;
    }

    public static string AsEnvironmentPlaceholder(this IResourceBuilder<ParameterResource> parameter, DockerComposeServiceResource service) =>
        service.Parameter(parameter);

    public static ReferenceExpression GetHostAddressExpression(this IResourceBuilder<DockerComposeEnvironmentResource> environment,
        EndpointReference endpoint)
    {
        if (!environment.ApplicationBuilder.Resources.Contains(endpoint.Resource))
        {
            throw new ArgumentException("Endpoint belongs to another builder.", nameof(endpoint));
        }

        return ReferenceExpression.Literal(ServiceName(endpoint.Resource.Name));
    }

    private static ComposeAuthoringState Infrastructure(DistributedApplicationBuilder builder) =>
        s_states.GetValue(builder, source =>
        {
            var state = new ComposeAuthoringState();
            var step = source.Pipeline.AddStep("validate-docker-compose", context =>
            {
                var resources = Objects(context.PublishingContext.Model["resources"]).ToArray();
                if (!resources.Any(resource => Text(resource, "type") == "docker-compose/environment") &&
                    (state.ServiceCallbacks.Count > 0 || resources.Any(resource => resource["configuration"]?["publish.dockerCompose"] is not null)))
                {
                    throw new InvalidOperationException("A resource uses PublishAsDockerComposeService(), but no Docker Compose environment exists. Call AddDockerComposeEnvironment().");
                }

                return Task.CompletedTask;
            });
            step.RequiredBy.AddRange(["before-start", "publish-prereq", "deploy-prereq"]);

            return state;
        });
}

internal sealed class ComposeAuthoringState
{
    internal Dictionary<Resource, List<Func<DockerComposeServiceResource, ComposeServiceBuilder, Task>>> ServiceCallbacks { get; } =
        new(ReferenceEqualityComparer.Instance);
    internal Dictionary<DockerComposeEnvironmentResource, DockerComposeEnvironment> Targets { get; } = new(ReferenceEqualityComparer.Instance);
}
