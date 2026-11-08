// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data.Common;
using Aspire.Azure.Common;
using Azure.Core;

namespace Aspire.Azure.AI.Extensions.OpenAI;

/// <summary>
/// Configures an Azure OpenAI account client and its Microsoft.Extensions.AI telemetry.
/// </summary>
public sealed class AzureOpenAISettings : IConnectionStringSettings
{
    /// <summary>
    /// Gets or sets the HTTPS Azure OpenAI account endpoint or its <c>/openai/v1/</c> endpoint.
    /// </summary>
    public Uri? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets the API key. When absent, the client uses Microsoft Entra authentication.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>
    /// Gets or sets the Microsoft Entra credential. Uses managed identity in Azure and a development credential locally by default.
    /// </summary>
    public TokenCredential? Credential { get; set; }

    /// <summary>
    /// Gets or sets the Microsoft Entra token scope. Defaults to <c>https://ai.azure.com/.default</c>.
    /// </summary>
    public string TokenScope { get; set; } = "https://ai.azure.com/.default";

    /// <summary>
    /// Gets or sets whether tracing is disabled. The default is <see langword="false"/>.
    /// </summary>
    public bool DisableTracing { get; set; }

    /// <summary>
    /// Gets or sets whether Microsoft.Extensions.AI metrics are disabled. The default is <see langword="false"/>.
    /// </summary>
    public bool DisableMetrics { get; set; }

    /// <summary>
    /// Gets or sets whether Microsoft.Extensions.AI telemetry includes sensitive message content.
    /// Defaults to <see langword="false"/> unless <c>OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT</c> is true.
    /// </summary>
    public bool EnableSensitiveTelemetryData { get; set; } = TelemetryHelpers.EnableSensitiveDataDefault;

    void IConnectionStringSettings.ParseConnectionString(string? connectionString)
    {
        // Accept a bare account URI or "Endpoint=https://account.openai.azure.com/;Deployment=chat;Key=...".
        // The existing chat/embedding helpers read Deployment or Model independently.
        if (Uri.TryCreate(connectionString, UriKind.Absolute, out var endpoint))
        {
            Endpoint = endpoint;
            return;
        }

        var values = new DbConnectionStringBuilder { ConnectionString = connectionString };
        if (!values.TryGetValue("Endpoint", out var value) ||
            !Uri.TryCreate(value.ToString(), UriKind.Absolute, out endpoint))
        {
            throw new ArgumentException("The Azure OpenAI connection string must contain an absolute Endpoint URI.", nameof(connectionString));
        }

        Endpoint = endpoint;
        if (values.TryGetValue("Key", out var key))
        {
            Key = key.ToString();
        }
    }

    internal static Uri GetV1Endpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps ||
            endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0 ||
            endpoint.AbsolutePath is not ("/" or "/openai/v1" or "/openai/v1/"))
        {
            throw new ArgumentException("The Azure OpenAI endpoint must be an absolute HTTPS account URI or /openai/v1/ URI without user information, query, or fragment.", nameof(endpoint));
        }

        // The standard SDK uses v1 routes, unlike AzureOpenAIClient's deployment-specific routes.
        // https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/openai/Azure.AI.OpenAI/migration-guidance.md
        return new UriBuilder(endpoint) { Path = "/openai/v1/" }.Uri;
    }
}
