// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable CA1852 // DispatchProxy classes can't be sealed

using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using NuGet.Packaging.Signing;

namespace Aspire.Managed.NuGet;

/// <summary>
/// Initializes NuGet's X509 trust store from embedded trusted root PEM certificates
/// for package signature verification on Linux, without writing to disk.
/// </summary>
internal static class TrustedRootsHelper
{
    private static bool s_initialized;

    /// <summary>
    /// Initializes the NuGet trust store with embedded trusted root certificates.
    /// On Linux, when DOTNET_NUGET_SIGNATURE_VERIFICATION is set to "true", NuGet requires
    /// certificate bundles for signature verification. The .NET SDK ships these as PEM files
    /// in its trustedroots directory, but aspire-managed is a single-file app without access
    /// to the SDK's directory structure. This method loads embedded PEM resources in memory
    /// and uses DispatchProxy to create IX509ChainFactory implementations that NuGet's trust
    /// store can use.
    /// </summary>
    /// <remarks>
    /// TODO: Remove this once NuGet supports a public API for configuring the trust store,
    /// or when it supports single-file apps. See https://github.com/dotnet/aspire/issues/15282.
    /// </remarks>
    public static void InitializeTrustStore()
    {
        if (s_initialized)
        {
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            // On Windows, NuGet uses the system certificate store directly.
            // On macOS, matching .NET SDK behavior which only enables this on Linux.
            return;
        }

        var envValue = Environment.GetEnvironmentVariable("DOTNET_NUGET_SIGNATURE_VERIFICATION");
        if (!(bool.TryParse(envValue, out var enabled) && enabled))
        {
            // If DOTNET_NUGET_SIGNATURE_VERIFICATION is not set to "true", NuGet won't
            // perform signature verification on Linux, so there's no need to initialize
            // the trust store.
            return;
        }

        try
        {
            InitializeTrustStoreFromEmbeddedResources();
            s_initialized = true;
        }
        catch (Exception ex)
        {
            // Log but don't fail the restore. If trust store initialization fails,
            // NuGet may still work if signature verification is not required or if
            // the system has its own certificate bundles.
            Console.Error.WriteLine($"WARNING: Failed to initialize NuGet trust store from embedded certificates: {ex}");
        }
    }

    private static void InitializeTrustStoreFromEmbeddedResources()
    {
        var nugetPackagingAssembly = typeof(X509TrustStore).Assembly;

        // Resolve internal NuGet types needed for DispatchProxy creation
        var chainFactoryInterfaceType = nugetPackagingAssembly.GetType("NuGet.Packaging.Signing.IX509ChainFactory");
        var chainInterfaceType = nugetPackagingAssembly.GetType("NuGet.Packaging.Signing.IX509Chain");

        if (chainFactoryInterfaceType is null || chainInterfaceType is null)
        {
            Console.Error.WriteLine("WARNING: Could not find IX509ChainFactory or IX509Chain types in NuGet.Packaging.");
            return;
        }

        // Set up code signing trust store
        SetTrustStoreFactory(
            "SetCodeSigningX509ChainFactory",
            "codesignctl.pem",
            chainFactoryInterfaceType,
            chainInterfaceType);

        // Set up timestamping trust store
        SetTrustStoreFactory(
            "SetTimestampingX509ChainFactory",
            "timestampctl.pem",
            chainFactoryInterfaceType,
            chainInterfaceType);
    }

    private static void SetTrustStoreFactory(
        string setterMethodName,
        string resourceName,
        Type chainFactoryInterfaceType,
        Type chainInterfaceType)
    {
        var certificates = LoadCertificatesFromResource(resourceName);
        if (certificates is null || certificates.Count == 0)
        {
            Console.Error.WriteLine($"WARNING: No certificates loaded from embedded resource: {resourceName}");
            return;
        }

        // Create IX509ChainFactory proxy via DispatchProxy
        var factory = ChainFactoryDispatchProxy.CreateFactory(
            chainFactoryInterfaceType, chainInterfaceType, certificates);

        // Call the setter on X509TrustStore to register the factory
        var setter = typeof(X509TrustStore).GetMethod(
            setterMethodName,
            BindingFlags.NonPublic | BindingFlags.Static);

        if (setter is null)
        {
            Console.Error.WriteLine($"WARNING: Could not find {setterMethodName} on X509TrustStore.");
            return;
        }

        setter.Invoke(null, [factory]);
    }

    private static X509Certificate2Collection? LoadCertificatesFromResource(string resourceName)
    {
        using var stream = typeof(TrustedRootsHelper).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        var pemContents = reader.ReadToEnd();

        var certificates = new X509Certificate2Collection();
        certificates.ImportFromPem(pemContents);
        return certificates;
    }
}

/// <summary>
/// DispatchProxy that implements NuGet's internal IX509ChainFactory interface.
/// Creates X509Chain instances configured with custom root trust using embedded certificates.
/// </summary>
internal class ChainFactoryDispatchProxy : DispatchProxy
{
    private X509Certificate2Collection _certificates = [];
    private Type _chainInterfaceType = null!;

    internal static object CreateFactory(
        Type chainFactoryInterfaceType,
        Type chainInterfaceType,
        X509Certificate2Collection certificates)
    {
        var proxy = (ChainFactoryDispatchProxy)Create(chainFactoryInterfaceType, typeof(ChainFactoryDispatchProxy));

        proxy._certificates = certificates;
        proxy._chainInterfaceType = chainInterfaceType;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        // IX509ChainFactory has a single method: IX509Chain Create()
        if (targetMethod?.Name == "Create")
        {
            return CreateChain();
        }

        throw new NotSupportedException($"Method '{targetMethod?.Name}' is not supported on IX509ChainFactory proxy. This may indicate a NuGet.Packaging version mismatch — the library may have added new interface members.");
    }

    private object CreateChain()
    {
        // Create an IX509Chain proxy that wraps an X509Chain with custom root trust
        return ChainDispatchProxy.CreateChain(_chainInterfaceType, _certificates);
    }
}

/// <summary>
/// DispatchProxy that implements NuGet's internal IX509Chain interface.
/// Wraps an X509Chain configured with CustomRootTrust mode.
/// </summary>
internal class ChainDispatchProxy : DispatchProxy
{
    private readonly X509Chain _chain = new();

    internal static object CreateChain(
        Type chainInterfaceType,
        X509Certificate2Collection certificates)
    {
        var proxy = (ChainDispatchProxy)Create(chainInterfaceType, typeof(ChainDispatchProxy));

        proxy._chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        proxy._chain.ChainPolicy.CustomTrustStore.AddRange(certificates);
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        return targetMethod?.Name switch
        {
            "Build" => Build((X509Certificate2)args![0]!),
            "Dispose" => Dispose(),
            "get_ChainElements" => _chain.ChainElements,
            "get_ChainPolicy" => _chain.ChainPolicy,
            "get_ChainStatus" => _chain.ChainStatus,
            "get_PrivateReference" => _chain,
            "get_AdditionalContext" => (global::NuGet.Common.ILogMessage?)null,
            _ => throw new NotSupportedException($"Method '{targetMethod?.Name}' is not supported on IX509Chain proxy. This may indicate a NuGet.Packaging version mismatch — the library may have added new interface members.")
        };
    }

    private bool Build(X509Certificate2 certificate)
    {
        return _chain.Build(certificate);
    }

    private object? Dispose()
    {
        _chain.Dispose();
        return null;
    }
}
