// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Client;

namespace Aspire.Hosting.Native.Authoring;

/// <summary>Provides generic hosting composition shared by every authored integration.</summary>
public static class ResourceBuilderExtensions
{
    /// <summary>Associates native symbolic connection syntax with an authored resource.</summary>
    /// <typeparam name="T">The authored resource type.</typeparam>
    /// <param name="resource">The connection owner.</param>
    /// <param name="connection">Native symbolic syntax, not a resolved connection string.</param>
    /// <returns>The same fluent builder.</returns>
    public static IResourceBuilder<T> WithConnectionString<T>(this IResourceBuilder<T> resource, ReferenceExpression connection) where T : Resource
    {
        resource.ApplicationBuilder.Record(context =>
        {
            context.BindConnection(resource.Resource, connection);
            return Task.CompletedTask;
        });

        return resource;
    }
    private static void RequireSameBuilder<T, TOther>(IResourceBuilder<T> resource, IResourceBuilder<TOther> other)
        where T : Resource where TOther : Resource
    {
        if (!ReferenceEquals(resource.ApplicationBuilder, other.ApplicationBuilder))
        {
            throw new ArgumentException("The reference belongs to another authoring plan.");
        }
    }
    /// <summary>Gets symbolic parameter syntax.</summary>
    /// <param name="parameter">The parameter in this transport plan.</param>
    /// <returns>Native-materialized parameter syntax.</returns>
    public static ReferenceExpression AsReference(this IResourceBuilder<ParameterResource> parameter) =>
        new(context => context.Parameter(parameter.Resource));
    /// <summary>Records an environment value without resolving it.</summary>
    /// <typeparam name="T">The authored resource type.</typeparam>
    /// <param name="resource">The environment recipient.</param>
    /// <param name="name">The environment variable name.</param>
    /// <param name="value">Native symbolic syntax.</param>
    /// <returns>The same fluent builder.</returns>
    public static IResourceBuilder<T> WithEnvironment<T>(this IResourceBuilder<T> resource, string name, ReferenceExpression value) where T : Resource
    {
        resource.ApplicationBuilder.Record(async context => await context.Declaration(resource.Resource)
            .WithEnvironmentAsync(name, await context.Value(value).ConfigureAwait(false), context.Token).ConfigureAwait(false));

        return resource;
    }
    /// <summary>Records an environment parameter reference.</summary>
    /// <typeparam name="T">The authored resource type.</typeparam>
    /// <param name="resource">The environment recipient.</param>
    /// <param name="name">The environment variable name.</param>
    /// <param name="value">A parameter in the same transport plan.</param>
    /// <returns>The same fluent builder.</returns>
    public static IResourceBuilder<T> WithEnvironment<T>(this IResourceBuilder<T> resource, string name,
        IResourceBuilder<ParameterResource> value) where T : Resource
    {
        RequireSameBuilder(resource, value);

        return resource.WithEnvironment(name, value.AsReference());
    }
    /// <summary>Records structured arguments; no shell expansion is implied.</summary>
    /// <typeparam name="T">The authored resource type.</typeparam>
    /// <param name="resource">The argument recipient.</param>
    /// <param name="arguments">Ordered literal or native symbolic arguments.</param>
    /// <returns>The same fluent builder.</returns>
    public static IResourceBuilder<T> WithArgs<T>(this IResourceBuilder<T> resource, params ReferenceExpression[] arguments) where T : Resource
    {
        foreach (var argument in arguments)
        {
            resource.ApplicationBuilder.Record(async context => await context.Declaration(resource.Resource)
                .WithArgumentAsync(await context.Value(argument).ConfigureAwait(false), context.Token).ConfigureAwait(false));
        }

        return resource;
    }
    /// <summary>Declares a named endpoint without allocating a host port.</summary>
    /// <typeparam name="T">The workload resource type.</typeparam>
    /// <param name="resource">The endpoint owner.</param>
    /// <param name="name">The endpoint name.</param>
    /// <param name="targetPort">The container port.</param>
    /// <param name="scheme">The URI scheme for native endpoint references.</param>
    /// <returns>The same fluent builder.</returns>
    public static IResourceBuilder<T> WithEndpoint<T>(this IResourceBuilder<T> resource, string name, int targetPort,
        string scheme = "tcp") where T : Resource
    {
        resource.ApplicationBuilder.Record(context => context.Declaration(resource.Resource).WithEndpointAsync(new()
        {
            Name = name, Protocol = "TCP", UriScheme = scheme, TargetPort = targetPort, HostPort = 0, EnvironmentVariable = ""
        }, context.Token));

        return resource;
    }
    /// <summary>Gets deferred endpoint syntax.</summary>
    /// <typeparam name="T">The authored resource type.</typeparam>
    /// <param name="resource">The endpoint owner.</param>
    /// <param name="name">The declared endpoint name.</param>
    /// <returns>Native endpoint property syntax.</returns>
    public static EndpointReference GetEndpoint<T>(this IResourceBuilder<T> resource, string name) where T : Resource =>
        new(resource.Resource, name);
    /// <summary>Configures a generic container entrypoint.</summary>
    /// <typeparam name="T">The container resource type.</typeparam>
    /// <param name="resource">The container declaration.</param>
    /// <param name="entrypoint">The executable inside the container.</param>
    /// <returns>The same fluent builder.</returns>
    public static IResourceBuilder<T> WithEntrypoint<T>(this IResourceBuilder<T> resource, string entrypoint) where T : ContainerResource
    {
        resource.ApplicationBuilder.Record(context => context.Declaration(resource.Resource).WithEntrypointAsync(entrypoint, context.Token));

        return resource;
    }
    /// <summary>Mounts a retained named data volume.</summary>
    /// <typeparam name="T">The authored resource type.</typeparam>
    /// <param name="resource">The mount recipient.</param>
    /// <param name="name">The retained physical volume name.</param>
    /// <param name="target">The container mount target.</param>
    /// <returns>The same fluent builder.</returns>
    public static IResourceBuilder<T> WithVolume<T>(this IResourceBuilder<T> resource, string name, string target) where T : Resource
    {
        resource.ApplicationBuilder.Record(context => context.Declaration(resource.Resource).WithVolumeAsync(name, target, false, context.Token));

        return resource;
    }
    /// <summary>Injects the producer's native connection reference into the consumer, independently of explicit waits.</summary>
    /// <typeparam name="T">The consuming resource type.</typeparam>
    /// <typeparam name="TProducer">The connection-owning resource type.</typeparam>
    /// <param name="consumer">The environment recipient.</param>
    /// <param name="producer">The connection owner in the same transport plan.</param>
    /// <returns>The consumer builder.</returns>
    public static IResourceBuilder<T> WithReference<T, TProducer>(this IResourceBuilder<T> consumer,
        IResourceBuilder<TProducer> producer) where T : Resource where TProducer : Resource
    {
        RequireSameBuilder(consumer, producer);
        consumer.ApplicationBuilder.Record(async context => await context.Declaration(consumer.Resource)
            .WithReferenceAsync($"ConnectionStrings__{producer.Resource.Name}",
                await context.Value(context.Connection(producer.Resource)).ConfigureAwait(false), context.Token).ConfigureAwait(false));

        return consumer;
    }
    /// <summary>Adds an explicit native readiness dependency without injecting environment values.</summary>
    /// <typeparam name="T">The waiting resource type.</typeparam>
    /// <typeparam name="TDependency">The readiness dependency type.</typeparam>
    /// <param name="consumer">The waiting resource.</param>
    /// <param name="dependency">The readiness dependency in the same transport plan.</param>
    /// <returns>The consumer builder.</returns>
    public static IResourceBuilder<T> WaitFor<T, TDependency>(this IResourceBuilder<T> consumer,
        IResourceBuilder<TDependency> dependency) where T : Resource where TDependency : Resource
    {
        RequireSameBuilder(consumer, dependency);
        consumer.ApplicationBuilder.Record(context => context.Declaration(consumer.Resource)
            .WaitForAsync(context.Declaration(dependency.Resource), context.Token));

        return consumer;
    }
    /// <summary>Declares authenticated observational health, with native-owned captured command inputs.</summary>
    /// <typeparam name="T">The authored resource type.</typeparam>
    /// <param name="resource">The health owner.</param>
    /// <param name="command">The integration-owned executable and native symbolic inputs.</param>
    /// <returns>The same fluent builder.</returns>
    public static IResourceBuilder<T> WithHealthCheck<T>(this IResourceBuilder<T> resource, ExecutableCommand command) where T : Resource
    {
        var captured = command.Copy();
        resource.ApplicationBuilder.Record(async context =>
        {
            foreach (var (name, value) in captured.Environment)
            {
                await context.Declaration(resource.Resource).SetSymbolicConfigurationAsync($"health.env.{name}",
                    await context.Value(value).ConfigureAwait(false), context.Token).ConfigureAwait(false);
            }
            await context.Declaration(resource.Resource).WithLifecycleExecutableAsync("health", captured.Launch(),
                captured.Bindings(), context.Token).ConfigureAwait(false);
        });

        return resource;
    }
}

