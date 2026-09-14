// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OpenAI;

namespace Aspire.OpenAI;

/// <summary>
/// A builder for configuring an <see cref="OpenAIClient"/> service registration.
/// Constructs a new instance of <see cref="AspireOpenAIClientBuilder"/>.
/// </summary>
/// <param name="hostBuilder">The <see cref="IHostApplicationBuilder"/> with which services are being registered.</param>
/// <param name="connectionName">The name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
/// <param name="serviceKey">The service key used to register the <see cref="OpenAIClient"/> service, if any.</param>
/// <param name="disableTracing">A flag to indicate whether tracing should be disabled.</param>
/// <param name="enableSensitiveTelemetryData">A flag indicating whether potentially sensitive information should be included in telemetry.</param>
public class AspireOpenAIClientBuilder(IHostApplicationBuilder hostBuilder, string connectionName, string? serviceKey, bool disableTracing, bool enableSensitiveTelemetryData)
{
    private const string DeploymentKey = "Deployment";
    private const string ModelKey = "Model";

    /// <summary>
    /// Gets the <see cref="IHostApplicationBuilder"/> with which services are being registered.
    /// </summary>
    public IHostApplicationBuilder HostBuilder { get; } = hostBuilder ?? throw new ArgumentNullException(nameof(hostBuilder));

    /// <summary>
    /// Gets the name used to retrieve the connection string from the ConnectionStrings configuration section.
    /// </summary>
    public string ConnectionName { get; } = ThrowIfNullOrEmpty(connectionName);

    /// <summary>
    /// Gets the service key used to register the <see cref="OpenAIClient"/> service, if any.
    /// </summary>
    public string? ServiceKey { get; } = serviceKey;

    /// <summary>
    /// Gets a flag indicating whether tracing should be disabled.
    /// </summary>
    public bool DisableTracing { get; } = disableTracing;

    /// <summary>
    /// Gets a flag indicating whether potentially sensitive information should be included in telemetry.
    /// </summary>
    public bool EnableSensitiveTelemetryData { get; } = enableSensitiveTelemetryData;

    /// <summary>
    /// Gets the name of the configuration section for this component type.
    /// </summary>
    public virtual string ConfigurationSectionName => ServiceKey is null ?
        AspireOpenAIExtensions.DefaultConfigSectionName :
        $"{AspireOpenAIExtensions.DefaultConfigSectionName}:{ServiceKey}";

    internal string GetRequiredDeploymentName()
    {
        string? deploymentName = null;

        var configuration = HostBuilder.Configuration;
        if (configuration.TryGetConnectionString(ConnectionName, out var connectionString))
        {
            // The reason we accept either 'Deployment' or 'Model' as the key is because some hosting solutions
            // require specific named deployments (Azure Foundry AI) while others may use a generic model name (OpenAI, GitHub Models).
            var connectionBuilder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            var deploymentValue = ConnectionStringValue(connectionBuilder, DeploymentKey);
            var modelValue = ConnectionStringValue(connectionBuilder, ModelKey);
            if (deploymentValue is not null && modelValue is not null)
            {
                throw new InvalidOperationException(
                    $"The connection string '{ConnectionName}' contains both '{DeploymentKey}' and '{ModelKey}' keys. Either of these may be specified, but not both.");
            }

            deploymentName = deploymentValue ?? modelValue;
        }

        if (string.IsNullOrEmpty(deploymentName))
        {
            var configSection = configuration.GetSection(ConfigurationSectionName);
            deploymentName = configSection[DeploymentKey];
        }

        if (string.IsNullOrEmpty(deploymentName))
        {
            throw new InvalidOperationException($"The deployment could not be determined. Ensure a '{DeploymentKey}' or '{ModelKey}' value is provided in 'ConnectionStrings:{ConnectionName}', or specify a '{DeploymentKey}' in the '{ConfigurationSectionName}' configuration section, or specify a '{nameof(deploymentName)}' in the call.");
        }

        return deploymentName;
    }

    private static string? ConnectionStringValue(DbConnectionStringBuilder connectionString, string key)
        => connectionString.TryGetValue(key, out var value) ? value as string : null;

    private static string ThrowIfNullOrEmpty([NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(argument, paramName);
        return argument;
    }
}
