// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECERTIFICATES001

using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dcp;

internal sealed class ExecutableConfigurationResult(
    IExecutionConfigurationResult configuration,
    ExecutablePemCertificates? pemCertificates)
{
    public IExecutionConfigurationResult Configuration { get; } = configuration;

    public ExecutablePemCertificates? PemCertificates { get; } = pemCertificates;
}

internal sealed class ExecutableConfigurationResolver(
    DistributedApplicationExecutionContext executionContext,
    Locations locations,
    IAspireStore aspireStore)
{
    private readonly DistributedApplicationExecutionContext _executionContext = executionContext;
    private readonly Locations _locations = locations;
    private readonly IAspireStore _aspireStore = aspireStore;

    public async Task<ExecutableConfigurationResult> ResolveAsync(
        RenderedModelResource<Executable> renderedResource,
        ILogger resourceLogger,
        CancellationToken cancellationToken)
    {
        var executable = renderedResource.DcpResource;
        var certificatesRootDirectory = GetCertificatesRootDirectory(renderedResource, executable);
        var bundleOutputPath = Path.Join(certificatesRootDirectory, "cert.pem");
        var customBundleOutputPath = Path.Join(certificatesRootDirectory, "bundles");
        var certificatesOutputPath = Path.Join(certificatesRootDirectory, "certs");
        var baseServerAuthOutputPath = Path.Join(certificatesRootDirectory, "private");

        var configuration = await ExecutionConfigurationBuilder.Create(renderedResource.ModelResource)
            .WithArgumentsConfig()
            .WithEnvironmentVariablesConfig()
            .WithCertificateTrustConfig(scope =>
            {
                var directories = new List<string> { certificatesOutputPath };
                if (scope == CertificateTrustScope.Append)
                {
                    var existingSslCertDirectory = Environment.GetEnvironmentVariable(
                        CertificateTrustExecutionConfigurationGatherer.SslCertDirEnvironmentVariable);
                    if (existingSslCertDirectory is not null)
                    {
                        directories.AddRange(existingSslCertDirectory.Split(
                            Path.PathSeparator,
                            StringSplitOptions.RemoveEmptyEntries));
                    }
                    else if (OperatingSystem.IsLinux())
                    {
                        // Aspire CLI normally materializes OpenSSL's default directory into SSL_CERT_DIR.
                        // A dotnet-run AppHost may not have that value, so use the same well-known directories
                        // used for containers without shelling out to openssl during resource creation.
                        directories.AddRange(
                            ContainerCertificatePathsAnnotation.DefaultCertificateDirectoriesPaths.Where(Directory.Exists));
                    }
                }

                return new()
                {
                    CertificateBundlePath = ReferenceExpression.Create($"{bundleOutputPath}"),
                    CertificateDirectoriesPath = ReferenceExpression.Create($"{string.Join(Path.PathSeparator, directories)}"),
                    RootCertificatesPath = certificatesRootDirectory,
                };
            })
            .WithHttpsCertificateConfig(certificate => new()
            {
                CertificatePath = ReferenceExpression.Create($"{Path.Join(baseServerAuthOutputPath, $"{certificate.Thumbprint}.crt")}"),
                KeyPath = ReferenceExpression.Create($"{Path.Join(baseServerAuthOutputPath, $"{certificate.Thumbprint}.key")}"),
                CertificateWithKeyPath = ReferenceExpression.Create($"{Path.Join(baseServerAuthOutputPath, $"{certificate.Thumbprint}.pem")}"),
                PfxPath = ReferenceExpression.Create($"{Path.Join(baseServerAuthOutputPath, $"{certificate.Thumbprint}.pfx")}"),
            })
            .BuildAsync(_executionContext, resourceLogger, cancellationToken)
            .ConfigureAwait(false);

        ExecutablePemCertificates? pemCertificates = null;
        if (configuration.TryGetAdditionalData<CertificateTrustExecutionConfigurationData>(out var certificateTrustConfiguration) &&
            certificateTrustConfiguration.Scope != CertificateTrustScope.None &&
            certificateTrustConfiguration.Certificates.Count > 0)
        {
            pemCertificates = new ExecutablePemCertificates
            {
                Certificates = CertificateUtilities.BuildPemCertificateList(certificateTrustConfiguration.Certificates),
                ContinueOnError = true,
            };

            if (certificateTrustConfiguration.CustomBundlesFactories.Count > 0)
            {
                CreatePrivateDirectory(customBundleOutputPath);
            }

            foreach (var bundleFactory in certificateTrustConfiguration.CustomBundlesFactories)
            {
                var bundleBytes = await bundleFactory.Value(
                    certificateTrustConfiguration.Certificates,
                    cancellationToken).ConfigureAwait(false);
                File.WriteAllBytes(Path.Join(customBundleOutputPath, bundleFactory.Key), bundleBytes);
            }
        }

        if (configuration.TryGetAdditionalData<HttpsCertificateExecutionConfigurationData>(out var tlsCertificateConfiguration))
        {
            var thumbprint = tlsCertificateConfiguration.Certificate.Thumbprint;
            var publicCertificatePem = tlsCertificateConfiguration.Certificate.ExportCertificatePem();
            var (keyPem, pfxBytes) = await DeveloperCertificateService.GetKeyMaterialAsync(
                certificate: tlsCertificateConfiguration.Certificate,
                password: tlsCertificateConfiguration.Password,
                needKeyPem: tlsCertificateConfiguration.IsKeyPathReferenced || tlsCertificateConfiguration.IsCertificateWithKeyPathReferenced,
                needPfx: tlsCertificateConfiguration.IsPfxPathReferenced,
                cancellationToken).ConfigureAwait(false);

            CreatePrivateDirectory(baseServerAuthOutputPath);
            File.WriteAllText(Path.Join(baseServerAuthOutputPath, $"{thumbprint}.crt"), publicCertificatePem);

            if (keyPem is not null)
            {
                var keyBytes = Encoding.ASCII.GetBytes(keyPem);
                File.WriteAllBytes(Path.Join(baseServerAuthOutputPath, $"{thumbprint}.key"), keyBytes);
                if (tlsCertificateConfiguration.IsCertificateWithKeyPathReferenced)
                {
                    File.WriteAllText(
                        Path.Join(baseServerAuthOutputPath, $"{thumbprint}.pem"),
                        new([.. keyPem, '\n', .. publicCertificatePem]));
                }

                Array.Clear(keyPem, 0, keyPem.Length);
                Array.Clear(keyBytes, 0, keyBytes.Length);
            }

            if (pfxBytes is not null)
            {
                File.WriteAllBytes(Path.Join(baseServerAuthOutputPath, $"{thumbprint}.pfx"), pfxBytes);
                Array.Clear(pfxBytes, 0, pfxBytes.Length);
            }
        }

        return new(configuration, pemCertificates);
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            // Certificate keys and custom trust bundles determine the process's identity and trust roots. Persistent
            // executables can place these outside the session directory, so do not rely on the ambient umask.
            Directory.CreateDirectory(
                path,
                UnixFileMode.UserExecute | UnixFileMode.UserWrite | UnixFileMode.UserRead);
        }
    }

    private string GetCertificatesRootDirectory(
        RenderedModelResource<Executable> renderedResource,
        Executable executable)
    {
        if (renderedResource.ModelResource.GetLifetimeType() == Lifetime.Persistent)
        {
            return Path.Join(_aspireStore.BasePath, "dcp", "executables", executable.Metadata.Name, "certificates");
        }

        return Path.Join(_locations.DcpSessionDir, executable.Metadata.Name);
    }
}
