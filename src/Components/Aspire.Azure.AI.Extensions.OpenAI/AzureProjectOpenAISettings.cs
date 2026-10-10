// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Azure.Common;
using Azure.Core;

namespace Aspire.Azure.AI.Extensions.OpenAI;

/// <summary>
/// Configures an Azure AI Project OpenAI client and its Microsoft.Extensions.AI telemetry.
/// </summary>
public sealed class AzureProjectOpenAISettings : IConnectionStringSettings
{
    /// <summary>
    /// Gets or sets the HTTPS Foundry project endpoint, such as <c>https://account.services.ai.azure.com/api/projects/project</c>.
    /// </summary>
    public Uri? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets the Microsoft Entra credential. Uses managed identity in Azure and a development credential locally by default.
    /// </summary>
    public TokenCredential? Credential { get; set; }

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
        Endpoint = AzureAIProjectConnectionString.ParseEndpoint(connectionString);
    }
}
