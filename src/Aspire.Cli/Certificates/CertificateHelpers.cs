// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Certificates.Generation;

namespace Aspire.Cli.Certificates;

/// <summary>
/// Shared helper methods for certificate operations.
/// </summary>
internal static partial class CertificateHelpers
{
    /// <summary>
    /// The environment variable name for overriding the dev-certs OpenSSL certificate directory.
    /// </summary>
    internal const string DevCertsOpenSslCertDirEnvVar = "DOTNET_DEV_CERTS_OPENSSL_CERTIFICATE_DIRECTORY";

    private static readonly string s_defaultDevCertsTrustPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".aspnet",
        "dev-certs",
        "trust");

    /// <summary>
    /// Gets the dev-certs trust path, respecting the <c>DOTNET_DEV_CERTS_OPENSSL_CERTIFICATE_DIRECTORY</c> override.
    /// </summary>
    internal static string GetDevCertsTrustPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(DevCertsOpenSslCertDirEnvVar);
        return !string.IsNullOrEmpty(overridePath) ? overridePath : s_defaultDevCertsTrustPath;
    }

    /// <summary>
    /// Gets the system certificate directories by querying OpenSSL. Falls back to well-known
    /// locations (<c>/etc/ssl/certs</c> and <c>/etc/pki/tls/certs</c>) when OpenSSL is unavailable.
    /// </summary>
    internal static List<string> GetSystemCertificateDirectories()
    {
        var systemCertDirs = new List<string>();

        if (TryGetOpenSslDirectory(out var openSslDir))
        {
            var openSslCertsDir = Path.Combine(openSslDir, "certs");
            if (Directory.Exists(openSslCertsDir))
            {
                systemCertDirs.Add(openSslCertsDir);
            }
        }
        else
        {
            // Fallback to common locations if OpenSSL is not available or fails
            if (Directory.Exists("/etc/ssl/certs"))
            {
                systemCertDirs.Add("/etc/ssl/certs");
            }

            if (Directory.Exists("/etc/pki/tls/certs"))
            {
                systemCertDirs.Add("/etc/pki/tls/certs");
            }
        }

        return systemCertDirs;
    }

    /// <summary>
    /// Determines whether the specified <see cref="EnsureCertificateResult"/> represents a successful trust operation.
    /// </summary>
    /// <param name="result">The result to evaluate.</param>
    /// <returns><see langword="true"/> if the result represents success; otherwise, <see langword="false"/>.</returns>
    internal static bool IsSuccessfulTrustResult(EnsureCertificateResult result) =>
        result is EnsureCertificateResult.Succeeded
            or EnsureCertificateResult.ValidCertificatePresent
            or EnsureCertificateResult.ExistingHttpsCertificateTrusted
            or EnsureCertificateResult.NewHttpsCertificateTrusted;

    /// <summary>
    /// Tries to detect the OpenSSL directory by running 'openssl version -d'.
    /// Parses the OPENSSLDIR value from the output (e.g. OPENSSLDIR: "/usr/lib/ssl").
    /// </summary>
    /// <param name="openSslDir">The detected OpenSSL directory path if successful.</param>
    /// <returns><see langword="true"/> if the directory was detected; otherwise, <see langword="false"/>.</returns>
    internal static bool TryGetOpenSslDirectory([NotNullWhen(true)] out string? openSslDir)
    {
        openSslDir = null;

        try
        {
            var processInfo = new ProcessStartInfo("openssl", "version -d")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            if (process is null)
            {
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (process.ExitCode != 0)
            {
                return false;
            }

            var match = OpenSslVersionRegex().Match(stdout);
            if (!match.Success)
            {
                return false;
            }

            openSslDir = match.Groups[1].Value;
            return true;
        }
        catch
        {
            // openssl may not be installed — silently fail
            return false;
        }
    }

    [GeneratedRegex("OPENSSLDIR:\\s*\"([^\"]+)\"")]
    internal static partial Regex OpenSslVersionRegex();
}
