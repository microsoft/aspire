// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONNECT001 // Integrations declare their default connect command.

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

internal static class IntegrationConnectCommand
{
    // Tracks the connect command each integration declared, so that later integration updates (for example when TLS
    // is enabled at BeforeStartEvent) can refresh it without replacing a command the app declared with
    // WithConnectCommand. Weak keys keep the table from extending resource lifetimes.
    private static readonly ConditionalWeakTable<IResource, ConnectCommandAnnotation> s_defaults = new();

    /// <summary>
    /// Declares or refreshes the integration's default connect command, unless the app declared its own.
    /// </summary>
    internal static IResourceBuilder<T> WithDefaultConnectCommand<T>(
        this IResourceBuilder<T> builder,
        string tool,
        ConnectCommandLocation location,
        ReferenceExpression command,
        string? append = null,
        string? note = null) where T : IResource
    {
        var resource = builder.Resource;
        if (resource.TryGetLastAnnotation<ConnectCommandAnnotation>(out var current) &&
            (!s_defaults.TryGetValue(resource, out var integrationDefault) || !ReferenceEquals(current, integrationDefault)))
        {
            return builder;
        }

        var annotation = new ConnectCommandAnnotation(tool, location, command)
        {
            Append = append,
            Note = note
        };
        s_defaults.AddOrUpdate(resource, annotation);

        return builder.WithAnnotation(annotation, ResourceAnnotationMutationBehavior.Replace);
    }
}
