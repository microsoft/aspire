// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data.Common;
using Aspire.OpenAI;
using Microsoft.Extensions.Configuration;
using OpenAI;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Registers OpenAI clients with endpoint and authentication selection based on connection configuration.
/// </summary>
public static class AspireConfigurableOpenAIExtensions
{
    /// <summary>
    /// Registers an <see cref="OpenAIClient"/> singleton, selecting OpenAI-compatible, Azure account,
    /// or Foundry project registration from the connection string.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="connectionName">The connection-string name.</param>
    /// <returns>A builder for registering chat and embedding services.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException">The name is empty or connection information is malformed.</exception>
    /// <exception cref="InvalidOperationException">The connection string has neither an endpoint nor an API key.</exception>
    /// <remarks>
    /// Azure endpoints use Microsoft Entra authentication unless an account API key is supplied.
    /// Other endpoints, including Foundry Local, use the configured API key.
    /// The <c>IsAzure</c> connection-string field overrides Azure account detection for custom domains.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.AddOpenAIClientFromConfiguration("chat").AddChatClient();
    /// </code>
    /// </example>
    public static AspireOpenAIClientBuilder AddOpenAIClientFromConfiguration(
        this IHostApplicationBuilder builder, string connectionName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        return GetClientKind(builder.Configuration, connectionName) switch
        {
            ClientKind.AzureAccount => builder.AddAzureOpenAIClient(connectionName),
            ClientKind.FoundryProject => builder.AddAzureProjectOpenAIClient(connectionName),
            _ => builder.AddOpenAIClient(connectionName)
        };
    }

    /// <summary>
    /// Registers a keyed <see cref="OpenAIClient"/> singleton, selecting OpenAI-compatible, Azure account,
    /// or Foundry project registration from the connection string.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="name">The service key and connection-string name.</param>
    /// <returns>A builder for registering keyed chat and embedding services.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException">The name is empty or connection information is malformed.</exception>
    /// <exception cref="InvalidOperationException">The connection string has neither an endpoint nor an API key.</exception>
    /// <remarks>Uses the same endpoint and authentication selection as <see cref="AddOpenAIClientFromConfiguration"/>.</remarks>
    /// <example>
    /// <code>
    /// builder.AddKeyedOpenAIClientFromConfiguration("chat").AddKeyedChatClient("chat-client");
    /// </code>
    /// </example>
    public static AspireOpenAIClientBuilder AddKeyedOpenAIClientFromConfiguration(
        this IHostApplicationBuilder builder, string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return GetClientKind(builder.Configuration, name) switch
        {
            ClientKind.AzureAccount => builder.AddKeyedAzureOpenAIClient(name),
            ClientKind.FoundryProject => builder.AddKeyedAzureProjectOpenAIClient(name),
            _ => builder.AddKeyedOpenAIClient(name)
        };
    }

    private static ClientKind GetClientKind(IConfiguration configuration, string connectionName)
    {
        if (!configuration.TryGetConnectionString(connectionName, out var connectionString))
        {
            // Preserve the original helper's default: settings-only registrations use Aspire:OpenAI.
            return ClientKind.OpenAI;
        }

        Uri? endpoint;
        bool? isAzure = null;
        string? key = null;
        var bareEndpoint = Uri.TryCreate(connectionString, UriKind.Absolute, out var uri);
        if (bareEndpoint)
        {
            endpoint = uri;
        }
        else
        {
            // Hosting supplies "Endpoint=...;Key=...;Model=..." locally and
            // "Endpoint=...;EndpointAIInference=...;Deployment=..." for Azure Foundry.
            var values = new DbConnectionStringBuilder { ConnectionString = connectionString };
            endpoint = null;
            if (values.TryGetValue("Endpoint", out var rawEndpoint))
            {
                if (!Uri.TryCreate(rawEndpoint.ToString(), UriKind.Absolute, out endpoint))
                {
                    throw new ArgumentException($"The 'Endpoint' value in 'ConnectionStrings:{connectionName}' must be an absolute HTTP or HTTPS URI.", nameof(connectionName));
                }
            }

            key = values.TryGetValue("Key", out var rawKey) ? rawKey.ToString()?.Trim() : null;
            if (values.TryGetValue("IsAzure", out var rawIsAzure))
            {
                if (!bool.TryParse(rawIsAzure.ToString(), out var parsedIsAzure))
                {
                    throw new ArgumentException($"The 'IsAzure' value in 'ConnectionStrings:{connectionName}' must be 'true' or 'false'.", nameof(connectionName));
                }

                isAzure = parsedIsAzure;
            }
        }

        if (endpoint is not null && endpoint.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException($"The 'Endpoint' value in 'ConnectionStrings:{connectionName}' must be an absolute HTTP or HTTPS URI.", nameof(connectionName));
        }

        if (endpoint is null && string.IsNullOrEmpty(key))
        {
            throw new InvalidOperationException($"An OpenAIClient could not be configured. Ensure valid connection information was provided in 'ConnectionStrings:{connectionName}'.");
        }

        if (isAzure == false)
        {
            return ClientKind.OpenAI;
        }

        var azure = isAzure == true || (endpoint is not null && IsAzureHost(endpoint.Host));
        if (!azure)
        {
            if (bareEndpoint)
            {
                throw new ArgumentException($"OpenAI-compatible endpoints require 'Endpoint=...;Key=...' in 'ConnectionStrings:{connectionName}'.", nameof(connectionName));
            }

            return ClientKind.OpenAI;
        }

        return endpoint is not null && endpoint.AbsolutePath.StartsWith("/api/projects/", StringComparison.OrdinalIgnoreCase)
            ? ClientKind.FoundryProject : ClientKind.AzureAccount;
    }

    private static bool IsAzureHost(string host)
        => host.EndsWith(".azure.com", StringComparison.OrdinalIgnoreCase) ||
           host.EndsWith(".azure.us", StringComparison.OrdinalIgnoreCase) ||
           host.EndsWith(".azure.cn", StringComparison.OrdinalIgnoreCase) ||
           host.EndsWith(".azure.de", StringComparison.OrdinalIgnoreCase);

    private enum ClientKind
    {
        OpenAI,
        AzureAccount,
        FoundryProject
    }
}
