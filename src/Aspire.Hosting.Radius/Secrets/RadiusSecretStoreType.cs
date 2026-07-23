// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS006 // The enum is experimental; the internal helper below consumes it.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Radius;

/// <summary>
/// The Radius secret-store type, mapping 1:1 to the
/// <c>Applications.Core/secretStores</c> <c>properties.type</c> string values.
/// The type drives required-key validation (<c>ASPIRERADIUS040</c>) and the
/// default per-key <c>encoding</c> (<c>ASPIRERADIUS047</c>).
/// </summary>
[Experimental("ASPIRERADIUS006", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public enum RadiusSecretStoreType
{
    /// <summary>An untyped secret store (Radius <c>generic</c>); no required keys; default encoding <c>raw</c>.</summary>
    Generic = 0,

    /// <summary>
    /// A TLS certificate store (Radius <c>certificate</c>, maps to
    /// <c>kubernetes.io/tls</c>). Requires <c>tls.crt</c> and <c>tls.key</c>;
    /// encoding must be <c>base64</c>.
    /// </summary>
    Certificate = 1,

    /// <summary>Basic-authentication credentials (Radius <c>basicAuthentication</c>). Requires <c>username</c> and <c>password</c>.</summary>
    BasicAuthentication = 2,

    /// <summary>Azure workload-identity credentials (Radius <c>azureWorkloadIdentity</c>). Requires <c>clientId</c> and <c>tenantId</c>.</summary>
    AzureWorkloadIdentity = 3,

    /// <summary>AWS IRSA credentials (Radius <c>awsIRSA</c>). Requires <c>roleARN</c>.</summary>
    AwsIrsa = 4,
}

/// <summary>
/// The per-key <c>encoding</c> of an inline Radius secret-store data value, mapping to the
/// <c>Applications.Core/secretStores</c> <c>data.&lt;key&gt;.encoding</c> string values.
/// </summary>
[Experimental("ASPIRERADIUS006", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public enum RadiusSecretStoreEncoding
{
    /// <summary>Raw UTF-8 value (Radius <c>raw</c>). Default for all types except <c>certificate</c>.</summary>
    Raw = 0,

    /// <summary>Base64-encoded value (Radius <c>base64</c>). Required for <c>certificate</c> stores.</summary>
    Base64 = 1,
}

/// <summary>
/// Internal helpers mapping <see cref="RadiusSecretStoreType"/> to the Radius
/// <c>type</c> string, its required keys, and its default encoding.
/// </summary>
internal static class RadiusSecretStoreTypeExtensions
{
    /// <summary>Returns the Radius <c>properties.type</c> string for <paramref name="type"/>.</summary>
    internal static string ToRadiusTypeString(this RadiusSecretStoreType type) => type switch
    {
        RadiusSecretStoreType.Generic => "generic",
        RadiusSecretStoreType.Certificate => "certificate",
        RadiusSecretStoreType.BasicAuthentication => "basicAuthentication",
        RadiusSecretStoreType.AzureWorkloadIdentity => "azureWorkloadIdentity",
        RadiusSecretStoreType.AwsIrsa => "awsIRSA",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown Radius secret-store type."),
    };

    /// <summary>Returns the Radius <c>encoding</c> string (<c>raw</c>/<c>base64</c>) for <paramref name="encoding"/>.</summary>
    internal static string ToRadiusEncodingString(this RadiusSecretStoreEncoding encoding) => encoding switch
    {
        RadiusSecretStoreEncoding.Raw => "raw",
        RadiusSecretStoreEncoding.Base64 => "base64",
        _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Unknown Radius secret-store encoding."),
    };

    /// <summary>Returns the keys Radius requires for <paramref name="type"/> (empty for <see cref="RadiusSecretStoreType.Generic"/>).</summary>
    internal static IReadOnlyList<string> RequiredKeys(this RadiusSecretStoreType type) => type switch
    {
        RadiusSecretStoreType.Certificate => ["tls.crt", "tls.key"],
        RadiusSecretStoreType.BasicAuthentication => ["username", "password"],
        RadiusSecretStoreType.AzureWorkloadIdentity => ["clientId", "tenantId"],
        RadiusSecretStoreType.AwsIrsa => ["roleARN"],
        _ => [],
    };

    /// <summary>
    /// The default per-key encoding for <paramref name="type"/> — <c>base64</c> for
    /// <see cref="RadiusSecretStoreType.Certificate"/> (Radius enforces it), <c>raw</c> otherwise.
    /// </summary>
    internal static string DefaultEncoding(this RadiusSecretStoreType type) =>
        type == RadiusSecretStoreType.Certificate ? "base64" : "raw";

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="encoding"/> is a valid choice for
    /// <paramref name="type"/>. Radius requires <c>base64</c> for <c>certificate</c> stores
    /// (rejecting <c>raw</c>); other types accept <c>raw</c> or <c>base64</c>.
    /// </summary>
    internal static bool IsValidEncoding(this RadiusSecretStoreType type, [NotNullWhen(true)] string? encoding)
    {
        if (string.IsNullOrWhiteSpace(encoding))
        {
            return false;
        }

        if (type == RadiusSecretStoreType.Certificate)
        {
            return string.Equals(encoding, "base64", StringComparison.Ordinal);
        }

        return encoding is "raw" or "base64";
    }
}
