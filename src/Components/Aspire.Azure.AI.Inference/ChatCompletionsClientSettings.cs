// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data.Common;
using Aspire.Azure.Common;
using Azure.Core;

namespace Aspire.Azure.AI.Inference;

/// <summary>
/// Represents configuration settings for Azure AI Chat Completions client.
/// </summary>
public sealed class ChatCompletionsClientSettings : IConnectionStringSettings
{
    /// <summary>
    /// Gets or sets the connection string used to connect to the AI Foundry account.
    /// </summary>
    /// <remarks>
    /// If <see cref="ConnectionString"/> is set, it overrides <see cref="Endpoint"/>, <see cref="DeploymentName"/> and <see cref="TokenCredential"/>.
    /// </remarks>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets the name of the AI model deployment to use for chat completions.
    /// </summary>
    public string? DeploymentName { get; set; }

    /// <summary>
    /// Gets or sets the endpoint URI for the Azure AI service.
    /// </summary>
    public Uri? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets the token credential used for Azure authentication.
    /// </summary>
    public TokenCredential? TokenCredential { get; set; }

    /// <summary>
    /// Gets or sets the API key used for authentication with the Azure AI service.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>
    /// Gets or sets a boolean value that indicates whether the health check is disabled or not.
    /// </summary>
    /// <value>
    /// The default value is <see langword="false"/>.
    /// </value>
    public bool DisableHealthChecks { get; set; }

    /// <summary>
    /// Gets or sets a boolean value that indicates whether the OpenTelemetry metrics are enabled or not.
    /// </summary>
    /// <remarks>
    /// Telemetry is recorded by Microsoft.Extensions.AI.
    /// </remarks>
    /// <value>
    /// The default value is <see langword="false"/>.
    /// </value>
    public bool DisableMetrics { get; set; }

    /// <summary>
    /// Gets or sets a boolean value that indicates whether the OpenTelemetry tracing is disabled or not.
    /// </summary>
    /// <remarks>
    /// Telemetry is recorded by Microsoft.Extensions.AI.
    /// </remarks>
    /// <value>
    /// The default value is <see langword="false"/>.
    /// </value>
    public bool DisableTracing { get; set; }

    /// <summary>
    /// Gets or sets a boolean value indicating whether potentially sensitive information should be included in telemetry.
    /// </summary>
    /// <value>
    /// <see langword="true"/> if potentially sensitive information should be included in telemetry;
    /// <see langword="false"/> if telemetry shouldn't include raw inputs and outputs.
    /// The default value is <see langword="false"/>, unless the <c>OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT</c>
    /// environment variable is set to "true" (case-insensitive).
    /// </value>
    /// <remarks>
    /// By default, telemetry includes metadata, such as token counts, but not raw inputs
    /// and outputs, such as message content, function call arguments, and function call results.
    /// The default value can be overridden by setting the <c>OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT</c>
    /// environment variable to "true". Explicitly setting this property will override the environment variable.
    /// </remarks>
    public bool EnableSensitiveTelemetryData { get; set; } = TelemetryHelpers.EnableSensitiveDataDefault;

    /// <summary>
    /// Parses a connection string and populates the settings properties.
    /// </summary>
    /// <param name="connectionString">The connection string containing configuration values.</param>
    /// <remarks>
    /// The connection string can contain the following keys:
    /// - Deployment: The deployment name (preferred)
    /// - DeploymentId: The deployment ID (legacy, for backward compatibility)
    /// - Model: The model name (used by GitHub Models)
    /// - Endpoint: The service endpoint URI
    /// - Key: The API key for authentication
    /// Note: Only one of Deployment, DeploymentId, or Model should be specified.
    /// </remarks>
    void IConnectionStringSettings.ParseConnectionString(string? connectionString)
    {
        var connectionBuilder = new DbConnectionStringBuilder
        {
            ConnectionString = connectionString
        };

        // Check for deployment/model keys and ensure only one is provided
        var deploymentKeys = new List<string>();
        if (connectionBuilder.ContainsKey("Deployment"))
        {
            deploymentKeys.Add("Deployment");
        }
        if (connectionBuilder.ContainsKey("DeploymentId"))
        {
            deploymentKeys.Add("DeploymentId");
        }
        if (connectionBuilder.ContainsKey("Model"))
        {
            deploymentKeys.Add("Model");
        }

        if (deploymentKeys.Count > 1)
        {
            throw new ArgumentException($"The connection string contains multiple deployment/model keys: {string.Join(", ", deploymentKeys)}. Only one of 'Deployment', 'DeploymentId', or 'Model' should be specified.");
        }

        if (connectionBuilder.TryGetValue("Deployment", out var deployment))
        {
            DeploymentName = deployment.ToString();
        }
        else if (connectionBuilder.TryGetValue("DeploymentId", out var deploymentId))
        {
            DeploymentName = deploymentId.ToString();
        }
        else if (connectionBuilder.TryGetValue("Model", out var model))
        {
            DeploymentName = model.ToString();
        }

        // Use the EndpointAIInference key if available, otherwise fallback to Endpoint.
        // This is because Azure AI Inference may require a /models suffix in the endpoint URL while some
        // other client libraries may not. 
        if (connectionBuilder.TryGetValue("EndpointAIInference", out var endpoint) && Uri.TryCreate(endpoint.ToString(), UriKind.Absolute, out var serviceUri))
        {
            Endpoint = serviceUri;
        }
        else if (connectionBuilder.TryGetValue("Endpoint", out endpoint) && Uri.TryCreate(endpoint.ToString(), UriKind.Absolute, out serviceUri))
        {
            Endpoint = serviceUri;
        }

        if (connectionBuilder.TryGetValue("Key", out var key) && !string.IsNullOrWhiteSpace(key.ToString()))
        {
            Key = key.ToString();
        }
    }
}
