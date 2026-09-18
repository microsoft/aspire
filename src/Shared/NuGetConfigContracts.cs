// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Shared;

internal sealed record NuGetSettingsResponse(
    [property: JsonPropertyName("ConfigPaths"), JsonRequired] string[] ConfigPaths,
    [property: JsonPropertyName("CacheIdentity"), JsonRequired] string CacheIdentity,
    [property: JsonPropertyName("Sources"), JsonRequired] NuGetSourceInfo[] Sources,
    [property: JsonPropertyName("SensitiveSourceValues"), JsonRequired] string[] SensitiveSourceValues,
    [property: JsonPropertyName("PackageSourceMappingEnabled"), JsonRequired] bool PackageSourceMappingEnabled,
    [property: JsonPropertyName("PackageSourceMappings"), JsonRequired] NuGetPackageSourceMapping[] PackageSourceMappings,
    [property: JsonPropertyName("DisabledPackageSourceKeys"), JsonRequired] string[] DisabledPackageSourceKeys,
    [property: JsonPropertyName("ReservedPackageSourceKeys"), JsonRequired] string[] ReservedPackageSourceKeys);

internal sealed record NuGetSourceInfo(
    [property: JsonPropertyName("Name"), JsonRequired] string Name,
    [property: JsonPropertyName("Identity"), JsonRequired] string Identity,
    [property: JsonPropertyName("IsEnabled"), JsonRequired] bool IsEnabled,
    [property: JsonPropertyName("HasCredentials"), JsonRequired] bool HasCredentials,
    [property: JsonPropertyName("HasClientCertificates"), JsonRequired] bool HasClientCertificates);

internal sealed record NuGetPackageSourceMapping(
    [property: JsonPropertyName("SourceKey"), JsonRequired] string SourceKey,
    [property: JsonPropertyName("Patterns"), JsonRequired] string[] Patterns);

internal sealed record NuGetConfigOverlayRequest(
    [property: JsonPropertyName("Sources"), JsonRequired] NuGetConfigSourceDefinition[] Sources,
    [property: JsonPropertyName("PackageSourceMappings"), JsonRequired] NuGetPackageSourceMapping[] PackageSourceMappings,
    [property: JsonPropertyName("ClearDisabledPackageSources"), JsonRequired] bool ClearDisabledPackageSources,
    [property: JsonPropertyName("DisabledPackageSourceKeys"), JsonRequired] string[] DisabledPackageSourceKeys,
    [property: JsonPropertyName("GlobalPackagesFolder")] string? GlobalPackagesFolder);

internal sealed record NuGetConfigSourceDefinition(
    [property: JsonPropertyName("Key"), JsonRequired] string Key,
    [property: JsonPropertyName("Source"), JsonRequired] string Source);
