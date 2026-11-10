// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECERTIFICATES001

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Resolved container configuration and certificate paths, before rendering backend-specific files and trust settings.
/// </summary>
internal sealed record ContainerConfigurationResult(
    IExecutionConfigurationResult Configuration,
    string CertificatesDestination,
    List<string> DefaultCertificateBundlePaths);

/// <summary>
/// Resolves app-model arguments, environment variables, and certificate references for containers.
/// </summary>
internal static class ContainerConfigurationResolver
{
    public static async Task<ContainerConfigurationResult> ResolveAsync(
        IResource resource,
        DistributedApplicationExecutionContext executionContext,
        ILogger resourceLogger,
        CancellationToken cancellationToken)
    {
        var certificatesDestination = ContainerCertificatePathsAnnotation.DefaultCustomCertificatesDestination;
        var bundlePaths = ContainerCertificatePathsAnnotation.DefaultCertificateBundlePaths.ToList();
        var certificateDirsPaths = ContainerCertificatePathsAnnotation.DefaultCertificateDirectoriesPaths.ToList();

        if (resource.TryGetLastAnnotation<ContainerCertificatePathsAnnotation>(out var pathsAnnotation))
        {
            certificatesDestination = pathsAnnotation.CustomCertificatesDestination ?? certificatesDestination;
            bundlePaths = pathsAnnotation.DefaultCertificateBundles ?? bundlePaths;
            certificateDirsPaths = pathsAnnotation.DefaultCertificateDirectories ?? certificateDirsPaths;
        }

        var serverAuthCertificatesBasePath = $"{certificatesDestination}/private";
        var configuration = await ExecutionConfigurationBuilder.Create(resource)
            .WithArgumentsConfig()
            .WithEnvironmentVariablesConfig()
            .WithCertificateTrustConfig(scope =>
            {
                var dirs = new List<string> { certificatesDestination + "/certs" };
                if (scope == CertificateTrustScope.Append)
                {
                    dirs.AddRange(certificateDirsPaths);
                }

                return new()
                {
                    CertificateBundlePath = ReferenceExpression.Create($"{certificatesDestination}/cert.pem"),
                    CertificateDirectoriesPath = ReferenceExpression.Create($"{string.Join(':', dirs)}"),
                    RootCertificatesPath = certificatesDestination,
                    IsContainer = true,
                };
            })
            .WithHttpsCertificateConfig(cert => new()
            {
                CertificatePath = ReferenceExpression.Create($"{serverAuthCertificatesBasePath}/{cert.Thumbprint}.crt"),
                KeyPath = ReferenceExpression.Create($"{serverAuthCertificatesBasePath}/{cert.Thumbprint}.key"),
                CertificateWithKeyPath = ReferenceExpression.Create($"{serverAuthCertificatesBasePath}/{cert.Thumbprint}.pem"),
                PfxPath = ReferenceExpression.Create($"{serverAuthCertificatesBasePath}/{cert.Thumbprint}.pfx"),
            })
            .BuildAsync(executionContext, resourceLogger, cancellationToken)
            .ConfigureAwait(false);

        return new(configuration, certificatesDestination, bundlePaths);
    }
}
