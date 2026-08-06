// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Go;

/// <summary>
/// Configures the headless Delve debug server used for remote debugging of a Go application.
/// </summary>
/// <remarks>
/// <para>
/// The server listens on the loopback interface. By default, it accepts a single debugger client
/// and relies on Delve's default same-user connection policy.
/// </para>
/// <para>
/// Set <see cref="AcceptMultiClient"/> only when the server must remain available after a debugger
/// disconnects or when multiple debugger clients need to attach.
/// </para>
/// </remarks>
/// <example>
/// Configure a Delve server that continues the application immediately and accepts multiple debugger clients:
/// <code lang="csharp">
/// builder.AddGoApp("api", "../go-api")
///        .WithDelveServer(new DelveServerOptions
///        {
///            AcceptMultiClient = true,
///            ContinueOnStart = true
///        });
/// </code>
/// </example>
[AspireDto]
public sealed class DelveServerOptions
{
    /// <summary>
    /// Gets the TCP port on which Delve listens. The default is <c>2345</c>.
    /// </summary>
    public int Port { get; init; } = 2345;

    /// <summary>
    /// Gets a value indicating whether Delve accepts multiple debugger clients.
    /// The default is <see langword="false"/>.
    /// </summary>
    public bool AcceptMultiClient { get; init; }

    /// <summary>
    /// Gets a value indicating whether Delve allows connections only from the same operating system user.
    /// When <see langword="null"/>, Delve's default same-user policy is used.
    /// </summary>
    public bool? OnlySameUser { get; init; }

    /// <summary>
    /// Gets a value indicating whether Delve continues the application immediately after startup.
    /// The default is <see langword="false"/>.
    /// </summary>
    public bool ContinueOnStart { get; init; }

    /// <summary>
    /// Gets a value indicating whether Delve server logging is enabled.
    /// The default is <see langword="false"/>.
    /// </summary>
    public bool Log { get; init; }

    /// <summary>
    /// Gets the Delve logging components enabled when <see cref="Log"/> is <see langword="true"/>.
    /// When <see langword="null"/> or empty, Delve uses its default logging components.
    /// </summary>
    public string? LogOutput { get; init; }
}
