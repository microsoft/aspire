// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography.X509Certificates;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides extension methods for <see cref="IExecutionConfigurationBuilder"/>.
/// </summary>
public static class ExecutionConfigurationBuilderExtensions
{
    /// <summary>
    /// Adds a command line arguments configuration gatherer to the builder.
    /// </summary>
    /// <param name="builder">The builder to add the configuration gatherer to.</param>
    /// <returns>The builder with the configuration gatherer added.</returns>
    public static IExecutionConfigurationBuilder WithArgumentsConfig(this IExecutionConfigurationBuilder builder)
    {
        return builder.AddExecutionConfigurationGatherer(new ArgumentsExecutionConfigurationGatherer());
    }

    /// <summary>
    /// Adds an environment variables configuration gatherer to the builder.
    /// </summary>
    /// <param name="builder">The builder to add the configuration gatherer to.</param>
    /// <returns>The builder with the configuration gatherer added.</returns>
    public static IExecutionConfigurationBuilder WithEnvironmentVariablesConfig(this IExecutionConfigurationBuilder builder)
    {
        return builder.AddExecutionConfigurationGatherer(new EnvironmentVariablesExecutionConfigurationGatherer());
    }

    /// <summary>
    /// Adds a certificate trust configuration gatherer to the builder.
    /// </summary>
    /// <param name="builder">The builder to add the configuration gatherer to.</param>
    /// <param name="configContextFactory">A factory function to create the configuration context.</param>
    /// <returns>The builder with the configuration gatherer added.</returns>
    [Experimental("ASPIRECERTIFICATES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IExecutionConfigurationBuilder WithCertificateTrustConfig(this IExecutionConfigurationBuilder builder, Func<CertificateTrustScope, CertificateTrustExecutionConfigurationContext> configContextFactory)
    {
        return builder.AddExecutionConfigurationGatherer(new CertificateTrustExecutionConfigurationGatherer(configContextFactory));
    }

    /// <summary>
    /// Adds a server authentication certificate configuration gatherer to the builder.
    /// </summary>
    /// <param name="builder">The builder to add the configuration gatherer to.</param>
    /// <param name="configContextFactory">A factory function to create the configuration context.</param>
    /// <returns>The builder with the configuration gatherer added.</returns>
    [Experimental("ASPIRECERTIFICATES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IExecutionConfigurationBuilder WithHttpsCertificateConfig(this IExecutionConfigurationBuilder builder, Func<X509Certificate2, HttpsCertificateExecutionConfigurationContext> configContextFactory)
    {
        return builder.AddExecutionConfigurationGatherer(new HttpsCertificateExecutionConfigurationGatherer(configContextFactory));
    }
}
