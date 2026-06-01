// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Specifies how an HTTP command should surface the HTTP response body as command result data.
/// </summary>
public enum HttpCommandResultMode
{
    /// <summary>
    /// Do not capture the HTTP response body as command result data.
    /// </summary>
    None,

    /// <summary>
    /// Infer the command result format from the HTTP response content type.
    /// </summary>
    Auto,

    /// <summary>
    /// Return the HTTP response body as JSON command result data.
    /// </summary>
    Json,

    /// <summary>
    /// Return the HTTP response body as plain text command result data.
    /// </summary>
    Text
}

/// <summary>
/// Optional configuration for resource HTTP commands added with <see cref="ResourceBuilderExtensions.WithHttpCommand{TResource}(Aspire.Hosting.ApplicationModel.IResourceBuilder{TResource}, string, string, string?, string?, Aspire.Hosting.ApplicationModel.HttpCommandOptions?)"/>.
/// </summary>
public class HttpCommandOptions : CommandOptions
{
    internal static new HttpCommandOptions Default { get; } = new();

    /// <summary>
    /// Gets or sets a callback that selects the HTTP endpoint to send the request to when the command is invoked.
    /// </summary>
    public Func<EndpointReference>? EndpointSelector { get; set; }

    /// <summary>
    /// Gets or sets the HTTP method to use when sending the request.
    /// </summary>
    public HttpMethod? Method { get; set; }

    /// <summary>
    /// Gets or sets the name of the HTTP client to use when creating it via <see cref="IHttpClientFactory.CreateClient(string)"/>.
    /// </summary>
    public string? HttpClientName { get; set; }

    /// <summary>
    /// Gets or sets a callback to be invoked to configure the request before it is sent.
    /// </summary>
    public Func<HttpCommandRequestContext, Task>? PrepareRequest { get; set; }

    /// <summary>
    /// Gets or sets a callback to be invoked after the response is received to determine the result of the command invocation.
    /// </summary>
    public Func<HttpCommandResultContext, Task<ExecuteCommandResult>>? GetCommandResult { get; set; }

    /// <summary>
    /// Gets or sets how the HTTP response content should be returned as command result data
    /// when <see cref="GetCommandResult"/> is not specified. The default is <see cref="HttpCommandResultMode.None"/>.
    /// </summary>
    public HttpCommandResultMode ResultMode { get; set; }
}

/// <summary>
/// ATS-friendly configuration for resource HTTP commands.
/// </summary>
[AspireDto]
internal sealed class HttpCommandExportOptions
{
    /// <summary>
    /// Optional description of the command, to be shown in the UI.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// When a confirmation message is specified, the UI will prompt with an OK/Cancel dialog before starting the command.
    /// </summary>
    public string? ConfirmationMessage { get; set; }

    /// <summary>
    /// The icon name for the command.
    /// </summary>
    public string? IconName { get; set; }

    /// <summary>
    /// The icon variant.
    /// </summary>
    public IconVariant? IconVariant { get; set; }

    /// <summary>
    /// A flag indicating whether the command is highlighted in the UI.
    /// </summary>
    public bool IsHighlighted { get; set; }

    /// <summary>
    /// Gets or sets the command name.
    /// </summary>
    public string? CommandName { get; set; }

    /// <summary>
    /// Gets or sets the HTTP endpoint name to send the request to when the command is invoked.
    /// </summary>
    public string? EndpointName { get; set; }

    /// <summary>
    /// Gets or sets the HTTP method name to use when sending the request.
    /// </summary>
    public string? MethodName { get; set; }

    /// <summary>
    /// Gets or sets how the HTTP response content should be returned as command result data.
    /// </summary>
    public HttpCommandResultMode ResultMode { get; set; }
}
