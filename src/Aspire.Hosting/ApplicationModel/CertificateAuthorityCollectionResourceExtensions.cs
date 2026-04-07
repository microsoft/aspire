// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography.X509Certificates;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides extension methods for <see cref="CertificateAuthorityCollection"/>.
/// </summary>
public static class CertificateAuthorityCollectionResourceExtensions
{
    /// <summary>
    /// Adds a new <see cref="CertificateAuthorityCollection"/> to the application model. This resource is
    /// intended for local development run time configuration and is excluded from published artifacts.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the certificate authority collection resource.</param>
    /// <returns>An <see cref="IResourceBuilder{CertificateAuthorityCollectionResource}"/> instance.</returns>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "All companion With* methods require X509Certificate2 — the resource would be unusable in polyglot hosts (zero configurable methods).")]
    public static IResourceBuilder<CertificateAuthorityCollection> AddCertificateAuthorityCollection(this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        var resource = new CertificateAuthorityCollection(name);
        return builder.AddResource(resource)
            .WithIconName("Certificate")
            .ExcludeFromManifest()
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = nameof(CertificateAuthorityCollection),
                Properties = [],
                IsHidden = true,
                State = KnownResourceStates.Active
            });
    }

    /// <summary>
    /// Adds a certificate to the <see cref="CertificateAuthorityCollection.Certificates"/> collection.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{CertificateAuthorityCollectionResource}"/>.</param>
    /// <param name="certificate">The certificate to add.</param>
    /// <returns>The updated <see cref="IResourceBuilder{CertificateAuthorityCollectionResource}"/>.</returns>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "Uses X509Certificate2 which is not ATS-compatible.")]
    public static IResourceBuilder<CertificateAuthorityCollection> WithCertificate(this IResourceBuilder<CertificateAuthorityCollection> builder, X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(certificate);

        builder.Resource.Certificates.Add(certificate);
        return builder;
    }

    /// <summary>
    /// Adds a collection of certificates to the <see cref="CertificateAuthorityCollection.Certificates"/> collection.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{CertificateAuthorityCollectionResource}"/>.</param>
    /// <param name="certificates">The collection of certificates to add.</param>
    /// <returns>The updated <see cref="IResourceBuilder{CertificateAuthorityCollectionResource}"/>.</returns>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "Uses X509Certificate2Collection which is not ATS-compatible.")]
    public static IResourceBuilder<CertificateAuthorityCollection> WithCertificates(this IResourceBuilder<CertificateAuthorityCollection> builder, X509Certificate2Collection certificates)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(certificates);

        builder.Resource.Certificates.AddRange(certificates);
        return builder;
    }

    /// <summary>
    /// Adds a collection of certificates to the <see cref="CertificateAuthorityCollection.Certificates"/> collection.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{CertificateAuthorityCollectionResource}"/>.</param>
    /// <param name="certificates">The collection of certificates to add.</param>
    /// <returns>The updated <see cref="IResourceBuilder{CertificateAuthorityCollectionResource}"/>.</returns>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "Uses X509Certificate2 which is not ATS-compatible.")]
    public static IResourceBuilder<CertificateAuthorityCollection> WithCertificates(this IResourceBuilder<CertificateAuthorityCollection> builder, IEnumerable<X509Certificate2> certificates)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(certificates);

        builder.Resource.Certificates.AddRange(certificates.ToArray());
        return builder;
    }

    /// <summary>
    /// Adds certificates from a certificate store to the <see cref="CertificateAuthorityCollection.Certificates"/> collection.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{CertificateAuthorityCollectionResource}"/>.</param>
    /// <param name="storeName">The name of the certificate store.</param>
    /// <param name="storeLocation">The location of the certificate store.</param>
    /// <param name="filter">An optional filter to apply to the certificates.</param>
    /// <returns>The updated <see cref="IResourceBuilder{CertificateAuthorityCollectionResource}"/>.</returns>
    /// <remarks>
    /// <para>This method is not available in polyglot app hosts.</para>
    /// <example>
    /// This example adds all certificates from the "Root" store in the "LocalMachine" location.
    /// <code language="csharp">
    /// builder.AddCertificateAuthorityCollection("my-ca")
    ///     .WithCertificatesFromStore(StoreName.Root, StoreLocation.LocalMachine);
    /// </code>
    /// </example>
    /// <example>
    /// This example adds only certificates that are not expired from the "My" store in the "CurrentUser" location.
    /// <code language="csharp">
    /// builder.AddCertificateAuthorityCollection("my-ca")
    ///     .WithCertificatesFromStore(StoreName.My, StoreLocation.CurrentUser, c => c.NotAfter &gt; DateTime.UtcNow);
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExportIgnore(Reason = "Uses StoreName and StoreLocation which are not ATS-compatible.")]
    public static IResourceBuilder<CertificateAuthorityCollection> WithCertificatesFromStore(this IResourceBuilder<CertificateAuthorityCollection> builder, StoreName storeName, StoreLocation storeLocation, Func<X509Certificate2, bool>? filter = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        using var store = new X509Store(storeName, storeLocation);
        store.Open(OpenFlags.ReadOnly);
        var certificates = store.Certificates as IEnumerable<X509Certificate2>;
        if (filter != null)
        {
            certificates = certificates.Where(filter);
        }
        builder.Resource.Certificates.AddRange(certificates.ToArray());
        return builder;
    }

    /// <summary>
    /// Adds certificates from a PEM file to the <see cref="CertificateAuthorityCollection.Certificates"/> collection.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{CertificateAuthorityCollection}"/>.</param>
    /// <param name="pemFilePath">The path to the PEM file.</param>
    /// <param name="filter">An optional filter to apply to the loaded certificates before they are added to the collection.</param>
    /// <returns>The updated <see cref="IResourceBuilder{CertificateAuthorityCollection}"/>.</returns>
    /// <remarks>
    /// <para>This method is not available in polyglot app hosts.</para>
    /// <example>
    /// This example adds certificates from a PEM file located at "../path/to/certificates.pem".
    /// <code language="csharp">
    /// builder.AddCertificateAuthorityCollection("my-ca")
    ///     .WithCertificatesFromFile("../path/to/certificates.pem");
    /// </code>
    /// </example>
    /// <example>
    /// This example adds only certificates that are not expired from a PEM file located at "../path/to/certificates.pem".
    /// <code language="csharp">
    /// builder.AddCertificateAuthorityCollection("my-ca")
    ///     .WithCertificatesFromFile("../path/to/certificates.pem", c => c.NotAfter &gt; DateTime.UtcNow);
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExportIgnore(Reason = "Uses Func<X509Certificate2, bool> which is not ATS-compatible.")]
    public static IResourceBuilder<CertificateAuthorityCollection> WithCertificatesFromFile(this IResourceBuilder<CertificateAuthorityCollection> builder, string pemFilePath, Func<X509Certificate2, bool>? filter = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(pemFilePath);

        var certificates = new X509Certificate2Collection();
        certificates.ImportFromPemFile(pemFilePath);
        if (filter != null)
        {
            builder.WithCertificates(certificates.Where(filter).ToArray());
        }
        else
        {
            builder.WithCertificates(certificates);
        }

        return builder;
    }
}
