// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

#pragma warning disable ASPIREINTERACTION001 // HTTP command arguments intentionally reuse the experimental interaction input model.

/// <summary>
/// Context passed to callback to configure <see cref="HttpRequestMessage"/> when using
/// <see cref="ResourceBuilderExtensions.WithHttpCommand{TResource}(IResourceBuilder{TResource}, string, string, string?, string?, HttpCommandOptions?)"/>
/// or <see cref="ResourceBuilderExtensions.WithHttpCommand{TResource}(IResourceBuilder{TResource}, string, string, Func{EndpointReference}?, string?, HttpCommandOptions?)"/>.
/// </summary>
public sealed class HttpCommandRequestContext
{
    /// <summary>
    /// The service provider.
    /// </summary>
    [Obsolete("Use Services instead.")]
    public IServiceProvider ServiceProvider
    {
        get => Services;
        init => Services = value;
    }

    /// <summary>
    /// The service provider.
    /// </summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>
    /// The name of the resource the command was configured on.
    /// </summary>
    public required string ResourceName { get; init; }

    /// <summary>
    /// The endpoint the request is targeting.
    /// </summary>
    public required EndpointReference Endpoint { get; init; }

    /// <summary>
    /// The cancellation token.
    /// </summary>
    public required CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// The HTTP client to use for the request.
    /// </summary>
    public required HttpClient HttpClient { get; init; }

    /// <summary>
    /// Gets the invocation arguments supplied by the client when the command is executed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The collection contains the arguments described by the command's <see cref="CommandOptions.Arguments"/> with their
    /// submitted values populated. CLI positional arguments are mapped by declaration order. Dashboard, MCP, and other
    /// named-payload clients are mapped by <see cref="InteractionInput.Name"/>.
    /// </para>
    /// </remarks>
    public InteractionInputCollection Arguments { get; init; } = new([]);

    /// <summary>
    /// The HTTP request message.
    /// </summary>
    public required HttpRequestMessage Request { get; init; }
}

/// <summary>
/// Provides context for HTTP command prepare-request callbacks in polyglot app hosts.
/// </summary>
[AspireExport(ExposeProperties = true)]
internal sealed class HttpCommandPrepareRequestContext
{
    /// <summary>
    /// The name of the resource the command was configured on.
    /// </summary>
    public required string ResourceName { get; init; }

    /// <summary>
    /// The endpoint the request is targeting.
    /// </summary>
    public required EndpointReference Endpoint { get; init; }

    /// <summary>
    /// The cancellation token.
    /// </summary>
    public required CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Gets the invocation arguments supplied by the client when the command is executed.
    /// </summary>
    public required InteractionInputCollection Arguments { get; init; }
}

/// <summary>
/// Context passed to callback to configure <see cref="ExecuteCommandResult"/> when using
/// <see cref="ResourceBuilderExtensions.WithHttpCommand{TResource}(IResourceBuilder{TResource}, string, string, string?, string?, HttpCommandOptions?)"/>
/// or <see cref="ResourceBuilderExtensions.WithHttpCommand{TResource}(IResourceBuilder{TResource}, string, string, Func{EndpointReference}?, string?, HttpCommandOptions?)"/>.
/// </summary>
public sealed class HttpCommandResultContext
{
    /// <summary>
    /// The service provider.
    /// </summary>
    [Obsolete("Use Services instead.")]
    public IServiceProvider ServiceProvider
    {
        get => Services;
        init => Services = value;
    }

    /// <summary>
    /// The service provider.
    /// </summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>
    /// The name of the resource the command was configured on.
    /// </summary>
    public required string ResourceName { get; init; }

    /// <summary>
    /// The endpoint the request is targeting.
    /// </summary>
    public required EndpointReference Endpoint { get; init; }

    /// <summary>
    /// The cancellation token.
    /// </summary>
    public required CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// The HTTP client that was used for the request.
    /// </summary>
    public required HttpClient HttpClient { get; init; }

    /// <summary>
    /// Gets the invocation arguments supplied by the client when the command is executed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The collection contains the arguments described by the command's <see cref="CommandOptions.Arguments"/> with their
    /// submitted values populated. CLI positional arguments are mapped by declaration order. Dashboard, MCP, and other
    /// named-payload clients are mapped by <see cref="InteractionInput.Name"/>.
    /// </para>
    /// </remarks>
    public InteractionInputCollection Arguments { get; init; } = new([]);

    /// <summary>
    /// The HTTP response message.
    /// </summary>
    public required HttpResponseMessage Response { get; init; }
}

#pragma warning restore ASPIREINTERACTION001
