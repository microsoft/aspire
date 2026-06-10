// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Shared certificate utilities used by both ExecutableCreator and ContainerCreator.
/// </summary>
internal sealed class CertificateUtilities
{
    internal static List<PemCertificate> BuildPemCertificateList(X509Certificate2Collection certificates)
    {
        return certificates.Select(c => new PemCertificate
        {
            Thumbprint = c.Thumbprint,
            Contents = c.ExportCertificatePem(),
        }).DistinctBy(cert => cert.Thumbprint).ToList();
    }
}
