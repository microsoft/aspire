// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Radius;

/// <summary>
/// The <c>kind</c> vocabulary of the <c>Radius.Security/secrets</c> resource type, and the data
/// keys each kind's reference recipes require.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately separate from <see cref="RadiusSecretStoreType"/>, which describes the
/// legacy <c>Applications.Core/secretStores</c> <c>properties.type</c>. The two vocabularies
/// overlap but are not the same: the legacy type spells the certificate case <c>certificate</c>,
/// while the replacement type splits it into <c>certificate-pem</c> and <c>certificate-pkcs12</c>
/// and does not accept the bare spelling at all. Sharing one table would both miss
/// <c>certificate-pem</c> and treat the invalid <c>certificate</c> as recognized.
/// </para>
/// <para>
/// The kind enum is declared by the built-in resource-type manifest, and the per-kind required
/// keys are enforced by the reference recipes rather than by the control plane:
/// <see href="https://github.com/radius-project/radius/blob/v0.60.0/deploy/manifest/built-in-providers/self-hosted/secrets.yaml"/>
/// and
/// <see href="https://github.com/radius-project/resource-types-contrib/blob/main/Security/secrets/README.md"/>.
/// </para>
/// </remarks>
internal static class RadiusSecuritySecretKinds
{
    /// <summary>The default <c>kind</c> when the property is omitted.</summary>
    internal const string Generic = "generic";

    /// <summary>
    /// The bare <c>certificate</c> spelling, which is valid on the <em>legacy</em>
    /// <c>Applications.Core/secretStores</c> type but is not a member of this type's enum. Carrying
    /// it over is the most likely migration mistake, so it is named here to be rejected with an
    /// actionable message rather than passed through as an unrecognized kind.
    /// </summary>
    internal const string LegacyCertificate = "certificate";

    /// <summary>The replacement for the legacy bare <c>certificate</c> kind.</summary>
    internal const string CertificatePem = "certificate-pem";

    /// <summary>
    /// The legacy <c>raw</c> encoding, valid on <c>Applications.Core/secretStores</c> and absent
    /// from this type's <c>encoding</c> enum, which is <c>string</c>/<c>base64</c>. Named here for
    /// the same reason as <see cref="LegacyCertificate"/>: it is a migration mistake rather than a
    /// value a newer control plane might introduce.
    /// </summary>
    internal const string LegacyRawEncoding = "raw";

    /// <summary>
    /// The complete <c>kind</c> enum of the <c>Radius.Security/secrets</c> type as pinned by
    /// <see cref="RadiusBicepExtension.Version"/>.
    /// </summary>
    /// <remarks>
    /// The enum is closed, and it is enforced by the <em>Bicep compiler</em> rather than only by the
    /// control plane: the generated <c>bicepconfig.json</c> resolves <c>extension radius</c> to
    /// <c>br:biceptypes.azurecr.io/radius:0.60</c>, whose <c>types.json</c> models <c>kind</c> as a
    /// union of string literals with no bare string element. A value outside the union therefore
    /// fails <c>bicep build</c> during <c>rad deploy</c>, and a newer control plane cannot make it
    /// valid, because the compile step reads the pinned type definitions. Publishing an unknown
    /// literal would only produce an artifact that cannot deploy, so it is rejected here where the
    /// message can name the offending resource.
    /// <see href="https://github.com/radius-project/radius/blob/v0.60.0/hack/bicep-types-radius/generated/radius/radius.security/2025-08-01-preview/types.json"/>
    /// </remarks>
    internal static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Generic,
        CertificatePem,
        "certificate-pkcs12",
        "basicAuthentication",
        "awsIRSA",
        "azureWorkloadIdentity",
    };

    /// <summary>
    /// The complete <c>encoding</c> enum of a <c>Radius.Security/secrets</c> data entry, closed for
    /// the same reason and by the same mechanism as <see cref="All"/>.
    /// </summary>
    internal static IReadOnlySet<string> Encodings { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "string",
        "base64",
    };

    /// <summary>
    /// Returns the data keys the reference recipes require for <paramref name="kind"/>.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> for a kind with no documented required keys — <c>generic</c>
    /// and <c>certificate-pkcs12</c> (the reference recipe documents no per-key contract for it).
    /// Membership in the enum itself is checked separately against <see cref="All"/>.
    /// </remarks>
    internal static bool TryGetRequiredKeys(string? kind, out IReadOnlyList<string> requiredKeys)
    {
        requiredKeys = kind switch
        {
            CertificatePem => ["tls.crt", "tls.key"],
            "basicAuthentication" => ["username", "password"],
            "azureWorkloadIdentity" => ["clientId", "tenantId"],
            "awsIRSA" => ["roleARN"],
            _ => [],
        };

        return requiredKeys.Count > 0;
    }
}
