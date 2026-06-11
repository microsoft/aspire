// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography.X509Certificates;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// An annotation that indicates a resource wants to manage how custom certificate trust is configured.
/// </summary>
/// <param name="callback">The callback used to customize certificate trust for the resource.</param>
public sealed class CertificateTrustConfigurationCallbackAnnotation(Func<CertificateTrustConfigurationCallbackAnnotationContext, Task> callback) : IResourceAnnotation
{
    /// <summary>
    /// Gets the callback to invoke to populate or modify the certificate authority collection.
    /// </summary>
    public Func<CertificateTrustConfigurationCallbackAnnotationContext, Task> Callback { get; } = callback ?? throw new ArgumentNullException(nameof(callback));
}

/// <summary>
/// Context provided to a <see cref="CertificateTrustConfigurationCallbackAnnotation"/> callback.
/// </summary>
public sealed class CertificateTrustConfigurationCallbackAnnotationContext
{
    /// <summary>
    /// Gets the <see cref="DistributedApplicationExecutionContext"/> for this session.
    /// </summary>
    public required DistributedApplicationExecutionContext ExecutionContext { get; init; }

    /// <summary>
    /// Gets the resource to which the annotation is applied.
    /// </summary>
    public required IResource Resource { get; init; }

    /// <summary>
    /// Gets the command line arguments associated with the callback context. Values can be either a string or a path
    /// value provider such as <see cref="CertificateBundlePath"/> or <see cref="CertificateDirectoriesPath"/>.
    /// </summary>
    /// <remarks>
    /// <example>
    /// <code language="csharp">
    /// builder.AddContainer("my-resource", "my-image:latest")
    ///    .WithCertificateTrustConfigurationCallback(ctx =>
    ///    {
    ///        ctx.Arguments.Add("--use-system-ca");
    ///        return Task.CompletedTask;
    ///    });
    /// </code>
    /// </example>
    /// </remarks>
    public required List<object> Arguments { get; init; }

    /// <summary>
    /// Gets the environment variables required to configure certificate trust for the resource.
    /// The dictionary key is the environment variable name; the value can be either a string or a path
    /// value provider such as <see cref="CertificateBundlePath"/> or <see cref="CertificateDirectoriesPath"/>.
    /// By default the environment will always include an entry for `SSL_CERT_DIR` and may include `SSL_CERT_FILE` if
    /// <see cref="CertificateTrustScope.Override"/> or <see cref="CertificateTrustScope.System"/> is configured.
    /// </summary>
    /// <remarks>
    /// <example>
    /// <code language="csharp">
    /// builder.AddContainer("my-resource", "my-image:latest")
    ///     .WithCertificateTrustConfigurationCallback(ctx =>
    ///     {
    ///         ctx.EnvironmentVariables["MY_CUSTOM_CERT_VAR"] = ctx.CertificateBundlePath;
    ///         ctx.EnvironmentVariables["CERTS_DIR"] = ctx.CertificateDirectoriesPath;
    ///         return Task.CompletedTask;
    ///     });
    /// </code>
    /// </example>
    /// </remarks>
    public required Dictionary<string, object> EnvironmentVariables { get; init; }

    /// <summary>
    /// A value provider that will resolve to a path to a custom certificate bundle.
    /// </summary>
    public required ReferenceExpression CertificateBundlePath { get; init; }

    /// <summary>
    /// A value provider that will resolve to paths containing individual certificates.
    /// </summary>
    public required ReferenceExpression CertificateDirectoriesPath { get; init; }

    /// <summary>
    /// Gets the <see cref="CertificateTrustScope"/> for the resource.
    /// </summary>
    public required CertificateTrustScope Scope { get; init; }

    /// <summary>
    /// Gets the <see cref="CancellationToken"/> that can be used to cancel the operation.
    /// </summary>
    public required CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Adds a custom certificate bundle to the callback context. The provided generator will be invoked during trust configuration and should return the bundle contents as a byte array.
    /// </summary>
    /// <param name="bundleGenerator">A function that generates the custom certificate bundle and returns the bundle contents as a byte array.</param>
    /// <returns>A <see cref="ReferenceExpression"/> that can be used to reference the custom bundle.</returns>
    /// <remarks>
    /// <example>
    /// <code language="csharp">
    /// builder.AddContainer("my-java-app", "my-image:latest")
    ///     .WithCertificateTrustConfiguration(ctx =>
    ///     {
    ///         ctx.EnvironmentVariables["JAVAX_NET_SSL_TRUSTSTORE"] = ctx.CreateCustomBundle((certs, ct) =>
    ///         {
    ///             var pkcs12Builder = new Pkcs12Builder();
    ///             var safeContents = new Pkcs12SafeContents();
    ///             foreach (var cert in certs)
    ///             {
    ///                 safeContents.AddCertificate(cert);
    ///             }
    ///             pkcs12Builder.AddSafeContentsUnencrypted(safeContents);
    ///             pkcs12Builder.SealWithMac(string.Empty, HashAlgorithmName.SHA256, 2048);
    ///             return Task.FromResult(pkcs12Builder.Encode());
    ///         });
    ///         return Task.CompletedTask;
    ///     });
    /// </code>
    /// </example>
    /// </remarks>
    [Experimental("ASPIRECERTIFICATES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public ReferenceExpression CreateCustomBundle(Func<X509Certificate2Collection, CancellationToken, Task<byte[]>> bundleGenerator)
    {
        ArgumentException.ThrowIfNullOrEmpty(RootCertificatesPath);

        var bundleId = Guid.NewGuid().ToString("N");
        CustomBundlesFactories[bundleId] = bundleGenerator;

        var bundleFilename = IsContainer ? $"{RootCertificatesPath}/bundles/{bundleId}" : Path.Join(RootCertificatesPath, "bundles", bundleId);
        var reference = ReferenceExpression.Create($"{bundleFilename}");

        return reference;
    }

    /// <summary>
    /// Gets the root path where certificates will be written for the resource.
    /// </summary>
    internal string? RootCertificatesPath { get; init; }

    /// <summary>
    /// Is this being generated for a container (requires Linux style paths)
    /// </summary>
    internal bool IsContainer { get; init; }

    /// <summary>
    /// Collection of custom certificate bundle generators added via the <see cref="CreateCustomBundle"/> method, keyed by the bundle's unique ID/filename under the root certificates path. The value is a function that generates the bundle contents as a byte array given a collection of X509 certificates.
    /// </summary>
    internal Dictionary<string, Func<X509Certificate2Collection, CancellationToken, Task<byte[]>>> CustomBundlesFactories { get; } = new();
}
