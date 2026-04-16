#pragma warning disable ASPIRECERTIFICATES001

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Ats;

/// <summary>
/// ATS exports for execution-configuration helpers that need DTO or callback shims.
/// </summary>
internal static class ExecutionConfigurationExports
{
    /// <summary>
    /// Creates an execution configuration builder for the specified resource.
    /// </summary>
    /// <param name="resource">The resource to build the execution configuration for.</param>
    /// <returns>The execution configuration builder.</returns>
    [AspireExport(Description = "Creates an execution configuration builder")]
    public static IExecutionConfigurationBuilder CreateExecutionConfiguration(this IResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return ExecutionConfigurationBuilder.Create(resource);
    }

    /// <summary>
    /// Builds the execution configuration for the specified builder.
    /// </summary>
    /// <param name="builder">The execution configuration builder.</param>
    /// <param name="executionContext">The execution context used while building the configuration.</param>
    /// <param name="resourceLogger">The logger used while resolving values.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The resolved execution configuration.</returns>
    [AspireExport("buildExecutionConfiguration", MethodName = "build", Description = "Builds the execution configuration")]
    public static Task<IExecutionConfigurationResult> Build(
        this IExecutionConfigurationBuilder builder,
        DistributedApplicationExecutionContext executionContext,
        ILogger? resourceLogger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(executionContext);

        return builder.BuildAsync(executionContext, resourceLogger ?? NullLogger.Instance, cancellationToken);
    }

    /// <summary>
    /// Adds an HTTPS certificate configuration gatherer using certificate metadata instead of a raw X509 certificate.
    /// </summary>
    /// <param name="builder">The execution configuration builder.</param>
    /// <param name="configContextFactory">The factory that creates the HTTPS certificate configuration context.</param>
    /// <returns>The execution configuration builder.</returns>
    [AspireExport("withHttpsCertificateConfigExport", MethodName = "withHttpsCertificateConfig", Description = "Adds an HTTPS certificate configuration gatherer")]
    public static IExecutionConfigurationBuilder WithHttpsCertificateConfig(
        this IExecutionConfigurationBuilder builder,
        Func<HttpsCertificateInfo, HttpsCertificateExecutionConfigurationContext> configContextFactory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configContextFactory);

        return ExecutionConfigurationBuilderExtensions.WithHttpsCertificateConfig(
            builder,
            certificate => configContextFactory(HttpsCertificateInfo.FromCertificate(certificate)));
    }

    /// <summary>
    /// Gets certificate trust execution-configuration data when present.
    /// </summary>
    /// <param name="configuration">The execution configuration result.</param>
    /// <returns>The certificate trust data. When no additional data is present, an empty DTO is returned.</returns>
    [AspireExport(Description = "Gets certificate trust execution-configuration data")]
    public static CertificateTrustExecutionConfigurationExportData GetCertificateTrustData(this IExecutionConfigurationResult configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.TryGetAdditionalData<CertificateTrustExecutionConfigurationData>(out var additionalData))
        {
            return new CertificateTrustExecutionConfigurationExportData
            {
                Scope = CertificateTrustScope.None,
                CertificateSubjects = [],
                CustomBundlePaths = []
            };
        }

        return new CertificateTrustExecutionConfigurationExportData
        {
            Scope = additionalData.Scope,
            CertificateSubjects = [.. additionalData.Certificates.Cast<X509Certificate2>().Select(static certificate => certificate.Subject)],
            CustomBundlePaths = [.. additionalData.CustomBundlesFactories.Keys]
        };
    }

    /// <summary>
    /// Gets HTTPS certificate execution-configuration data when present.
    /// </summary>
    /// <param name="configuration">The execution configuration result.</param>
    /// <returns>The HTTPS certificate data. When no additional data is present, an empty DTO is returned.</returns>
    [AspireExport(Description = "Gets HTTPS certificate execution-configuration data")]
    public static HttpsCertificateExecutionConfigurationExportData GetHttpsCertificateData(this IExecutionConfigurationResult configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.TryGetAdditionalData<HttpsCertificateExecutionConfigurationData>(out var additionalData))
        {
            return new HttpsCertificateExecutionConfigurationExportData
            {
                Subject = string.Empty,
                KeyPathExpression = string.Empty,
                PfxPathExpression = string.Empty,
                IsKeyPathReferenced = false,
                IsPfxPathReferenced = false
            };
        }

        return new HttpsCertificateExecutionConfigurationExportData
        {
            Subject = additionalData.Certificate.Subject,
            Thumbprint = additionalData.Certificate.Thumbprint,
            KeyPathExpression = additionalData.KeyPathReference.ValueExpression,
            PfxPathExpression = additionalData.PfxPathReference.ValueExpression,
            IsKeyPathReferenced = additionalData.IsKeyPathReferenced,
            IsPfxPathReferenced = additionalData.IsPfxPathReferenced,
            Password = additionalData.Password
        };
    }

}

/// <summary>
/// ATS-friendly certificate metadata supplied to HTTPS certificate configuration callbacks.
/// </summary>
[AspireDto]
internal sealed class HttpsCertificateInfo
{
    /// <summary>
    /// The certificate subject.
    /// </summary>
    public required string Subject { get; init; }

    /// <summary>
    /// The certificate issuer.
    /// </summary>
    public required string Issuer { get; init; }

    /// <summary>
    /// The certificate thumbprint.
    /// </summary>
    public string? Thumbprint { get; init; }

    internal static HttpsCertificateInfo FromCertificate(X509Certificate2 certificate)
    {
        return new HttpsCertificateInfo
        {
            Subject = certificate.Subject,
            Issuer = certificate.Issuer,
            Thumbprint = certificate.Thumbprint
        };
    }
}

/// <summary>
/// ATS-friendly certificate trust data returned from an execution-configuration result.
/// </summary>
[AspireDto]
internal sealed class CertificateTrustExecutionConfigurationExportData
{
    /// <summary>
    /// The certificate trust scope.
    /// </summary>
    public required CertificateTrustScope Scope { get; init; }

    /// <summary>
    /// The certificate subjects included in the trust configuration.
    /// </summary>
    public required string[] CertificateSubjects { get; init; }

    /// <summary>
    /// The relative custom bundle paths.
    /// </summary>
    public required string[] CustomBundlePaths { get; init; }
}

/// <summary>
/// ATS-friendly HTTPS certificate data returned from an execution-configuration result.
/// </summary>
[AspireDto]
internal sealed class HttpsCertificateExecutionConfigurationExportData
{
    /// <summary>
    /// The certificate subject.
    /// </summary>
    public required string Subject { get; init; }

    /// <summary>
    /// The certificate thumbprint.
    /// </summary>
    public string? Thumbprint { get; init; }

    /// <summary>
    /// The expression for the key path reference.
    /// </summary>
    public required string KeyPathExpression { get; init; }

    /// <summary>
    /// The expression for the PFX path reference.
    /// </summary>
    public required string PfxPathExpression { get; init; }

    /// <summary>
    /// Indicates whether the key path was referenced.
    /// </summary>
    public required bool IsKeyPathReferenced { get; init; }

    /// <summary>
    /// Indicates whether the PFX path was referenced.
    /// </summary>
    public required bool IsPfxPathReferenced { get; init; }

    /// <summary>
    /// The certificate password, if any.
    /// </summary>
    public string? Password { get; init; }
}
