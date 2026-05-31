// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Agents;

/// <summary>
/// Describes agent-specific metadata for a resource.
/// </summary>
public sealed class AgentResourceAnnotation : IResourceAnnotation, IResourceWithReferenceAnnotation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AgentResourceAnnotation"/> class.
    /// </summary>
    /// <param name="protocols">The agent protocols supported by the resource.</param>
    /// <param name="customPath">The custom protocol path, when one is configured.</param>
    /// <param name="a2AInvocationMode">The invocation mode used by dashboard commands for A2A protocols.</param>
    public AgentResourceAnnotation(IReadOnlySet<AgentProtocol> protocols, string? customPath, A2AInvocationMode a2AInvocationMode)
    {
        ArgumentNullException.ThrowIfNull(protocols);

        Protocols = protocols;
        CustomPath = customPath;
        A2AInvocationMode = a2AInvocationMode;
    }

    /// <summary>
    /// Gets the agent protocols supported by the resource.
    /// </summary>
    public IReadOnlySet<AgentProtocol> Protocols { get; }

    /// <summary>
    /// Gets the custom protocol path configured for the agent.
    /// </summary>
    public string? CustomPath { get; }

    /// <summary>
    /// Gets the invocation mode used by dashboard commands for A2A protocols.
    /// </summary>
    public A2AInvocationMode A2AInvocationMode { get; }

    bool IResourceWithReferenceAnnotation.CanApplyReference(IResource source)
    {
        return source is IResourceWithEndpoints && Protocols.Any(AgentResourceBuilderExtensions.IsA2AProtocol);
    }

    IResourceBuilder<TDestination> IResourceWithReferenceAnnotation.WithReference<TDestination>(
        IResourceBuilder<TDestination> builder,
        IResource source,
        string referenceName)
    {
        var sourceWithEndpoints = (IResourceWithEndpoints)source;

        builder.WithReferenceRelationship(source);

        return builder.WithEnvironment(context =>
        {
            context.Resource.TryGetLastAnnotation<ReferenceEnvironmentInjectionAnnotation>(out var injectionAnnotation);
            var flags = injectionAnnotation?.Flags ?? ReferenceEnvironmentInjectionFlags.All;
            if (!flags.HasFlag(ReferenceEnvironmentInjectionFlags.Endpoints))
            {
                return;
            }

            var network = context.Resource.IsContainer()
                ? KnownNetworkIdentifiers.DefaultAspireContainerNetwork
                : KnownNetworkIdentifiers.LocalhostNetwork;
            var endpoint = GetDefaultAgentEndpoint(sourceWithEndpoints, network);
            var envVarName = AgentResourceBuilderExtensions.GetAgentCardEnvironmentVariableName(referenceName);
            context.EnvironmentVariables[envVarName] = AgentResourceBuilderExtensions.CreateA2AAgentCardUrl(endpoint, AgentResourceBuilderExtensions.GetA2AAgentCardPath(this));
        });
    }

    private static EndpointReference GetDefaultAgentEndpoint(IResourceWithEndpoints source, NetworkIdentifier network)
    {
        var endpointName = source.Annotations
            .OfType<EndpointAnnotation>()
            .Where(e => e.UriScheme is "http" or "https")
            .OrderByDescending(e => string.Equals(e.UriScheme, "https", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Name)
            .FirstOrDefault() ?? "http";

        return new EndpointReference(source, endpointName, network);
    }
}
