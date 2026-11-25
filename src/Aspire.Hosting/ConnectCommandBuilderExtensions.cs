// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Provides methods for describing how to reach a resource with its own client.
/// </summary>
public static class ConnectCommandBuilderExtensions
{
    /// <summary>
    /// Describes the command that reaches the resource with its own client. Repeated calls replace the command.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="tool">The name of the client tool the command runs, such as <c>psql</c>.</param>
    /// <param name="location">
    /// Where the command runs. For <see cref="ConnectCommandLocation.Container"/>, <paramref name="command"/> is a shell
    /// script that runs in the resource's container. For <see cref="ConnectCommandLocation.Host"/>, it is a host
    /// command line.
    /// </param>
    /// <param name="command">
    /// The command, ending where the client's own arguments go. It is displayed by tooling, so it can reference only
    /// literal text and the resource's own endpoint values, never secrets. Read credentials from the container's environment
    /// variables instead.
    /// </param>
    /// <param name="append">A description of what to append to the command, such as <c>-c '&lt;sql&gt;'</c>.</param>
    /// <param name="note">An optional note about using the command.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// The resolved command appears as the <c>connect</c> entry of the resource in <c>aspire describe --format json</c>
    /// and in the Aspire MCP server's resource listing. A container command runs with <c>sh -c</c> in the resource's
    /// container through the configured container runtime, and arguments appended by the caller are forwarded to it.
    /// </remarks>
    /// <example>
    /// <code>
    /// var nats = builder.AddContainer("nats", "nats")
    ///     .WithEndpoint(targetPort: 4222, name: "tcp");
    ///
    /// nats.WithConnectCommand(
    ///     "nats",
    ///     ConnectCommandLocation.Host,
    ///     ReferenceExpression.Create($"nats --server nats://localhost:{nats.GetEndpoint("tcp").Property(EndpointProperty.Port)}"),
    ///     append: "&lt;command&gt;, for example server info");
    /// </code>
    /// </example>
    [Experimental("ASPIRECONNECT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport]
    public static IResourceBuilder<T> WithConnectCommand<T>(
        this IResourceBuilder<T> builder,
        string tool,
        ConnectCommandLocation location,
        ReferenceExpression command,
        string? append = null,
        string? note = null) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithAnnotation(new ConnectCommandAnnotation(tool, location, command)
        {
            Append = append,
            Note = note
        }, ResourceAnnotationMutationBehavior.Replace);
    }
}
