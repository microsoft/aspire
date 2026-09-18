// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data.Common;
using Aspire.OpenAI;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Provides extension methods for registering <see cref="OpenAIClient"/> or <see cref="AzureOpenAIClient"/> as a singleton in the services provided by the <see cref="IHostApplicationBuilder"/>.
/// </summary>
public static class AspireConfigurableOpenAIExtensions
{
    private const string ConnectionStringEndpoint = "Endpoint";
    private const string ConnectionStringKey = "Key";
    private const string ConnectionStringIsAzure = "IsAzure";

    /// <summary>
    /// Registers <see cref="OpenAIClient"/> or <see cref="AzureOpenAIClient"/> as a singleton in the services provided by the <paramref name="builder"/>.
    /// The concrete implementation is selected automatically from configuration.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <returns>An <see cref="AspireOpenAIClientBuilder"/> that can be used to register additional services.</returns>
    public static AspireOpenAIClientBuilder AddOpenAIClientFromConfiguration(
        this IHostApplicationBuilder builder,
        string connectionName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        var useAzure = false;

        if (builder.Configuration.TryGetConnectionString(connectionName, out var connectionString))
        {
            useAzure = IsAzureConnectionString(connectionString, connectionName);
        }

        return useAzure ?
            builder.AddAzureOpenAIClient(connectionName) :
            builder.AddOpenAIClient(connectionName);
    }

    /// <summary>
    /// Registers <see cref="OpenAIClient"/> as a singleton in the services provided by the <paramref name="builder"/>.
    /// The concrete implementation is selected automatically from configuration.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> of the service and also to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <returns>An <see cref="AspireOpenAIClientBuilder"/> that can be used to register additional services.</returns>
    public static AspireOpenAIClientBuilder AddKeyedOpenAIClientFromConfiguration(
        this IHostApplicationBuilder builder,
        string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var useAzure = false;

        if (builder.Configuration.TryGetConnectionString(name, out var connectionString))
        {
            useAzure = IsAzureConnectionString(connectionString, name);
        }

        return useAzure ?
            builder.AddKeyedAzureOpenAIClient(name) :
            builder.AddKeyedOpenAIClient(name);
    }

    private static bool IsAzureConnectionString(string connectionString, string connectionName)
    {
        Uri? serviceUri = null;
        string? apiKey = null;

        var connectionBuilder = new DbConnectionStringBuilder
        {
            ConnectionString = connectionString
        };

        if (connectionBuilder.TryGetValue(ConnectionStringEndpoint, out var endpoint) && endpoint != null && Uri.TryCreate(endpoint.ToString(), UriKind.Absolute, out var endpointUri))
        {
            serviceUri = endpointUri;
        }

        if (connectionBuilder.TryGetValue(ConnectionStringKey, out var key) && key != null)
        {
            apiKey = key.ToString()?.Trim();
        }

        if (serviceUri == null && string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException($"An OpenAIClient could not be configured. Ensure valid connection information was provided in 'ConnectionStrings:{connectionName}'.");
        }

        if (connectionBuilder.ContainsKey(ConnectionStringIsAzure))
        {
            return bool.TryParse(connectionBuilder[ConnectionStringIsAzure].ToString(), out var isAzure) && isAzure;
        }

        if (serviceUri != null && serviceUri.Host.Contains(".azure.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
