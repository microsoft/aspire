// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// An annotation that associates an HTTPS/TLS server certificate with a resource.
/// </summary>
[Experimental("ASPIRECERTIFICATES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class HttpsCertificateAnnotation : IResourceAnnotation
{
    private X509Certificate2? _certificate;
    private bool? _useDeveloperCertificate;

    /// <summary>
    /// Sets an <see cref="X509Certificate2"/> instance associated with this annotation.
    /// If a certificate is provided, it must have a private key; otherwise, an <see cref="ArgumentException"/> is thrown when setting the value.
    /// </summary>
    public X509Certificate2? Certificate
    {
        get => _certificate;
        init
        {
            if (value != null && _useDeveloperCertificate == true)
            {
                throw new ArgumentException("Cannot set both UseDeveloperCertificate and Certificate properties.", nameof(value));
            }

            if (value?.HasPrivateKey == false)
            {
                throw new ArgumentException("The provided certificate must have a private key.", nameof(value));
            }

            try
            {
                if (value != null && value.PublicKey == null)
                {
                    throw new ArgumentException("The provided certificate must have a valid public key.", nameof(value));
                }
            }
            catch (CryptographicException ex)
            {
                throw new ArgumentException("The provided certificate is invalid.", nameof(value), ex);
            }

            _certificate = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the resource should use a platform developer certificate for its key pair.
    /// </summary>
    public bool? UseDeveloperCertificate
    {
        get => _useDeveloperCertificate;
        init
        {
            _useDeveloperCertificate = value;
            if (value == true && _certificate != null)
            {
                throw new ArgumentException("Cannot set both UseDeveloperCertificate and Certificate properties.", nameof(value));
            }
        }
    }

    /// <summary>
    /// Gets or sets a parameter resource that contains the password for the private key of the certificate.
    /// </summary>
    public ParameterResource? Password { get; init; }
}