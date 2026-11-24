// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Specifies where the command described by a <see cref="ConnectCommandAnnotation"/> runs.
/// </summary>
[Experimental("ASPIRECONNECT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public enum ConnectCommandLocation
{
    /// <summary>
    /// The client runs inside the resource's container. The command is a shell script that the container's
    /// <c>sh</c> interprets, so it can read credentials from the container's own environment variables.
    /// </summary>
    Container,

    /// <summary>
    /// The client runs on the host, for example because the container image has no client. The command is a host
    /// command line that typically reaches the resource through its published endpoint.
    /// </summary>
    Host
}

/// <summary>
/// Describes the command that reaches a resource with its own client, such as <c>psql</c> or <c>redis-cli</c>.
/// </summary>
/// <remarks>
/// <para>
/// Tooling such as <c>aspire describe --format json</c> and the Aspire MCP server shows the resolved command so that
/// people and agents can query a dependency directly. The command ends where the client's own arguments go.
/// </para>
/// <para>
/// For <see cref="ConnectCommandLocation.Container"/>, <see cref="Command"/> is a shell script that runs in the
/// container. Tooling wraps it as <c>docker exec &lt;container&gt; sh -c '&lt;script&gt; "$@"' sh</c> (using the
/// configured container runtime), so appended arguments are forwarded to the client. Read credentials from the
/// container's environment variables, for example <c>"$POSTGRES_PASSWORD"</c>, so that only variable names are shown.
/// </para>
/// <para>
/// For <see cref="ConnectCommandLocation.Host"/>, <see cref="Command"/> is the complete host command line, usually
/// referencing the resource's published endpoint.
/// </para>
/// <para>
/// The command is displayed in tooling output, so it must not contain secret values. It can reference only endpoint
/// values, such as <see cref="EndpointReference"/> or <see cref="EndpointReferenceExpression"/>, and literal text; a
/// command that references other values, such as parameters or connection strings, is not shown. Endpoint target
/// ports must be fixed, and browser origins are not supported.
/// </para>
/// </remarks>
[Experimental("ASPIRECONNECT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ConnectCommandAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Creates a connect command description.
    /// </summary>
    /// <param name="tool">The name of the client tool the command runs, such as <c>psql</c>.</param>
    /// <param name="location">Where the command runs.</param>
    /// <param name="command">The command, ending where the client's own arguments go.</param>
    public ConnectCommandAnnotation(string tool, ConnectCommandLocation location, ReferenceExpression command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentNullException.ThrowIfNull(command);

        if (!Enum.IsDefined(location))
        {
            throw new ArgumentOutOfRangeException(nameof(location), location, "The connect command location is not supported.");
        }

        Tool = tool;
        Location = location;
        Command = command;
    }

    /// <summary>
    /// Gets the name of the client tool the command runs, such as <c>psql</c>.
    /// </summary>
    public string Tool { get; }

    /// <summary>
    /// Gets where the command runs.
    /// </summary>
    public ConnectCommandLocation Location { get; }

    /// <summary>
    /// Gets the command, ending where the client's own arguments go.
    /// </summary>
    public ReferenceExpression Command { get; }

    /// <summary>
    /// Gets a description of what to append to the command, such as <c>-c '&lt;sql&gt;'</c>.
    /// </summary>
    public string? Append { get; init; }

    /// <summary>
    /// Gets an optional note about using the command, such as a client requirement on the host.
    /// </summary>
    public string? Note { get; init; }
}
