// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TypeSystem;

namespace Aspire.Hosting.RemoteHost.Language;

/// <summary>
/// Describes the integration-host commands returned by an optional language-provider JSON hook.
/// </summary>
internal sealed class IntegrationHostSpec
{
    /// <summary>
    /// Gets the command to start the integration host.
    /// Supports <c>{entryPoint}</c> placeholder for the host entry file path.
    /// </summary>
    public required CommandSpec Execute { get; init; }

    /// <summary>
    /// Gets the command to install the integration host package's dependencies
    /// (e.g. <c>npm install</c>) before the host is spawned. Null if no install
    /// step is required for this language.
    /// </summary>
    public CommandSpec? InstallDependencies { get; init; }
}
