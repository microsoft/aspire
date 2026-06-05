// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECERTIFICATES001

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Gathers certificate trust configuration for resources that require it.
/// </summary>
internal class CertificateTrustExecutionConfigurationGatherer : IExecutionConfigurationGatherer
{
    private readonly Func<CertificateTrustScope, CertificateTrustExecutionConfigurationContext> _configContextFactory;

    /// <summary>
    /// Initializes a new instance of <see cref="CertificateTrustExecutionConfigurationGatherer"/>.
    /// </summary>
    /// <param name="configContextFactory">A factory for configuring certificate trust configuration properties.</param>
    public CertificateTrustExecutionConfigurationGatherer(Func<CertificateTrustScope, CertificateTrustExecutionConfigurationContext> configContextFactory)
    {
        _configContextFactory = configContextFactory;
    }

    /// <inheritdoc/>
    public async ValueTask GatherAsync(IExecutionConfigurationGathererContext context, IResource resource, ILogger resourceLogger, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken = default)
    {
        var developerCertificateService = executionContext.ServiceProvider.GetRequiredService<IDeveloperCertificateService>();
        var trustDevCert = developerCertificateService.TrustCertificate;

        // Add additional certificate trust configuration metadata
        var additionalData = new CertificateTrustExecutionConfigurationData();
        context.AddAdditionalData(additionalData);

        additionalData.Scope = CertificateTrustScope.Append;
        var certificates = new X509Certificate2Collection();
        if (resource.TryGetLastAnnotation<CertificateAuthorityCollectionAnnotation>(out var caAnnotation))
        {
            foreach (var certCollection in caAnnotation.CertificateAuthorityCollections)
            {
                certificates.AddRange(certCollection.Certificates);
            }

            trustDevCert = caAnnotation.TrustDeveloperCertificates.GetValueOrDefault(trustDevCert);
            additionalData.Scope = caAnnotation.Scope.GetValueOrDefault(additionalData.Scope);
        }

        if (additionalData.Scope == CertificateTrustScope.None)
        {
            // No certificate trust configuration to apply
            return;
        }

        if (additionalData.Scope == CertificateTrustScope.System)
        {
            // Read the system root certificates and add them to the collection
            certificates.AddRootCertificates();
        }

        if (executionContext.IsRunMode && trustDevCert)
        {
            foreach (var cert in developerCertificateService.Certificates)
            {
                certificates.Add(cert);
            }
        }

        additionalData.Certificates.AddRange(certificates);

        if (!additionalData.Certificates.Any())
        {
            // No certificates to configure
            resourceLogger.LogInformation("No custom certificate authorities to configure for '{ResourceName}'. Default certificate authority trust behavior will be used.", resource.Name);
            return;
        }

        var configurationContext = _configContextFactory(additionalData.Scope);

        // Apply default OpenSSL environment configuration for certificate trust
        context.EnvironmentVariables["SSL_CERT_DIR"] = configurationContext.CertificateDirectoriesPath;

        if (additionalData.Scope != CertificateTrustScope.Append)
        {
            context.EnvironmentVariables["SSL_CERT_FILE"] = configurationContext.CertificateBundlePath;
        }

        var callbackContext = new CertificateTrustConfigurationCallbackAnnotationContext
        {
            ExecutionContext = executionContext,
            Resource = resource,
            Scope = additionalData.Scope,
            CertificateBundlePath = configurationContext.CertificateBundlePath,
            CertificateDirectoriesPath = configurationContext.CertificateDirectoriesPath,
            Arguments = context.Arguments,
            EnvironmentVariables = context.EnvironmentVariables,
            CancellationToken = cancellationToken,
        };

        if (resource.TryGetAnnotationsOfType<CertificateTrustConfigurationCallbackAnnotation>(out var callbacks))
        {
            foreach (var callback in callbacks)
            {
                await callback.Callback(callbackContext).ConfigureAwait(false);
            }
        }

        if (additionalData.Scope == CertificateTrustScope.System)
        {
            resourceLogger.LogInformation("Resource '{ResourceName}' has a certificate trust scope of '{Scope}'. Automatically including system root certificates in the trusted configuration.", resource.Name, Enum.GetName(additionalData.Scope));
        }

    }
}

/// <summary>
/// Metadata about the resource certificate trust configuration.
/// </summary>
[Experimental("ASPIRECERTIFICATES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public class CertificateTrustExecutionConfigurationData : IExecutionConfigurationData
{
    /// <summary>
    /// The certificate trust scope for the resource.
    /// </summary>
    public CertificateTrustScope Scope { get; internal set; }

    /// <summary>
    /// The collection of certificates to trust.
    /// </summary>
    public X509Certificate2Collection Certificates { get; } = new();
}

/// <summary>
/// Context for configuring certificate trust configuration properties.
/// </summary>
[Experimental("ASPIRECERTIFICATES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public class CertificateTrustExecutionConfigurationContext
{
    /// <summary>
    /// The path to the certificate bundle file in the resource context (e.g., container filesystem).
    /// </summary>
    public required ReferenceExpression CertificateBundlePath { get; init; }

    /// <summary>
    /// The path(s) to the certificate directories in the resource context (e.g., container filesystem).
    /// </summary>
    public required ReferenceExpression CertificateDirectoriesPath { get; init; }
}
