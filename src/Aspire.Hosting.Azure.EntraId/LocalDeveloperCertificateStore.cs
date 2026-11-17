// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Aspire.Hosting.ApplicationModel;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Persists one short-lived signing certificate per registration in the AppHost's store.
/// </summary>
internal sealed class LocalDeveloperCertificateStore(IAspireStore store, TimeProvider timeProvider, ILogger<LocalDeveloperCertificateStore> logger)
{
    internal string GetPath(string resourceName) => Path.Combine(store.BasePath, "dev-certs", "Entra", $"{resourceName}.pfx");

    public async Task<LocalDeveloperCertificate> GetCertificateAsync(string resourceName, CancellationToken cancellationToken)
    {
        var path = GetPath(resourceName);
        var directory = Path.GetDirectoryName(path)!;
        DirectoryHelper.CreateWithOwnerOnlyPermissions(directory);
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                identity.User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
        }

        // File sharing coordinates separate AppHost processes as well as threads. The lock file is stable;
        // deleting it would let another process lock a different inode while a waiter holds the original.
        using var fileLock = await AcquireLockAsync(path + ".lock", cancellationToken).ConfigureAwait(false);
        var temporaryPath = path + ".tmp";
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        var now = timeProvider.GetUtcNow();
        if (File.Exists(path))
        {
            ProtectFile(path);
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            try
            {
                // macOS doesn't support ephemeral PFX imports and throws PlatformNotSupportedException; there the
                // key is imported into a temporary keychain that is cleaned up when the certificate is disposed.
                var keyStorageFlags = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
                using var certificate = X509CertificateLoader.LoadPkcs12(bytes, null, keyStorageFlags);
                using var key = certificate.GetRSAPrivateKey();
                // Exercise the private key rather than trusting HasPrivateKey on a damaged/inaccessible key.
                _ = key?.SignData([0], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                // NotBefore/NotAfter are local times. Compare UTC values so a daylight-saving transition inside
                // the validity period doesn't make a 48-hour certificate look longer than it is.
                var notBefore = certificate.NotBefore.ToUniversalTime();
                var notAfter = certificate.NotAfter.ToUniversalTime();
                if (key is { KeySize: >= 2048 } &&
                    certificate.Subject == $"CN=Aspire Entra {resourceName}" &&
                    certificate.Issuer == certificate.Subject &&
                    notAfter - notBefore <= TimeSpan.FromDays(2) &&
                    notBefore <= now.UtcDateTime &&
                    notAfter > now.AddDays(1).UtcDateTime)
                {
                    return new LocalDeveloperCertificate(certificate, bytes);
                }
            }
            catch (CryptographicException)
            {
                logger.LogWarning("The local developer certificate for {ResourceName} could not be loaded. Generating a replacement.", resourceName);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }

            logger.LogInformation("Replacing the local developer certificate for {ResourceName} because it is incompatible or has one day or less remaining.", resourceName);
        }

        using var generated = CertificateFactory.CreateSelfSignedCertificate(
            new X500DistinguishedName($"CN=Aspire Entra {resourceName}"),
            [new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true)],
            now,
            now.AddDays(2));
        var pfx = generated.Export(X509ContentType.Pfx);
        try
        {
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows())
            {
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var file = new FileStream(temporaryPath, fileOptions))
            {
                ProtectFile(temporaryPath);
                await file.WriteAsync(pfx, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            // Replace the same file atomically. Retain the new material on Graph failure so retries use the
            // same public key, and never leave a growing collection of private-key generations on disk.
            File.Move(temporaryPath, path, overwrite: true);
            return new LocalDeveloperCertificate(generated, pfx);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
            File.Delete(temporaryPath);
        }
    }

    private static void ProtectFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            // Retry sharing/lock violations and Unix EAGAIN/EWOULDBLOCK, not arbitrary I/O failures.
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 ||
                (!OperatingSystem.IsWindows() && (ex.HResult & 0xffff) is 11 or 35))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// Holds the matching public provisioning value and secret runtime credential.
/// </summary>
internal sealed class LocalDeveloperCertificate
{
    public LocalDeveloperCertificate(X509Certificate2 certificate, byte[] pfx)
    {
        Base64EncodedValue = Convert.ToBase64String(pfx);
        PublicCertificate = Convert.ToBase64String(certificate.RawData);
        Thumbprint = Convert.ToBase64String(certificate.GetCertHash());
        NotBefore = new DateTimeOffset(certificate.NotBefore.ToUniversalTime());
        NotAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
    }

    public string Base64EncodedValue { get; }
    public string PublicCertificate { get; }
    public string Thumbprint { get; }
    public DateTimeOffset NotBefore { get; }
    public DateTimeOffset NotAfter { get; }
}
