// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.NuGet;

/// <summary>
/// Interface for commands that define which types of package metadata should be prefetched.
/// </summary>
internal interface IPackageMetaPrefetchingCommand
{
    /// <summary>
    /// Gets a value indicating whether template package metadata should be prefetched for this command.
    /// </summary>
    bool PrefetchesTemplatePackageMetadata { get; }

    /// <summary>
    /// Gets a value indicating whether CLI package metadata should be prefetched for this command.
    /// </summary>
    bool PrefetchesCliPackageMetadata { get; }
}