/// <summary>Contains an integration-owned one-shot executable declaration, not a running process.</summary>
/// <param name="executablePath">The absolute host executable path.</param>
/// <param name="workingDirectory">The absolute host working directory.</param>
/// <param name="arguments">Ordered literal tool arguments; secrets belong in symbolic environment inputs.</param>
public sealed class ExecutableCommand(string executablePath, string workingDirectory, params string[] arguments)
{
    internal Dictionary<string, ReferenceExpression> Environment { get; } = new(StringComparer.Ordinal);
    internal ExecutableCommand Copy()
    {
        var copy = new ExecutableCommand(executablePath, workingDirectory, arguments.ToArray());
        foreach (var input in Environment)
        {
            copy.Environment.Add(input.Key, input.Value);
        }

        return copy;
    }
    /// <summary>Adds a native-resolved input to a health command.</summary>
    /// <param name="name">The tool's environment variable name.</param>
    /// <param name="value">Native symbolic input syntax.</param>
    /// <returns>The same command builder.</returns>
    public ExecutableCommand WithEnvironment(string name, ReferenceExpression value)
    {
        Environment.Add(name, value);

        return this;
    }
    internal ExecutableWorkloadLaunch Launch() => new()
    {
        ExecutablePath = executablePath, WorkingDirectory = workingDirectory,
        Arguments = arguments, Environment = [], Endpoints = []
    };
    internal SymbolicLaunchBindings Bindings() => new()
    {
        Environment = Environment.Select(input => new SymbolicLaunchEnvironment
        {
            Name = input.Key, ConfigurationKey = $"health.env.{input.Key}"
        }).ToArray(),
        Arguments = []
    };
}
