// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Security.Cryptography.X509Certificates;

namespace Aspire.Hosting.Tests.Utils;

#pragma warning disable ASPIRECERTIFICATES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
public sealed class TestDeveloperCertificateService(List<X509Certificate2> certificates, bool supportsContainerTrust, bool trustCertificate, bool tlsTerminate, bool latestCertificateIsUntrusted = false) : IDeveloperCertificateService
#pragma warning restore ASPIRECERTIFICATES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
{
    /// <inheritdoc />
    public ImmutableList<X509Certificate2> Certificates { get; } = certificates.ToImmutableList();

    /// <inheritdoc />
    public bool SupportsContainerTrust => supportsContainerTrust;

    /// <inheritdoc />
    public bool TrustCertificate => trustCertificate;

    /// <inheritdoc />
    public bool UseForHttps => !OperatingSystem.IsMacOS() && tlsTerminate && trustCertificate;

    /// <inheritdoc />
    public bool LatestCertificateIsUntrusted => latestCertificateIsUntrusted;
}
