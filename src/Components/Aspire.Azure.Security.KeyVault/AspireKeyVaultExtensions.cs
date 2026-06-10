// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Azure.Common;
using Aspire.Azure.Security.KeyVault;
using Azure.Core.Extensions;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Security.KeyVault.Certificates;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Provides extension methods for registering and configuring Azure Key Vault secrets in an Aspire application.
/// </summary>
public static class AspireKeyVaultExtensions
{
    internal const string DefaultConfigSectionName = "Aspire:Azure:Security:KeyVault";

    /// <summary>
    /// Registers <see cref="SecretClient"/> as a singleton in the services provided by the <paramref name="builder"/>.
    /// Enables retries, corresponding health check, logging and telemetry.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="AzureSecurityKeyVaultSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureClientBuilder">An optional method that can be used for customizing the <see cref="IAzureClientBuilder{TClient, TOptions}"/>.</param>
    /// <remarks>Reads the configuration from "Aspire:Azure:Security:KeyVault" section.</remarks>
    /// <exception cref="InvalidOperationException">Thrown when mandatory <see cref="AzureSecurityKeyVaultSettings.VaultUri"/> is not provided.</exception>
    public static void AddAzureKeyVaultClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<AzureSecurityKeyVaultSettings>? configureSettings = null,
        Action<IAzureClientBuilder<SecretClient, SecretClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        new AzureKeyVaultSecretsComponent()
            .AddClient(builder, DefaultConfigSectionName, configureSettings, configureClientBuilder, connectionName, serviceKey: null);
    }

    /// <summary>
    /// Registers <see cref="SecretClient"/> as a singleton for given <paramref name="name"/> in the services provided by the <paramref name="builder"/>.
    /// Enables retries, corresponding health check, logging and telemetry.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> of the service and also to retrieve the connection information from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="AzureSecurityKeyVaultSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureClientBuilder">An optional method that can be used for customizing the <see cref="IAzureClientBuilder{TClient, TOptions}"/>.</param>
    /// <remarks>Reads the configuration from "Aspire:Azure:Security:KeyVault:{name}" section.</remarks>
    /// <exception cref="InvalidOperationException">Thrown when mandatory <see cref="AzureSecurityKeyVaultSettings.VaultUri"/> is not provided.</exception>
    public static void AddKeyedAzureKeyVaultClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<AzureSecurityKeyVaultSettings>? configureSettings = null,
        Action<IAzureClientBuilder<SecretClient, SecretClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        new AzureKeyVaultSecretsComponent()
            .AddClient(builder, DefaultConfigSectionName, configureSettings, configureClientBuilder, connectionName: name, serviceKey: name);
    }

    /// <summary>
    /// Registers <see cref="CertificateClient"/> as a singleton in the services provided by the <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="AzureSecurityKeyVaultSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureClientBuilder">An optional method that can be used for customizing the <see cref="IAzureClientBuilder{TClient, TOptions}"/>.</param>
    /// <remarks>Reads the configuration from "Aspire:Azure:Security:KeyVault:{name}" section.</remarks>
    /// <exception cref="InvalidOperationException">Thrown when mandatory <see cref="AzureSecurityKeyVaultSettings.VaultUri"/> is not provided.</exception>
    public static void AddAzureKeyVaultCertificateClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<AzureSecurityKeyVaultSettings>? configureSettings = null,
        Action<IAzureClientBuilder<CertificateClient, CertificateClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        new AzureKeyVaultCertificatesComponent()
            .AddClient(builder, DefaultConfigSectionName, configureSettings, configureClientBuilder, connectionName, serviceKey: null);
    }

    /// <summary>
    /// Registers <see cref="CertificateClient"/> as a singleton for given <paramref name="name"/> in the services provided by the <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> of the service and also to retrieve the connection information from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="AzureSecurityKeyVaultSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureClientBuilder">An optional method that can be used for customizing the <see cref="IAzureClientBuilder{TClient, TOptions}"/>.</param>
    /// <remarks>Reads the configuration from "Aspire:Azure:Security:KeyVault:{name}" section.</remarks>
    /// <exception cref="InvalidOperationException">Thrown when mandatory <see cref="AzureSecurityKeyVaultSettings.VaultUri"/> is not provided.</exception>
    public static void AddKeyedAzureKeyVaultCertificateClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<AzureSecurityKeyVaultSettings>? configureSettings = null,
        Action<IAzureClientBuilder<CertificateClient, CertificateClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        new AzureKeyVaultCertificatesComponent()
            .AddClient(builder, DefaultConfigSectionName, configureSettings, configureClientBuilder, connectionName: name, serviceKey: name);
    }

    /// <summary>
    /// Registers <see cref="KeyClient"/> as a singleton in the services provided by the <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="AzureSecurityKeyVaultSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureClientBuilder">An optional method that can be used for customizing the <see cref="IAzureClientBuilder{TClient, TOptions}"/>.</param>
    /// <remarks>Reads the configuration from "Aspire:Azure:Security:KeyVault:{name}" section.</remarks>
    /// <exception cref="InvalidOperationException">Thrown when mandatory <see cref="AzureSecurityKeyVaultSettings.VaultUri"/> is not provided.</exception>
    public static void AddAzureKeyVaultKeyClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<AzureSecurityKeyVaultSettings>? configureSettings = null,
        Action<IAzureClientBuilder<KeyClient, KeyClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        new AzureKeyVaultKeysComponent()
            .AddClient(builder, DefaultConfigSectionName, configureSettings, configureClientBuilder, connectionName, serviceKey: null);
    }

    /// <summary>
    /// Registers <see cref="KeyClient"/> as a singleton for given <paramref name="name"/> in the services provided by the <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> of the service and also to retrieve the connection information from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="AzureSecurityKeyVaultSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureClientBuilder">An optional method that can be used for customizing the <see cref="IAzureClientBuilder{TClient, TOptions}"/>.</param>
    /// <remarks>Reads the configuration from "Aspire:Azure:Security:KeyVault:{name}" section.</remarks>
    /// <exception cref="InvalidOperationException">Thrown when mandatory <see cref="AzureSecurityKeyVaultSettings.VaultUri"/> is not provided.</exception>
    public static void AddKeyedAzureKeyVaultKeyClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<AzureSecurityKeyVaultSettings>? configureSettings = null,
        Action<IAzureClientBuilder<KeyClient, KeyClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        new AzureKeyVaultKeysComponent()
            .AddClient(builder, DefaultConfigSectionName, configureSettings, configureClientBuilder, connectionName: name, serviceKey: name);
    }

    /// <summary>
    /// Adds the Azure KeyVault secrets to be configuration values in the <paramref name="configurationManager"/>.
    /// </summary>
    /// <param name="configurationManager">The <see cref="IConfigurationManager"/> to add the secrets to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="AzureSecurityKeyVaultSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureClientOptions">An optional method that can be used for customizing the <see cref="SecretClientOptions"/>.</param>
    /// <param name="options">An optional <see cref="AzureKeyVaultConfigurationOptions"/> instance to configure the behavior of the configuration provider.</param>
    public static void AddAzureKeyVaultSecrets(
        this IConfigurationManager configurationManager,
        string connectionName,
        Action<AzureSecurityKeyVaultSettings>? configureSettings = null,
        Action<SecretClientOptions>? configureClientOptions = null,
        AzureKeyVaultConfigurationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(configurationManager);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        var client = configurationManager.GetSecretClient(connectionName, configureSettings, configureClientOptions);
        configurationManager.AddAzureKeyVault(client, options ?? new AzureKeyVaultConfigurationOptions());
    }

    private static SecretClient GetSecretClient(
        this IConfiguration configuration,
        string connectionName,
        Action<AzureSecurityKeyVaultSettings>? configureSettings,
        Action<SecretClientOptions>? configureOptions)
    {
        var configSection = configuration.GetSection(DefaultConfigSectionName);

        var settings = new AzureSecurityKeyVaultSettings();
        configSection.Bind(settings);

        if (configuration.GetConnectionString(connectionName) is string connectionString)
        {
            ((IConnectionStringSettings)settings).ParseConnectionString(connectionString);
        }

        configureSettings?.Invoke(settings);

        var clientOptions = new SecretClientOptions();
        configSection.GetSection("ClientOptions").Bind(clientOptions);
        configureOptions?.Invoke(clientOptions);

        if (settings.VaultUri is null)
        {
            throw new InvalidOperationException($"VaultUri is missing. It should be provided in 'ConnectionStrings:{connectionName}' or under the 'VaultUri' key in the '{DefaultConfigSectionName}' configuration section.");
        }

        return new SecretClient(settings.VaultUri, settings.Credential ?? AzureCredentialHelper.CreateDefaultAzureCredential(), clientOptions);
    }
}
