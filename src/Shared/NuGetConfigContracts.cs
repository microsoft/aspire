// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared;

internal sealed record NuGetSourceInfo(
    string Name,
    string Identity,
    bool IsEnabled,
    bool HasCredentials,
    bool HasClientCertificates);

internal sealed record NuGetPackageSourceMapping(
    string SourceKey,
    string[] Patterns);

internal sealed record NuGetConfigOverlayRequest(
    NuGetConfigSourceDefinition[] Sources,
    NuGetPackageSourceMapping[] PackageSourceMappings,
    bool ClearDisabledPackageSources,
    string[] DisabledPackageSourceKeys,
    string? GlobalPackagesFolder);

internal sealed record NuGetConfigSourceDefinition(
    string Key,
    string Source);
