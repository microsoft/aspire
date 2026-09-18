// <copyright file="ChaosTargetKind.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Chaos;

/// <summary>
/// The resource-type classification of a meshed edge's target, used to pick the fault
/// profile that resource-aware random chaos samples for it.
/// </summary>
internal enum ChaosTargetKind
{
    /// <summary>One of the author's own services (project / author container).</summary>
    Service,

    /// <summary>Azure Cosmos DB (emulator).</summary>
    Cosmos,

    /// <summary>Azure Storage queue (Azurite).</summary>
    StorageQueue,

    /// <summary>Azure Key Vault.</summary>
    KeyVault,
}

/// <summary>
/// Stamped by the mesh edge providers on each proxy they create, recording the resource
/// type of the proxy's upstream so <c>WithRandomChaos</c> can assign the matching fault
/// profile without any per-edge configuration.
/// </summary>
internal sealed class ChaosTargetKindAnnotation : IResourceAnnotation
{
    public ChaosTargetKindAnnotation(ChaosTargetKind kind) => this.Kind = kind;

    public ChaosTargetKind Kind { get; }
}

/// <summary>Maps a <see cref="ChaosTargetKind"/> to the id of the fault profile that models it.</summary>
internal static class ChaosFaultProfiles
{
    public const string Service = "service.http";
    public const string Cosmos = "azure.cosmos";
    public const string StorageQueue = "azure.storagequeue";
    public const string KeyVault = "azure.keyvault";

    public static string ForKind(ChaosTargetKind kind) => kind switch
    {
        ChaosTargetKind.Cosmos => Cosmos,
        ChaosTargetKind.StorageQueue => StorageQueue,
        ChaosTargetKind.KeyVault => KeyVault,
        _ => Service,
    };

    public static string ForResource(IResource resource)
        => resource.Annotations.OfType<ChaosTargetKindAnnotation>().FirstOrDefault() is { } annotation
            ? ForKind(annotation.Kind)
            : Service;
}
