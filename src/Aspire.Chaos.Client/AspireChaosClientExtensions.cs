// <copyright file="AspireChaosClientExtensions.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using Aspire.Chaos.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Provides extension methods for registering <see cref="ChaosProxyClient"/> in the
/// services provided by <see cref="IHostApplicationBuilder"/>. Mirrors the canonical
/// Aspire client-integration shape (e.g., <c>AspireTablesExtensions.AddAzureTableClient</c>).
/// </summary>
[Experimental("ASPIRECHAOS001", UrlFormat = "https://aka.ms/aspire-chaos-proxy/experimental/{0}")]
public static class AspireChaosClientExtensions
{
    /// <summary>The configuration section path the settings are bound from.</summary>
    public const string DefaultConfigSectionName = "Aspire:Chaos:Client";

    private const string DefaultHealthCheckNamePrefix = "chaos-proxy_";

    /// <summary>
    /// Registers <see cref="ChaosProxyClient"/> as a typed <see cref="HttpClient"/> in the
    /// services provided by <paramref name="builder"/>. The client's
    /// <see cref="HttpClient.BaseAddress"/> is set from the connection string resolved by
    /// <paramref name="connectionName"/>, or from <see cref="ChaosProxyClientSettings.Endpoint"/>
    /// if provided.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder"/> to read configuration from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section, and to look up overrides under <c>Aspire:Chaos:Client:{connectionName}</c>.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="ChaosProxyClientSettings"/>. Invoked after settings are read from configuration.</param>
    /// <param name="configureHttpClient">An optional method that can be used for customizing the <see cref="IHttpClientBuilder"/> for the <see cref="ChaosProxyClient"/>.</param>
    /// <returns>The <see cref="IHttpClientBuilder"/> so calls can be chained.</returns>
    /// <remarks>
    /// Reads the configuration from <c>"Aspire:Chaos:Client"</c> (and <c>"Aspire:Chaos:Client:{connectionName}"</c> for per-connection overrides).
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when neither <see cref="ChaosProxyClientSettings.ConnectionString"/> nor <see cref="ChaosProxyClientSettings.Endpoint"/> can be resolved.</exception>
    public static IHttpClientBuilder AddChaosProxyClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<ChaosProxyClientSettings>? configureSettings = null,
        Action<IHttpClientBuilder>? configureHttpClient = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        return AddChaosProxyClientInternal(builder, connectionName, serviceKey: null, configureSettings, configureHttpClient);
    }

    /// <summary>
    /// Registers a keyed <see cref="ChaosProxyClient"/> in the services provided by
    /// <paramref name="builder"/>. Use when an AppHost has multiple chaos proxies and a
    /// consumer needs to resolve the right one by key.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder"/> to read configuration from and add services to.</param>
    /// <param name="name">The name of the component. Used as the service key AND to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="ChaosProxyClientSettings"/>.</param>
    /// <param name="configureHttpClient">An optional method that can be used for customizing the <see cref="IHttpClientBuilder"/>.</param>
    /// <returns>The <see cref="IHttpClientBuilder"/> so calls can be chained.</returns>
    /// <remarks>
    /// Reads the configuration from <c>"Aspire:Chaos:Client:{name}"</c>.
    /// </remarks>
    public static IHttpClientBuilder AddKeyedChaosProxyClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<ChaosProxyClientSettings>? configureSettings = null,
        Action<IHttpClientBuilder>? configureHttpClient = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return AddChaosProxyClientInternal(builder, name, serviceKey: name, configureSettings, configureHttpClient);
    }

    private static IHttpClientBuilder AddChaosProxyClientInternal(
        IHostApplicationBuilder builder,
        string connectionName,
        string? serviceKey,
        Action<ChaosProxyClientSettings>? configureSettings,
        Action<IHttpClientBuilder>? configureHttpClient)
    {
        var settings = new ChaosProxyClientSettings();
        builder.Configuration.GetSection(DefaultConfigSectionName).Bind(settings);
        builder.Configuration.GetSection($"{DefaultConfigSectionName}:{connectionName}").Bind(settings);

        var connectionString = builder.Configuration.GetConnectionString(connectionName);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            settings.ConnectionString = connectionString;
        }

        configureSettings?.Invoke(settings);

        var endpoint = ResolveEndpoint(settings, connectionName);

        var httpClientName = serviceKey is null
            ? typeof(ChaosProxyClient).Name
            : $"{typeof(ChaosProxyClient).Name}_{serviceKey}";

        var httpClientBuilder = serviceKey is null
            ? builder.Services
                .AddHttpClient<ChaosProxyClient>(httpClientName, client => client.BaseAddress = endpoint)
            : builder.Services
                .AddHttpClient(httpClientName, client => client.BaseAddress = endpoint);

        if (serviceKey is not null)
        {
            // Keyed registration: the named HttpClient resolves to a ChaosProxyClient via
            // explicit keyed registration. Mirrors Aspire's keyed-client pattern for typed
            // HttpClients without IServiceCollection.AddKeyedHttpClient<T> (not yet shipped).
            builder.Services.AddKeyedTransient<ChaosProxyClient>(serviceKey, (sp, _) =>
            {
                var factory = sp.GetRequiredService<IHttpClientFactory>();
                var http = factory.CreateClient(httpClientName);
                return new ChaosProxyClient(http);
            });
        }

        if (!settings.DisableHealthChecks)
        {
            var healthCheckName = serviceKey is null
                ? DefaultHealthCheckNamePrefix.TrimEnd('_')
                : $"{DefaultHealthCheckNamePrefix}{serviceKey}";

            builder.Services
                .AddHealthChecks()
                .Add(new HealthCheckRegistration(
                    healthCheckName,
                    sp => new ChaosProxyHealthCheck(sp.GetRequiredService<IHttpClientFactory>(), httpClientName),
                    failureStatus: null,
                    tags: ["chaos", "chaos-proxy"]));
        }

        configureHttpClient?.Invoke(httpClientBuilder);
        return httpClientBuilder;
    }

    private static Uri ResolveEndpoint(ChaosProxyClientSettings settings, string connectionName)
    {
        if (settings.Endpoint is not null)
        {
            return settings.Endpoint;
        }

        if (!string.IsNullOrWhiteSpace(settings.ConnectionString)
            && Uri.TryCreate(settings.ConnectionString, UriKind.Absolute, out var fromConnString))
        {
            return fromConnString;
        }

        throw new InvalidOperationException(
            $"Unable to resolve chaos proxy endpoint for connection '{connectionName}'. " +
            $"Provide either a connection string in '{nameof(ConfigurationExtensions.GetConnectionString)}(\"{connectionName}\")' (an absolute URL) " +
            $"or set '{nameof(ChaosProxyClientSettings.Endpoint)}' on the bound {nameof(ChaosProxyClientSettings)}.");
    }

    private sealed class ChaosProxyHealthCheck : IHealthCheck
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _httpClientName;

        public ChaosProxyHealthCheck(IHttpClientFactory httpClientFactory, string httpClientName)
        {
            _httpClientFactory = httpClientFactory;
            _httpClientName = httpClientName;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                var http = _httpClientFactory.CreateClient(_httpClientName);
                var client = new ChaosProxyClient(http);
                var ok = await client.HealthAsync(cancellationToken).ConfigureAwait(false);
                return ok ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Chaos proxy /chaos/healthz did not return 200.");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("Chaos proxy health probe threw an exception.", ex);
            }
        }
    }
}
