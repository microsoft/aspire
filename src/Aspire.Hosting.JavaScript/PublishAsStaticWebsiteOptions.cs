// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.JavaScript;

/// <summary>
/// Options for configuring the static website publish mode.
/// </summary>
public class PublishAsStaticWebsiteOptions
{
    /// <summary>
    /// Gets or sets the relative path to the directory containing the built static files.
    /// Defaults to <c>dist</c>. Some frameworks use a different output directory,
    /// for example Angular uses <c>dist/browser</c>.
    /// </summary>
    public string OutputPath { get; set; } = "dist";

    /// <summary>
    /// Gets or sets whether to remove the API path prefix before forwarding to the backend.
    /// For example, with <c>apiPath="/api"</c> and <c>StripPrefix=true</c>, a request to
    /// <c>/api/weatherforecast</c> is forwarded as <c>/weatherforecast</c>.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool StripPrefix { get; set; }

    /// <summary>
    /// Gets or sets the name of a specific endpoint on the API target resource to proxy to.
    /// When <see langword="null"/>, YARP uses service discovery to resolve the appropriate endpoint,
    /// preferring HTTPS when available.
    /// </summary>
    public string? TargetEndpointName { get; set; }
}
