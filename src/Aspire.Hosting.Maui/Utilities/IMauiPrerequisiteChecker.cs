// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Maui.Utilities;

/// <summary>
/// Checks whether a MAUI platform resource has a required local run-mode prerequisite.
/// </summary>
/// <remarks>
/// MAUI prerequisite checks intentionally do not use the hosting required-command validator. That validator
/// is warning-oriented and caches failures by command, while MAUI startup must fail before build/device work,
/// scope checks by platform/toolchain, and allow install-and-retry in the same AppHost after a missing workload
/// or tool is installed.
/// </remarks>
internal interface IMauiPrerequisiteChecker
{
    string Name { get; }

    string InstallHint { get; }

    string DocumentationUrl { get; }

    bool AppliesTo(IResource resource);

    string GetCacheKey(IResource resource);

    Task<MauiPrerequisiteCheckResult> CheckAsync(IResource resource, ILogger logger, CancellationToken cancellationToken);
}
