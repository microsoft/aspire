// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.OpenAI;
using Microsoft.Extensions.Hosting;
using OpenAI;

namespace Aspire.Azure.AI.Extensions.OpenAI;

/// <summary>
/// Configures additional services for an Azure OpenAI account's <see cref="OpenAIClient"/> registration.
/// </summary>
/// <param name="hostBuilder">The application builder.</param>
/// <param name="connectionName">The account connection-string name.</param>
/// <param name="serviceKey">The service key, or <see langword="null"/> for an unkeyed client.</param>
/// <param name="disableTracing">Whether tracing is disabled.</param>
/// <param name="enableSensitiveTelemetryData">Whether telemetry may capture message content.</param>
public class AspireAzureOpenAIClientBuilder(IHostApplicationBuilder hostBuilder, string connectionName, string? serviceKey, bool disableTracing, bool enableSensitiveTelemetryData)
    : AspireOpenAIClientBuilder(hostBuilder, connectionName, serviceKey, disableTracing, enableSensitiveTelemetryData)
{
    /// <inheritdoc/>
    public override string ConfigurationSectionName => ServiceKey is null
        ? AspireAzureOpenAIExtensions.DefaultConfigSectionName
        : $"{AspireAzureOpenAIExtensions.DefaultConfigSectionName}:{ServiceKey}";
}
