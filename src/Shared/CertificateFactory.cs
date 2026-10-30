// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Aspire.Shared;

/// <summary>
/// Creates self-signed RSA certificates for local development.
/// </summary>
internal static class CertificateFactory
{
    private const int RSAMinimumKeySizeInBits = 2048;
    private const string SubjectKeyIdentifierOid = "2.5.29.14";
    private const string AuthorityKeyIdentifierOid = "2.5.29.35";

    public static X509Certificate2 CreateSelfSignedCertificate(
        X500DistinguishedName subject,
        IEnumerable<X509Extension> extensions,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter)
    {
        using var key = CreateKeyMaterial(RSAMinimumKeySizeInBits);

        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        foreach (var extension in extensions)
        {
            request.CertificateExtensions.Add(extension);
        }

        // OpenSSL uses SKI and AKI to distinguish certificates with the same subject when
        // building a trust chain. Preserve caller-supplied identifiers instead of duplicating them.
        // See https://www.rfc-editor.org/rfc/rfc5280#section-4.2.1.1 and section 4.2.1.2.
        if (!request.CertificateExtensions.Any(ext => ext.Oid?.Value is SubjectKeyIdentifierOid or AuthorityKeyIdentifierOid))
        {
            var subjectKeyIdentifier = new X509SubjectKeyIdentifierExtension(request.PublicKey, X509SubjectKeyIdentifierHashAlgorithm.Sha256, critical: false);
            var authorityKeyIdentifier = X509AuthorityKeyIdentifierExtension.CreateFromSubjectKeyIdentifier(subjectKeyIdentifier);

            request.CertificateExtensions.Add(subjectKeyIdentifier);
            request.CertificateExtensions.Add(authorityKeyIdentifier);
        }

        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private static RSA CreateKeyMaterial(int minimumKeySize)
    {
        var rsa = RSA.Create(minimumKeySize);
        if (rsa.KeySize < minimumKeySize)
        {
            rsa.Dispose();
            throw new InvalidOperationException($"Failed to create a key with a size of {minimumKeySize} bits");
        }

        return rsa;
    }
}
