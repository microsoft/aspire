// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Aspire.Cli.Utils;

/// <summary>
/// Provides the path to the running Aspire CLI process.
/// </summary>
internal interface IProcessPathProvider
{
    string? ProcessPath { get; }

    bool IsNativeAot { get; }
}

internal sealed class EnvironmentProcessPathProvider : IProcessPathProvider
{
    public string? ProcessPath => Environment.ProcessPath;

    // PublishAot also disables dynamic code in the runtimeconfig of managed builds.
    // Those still have a CLI assembly on disk; NativeAOT assemblies have no location.
    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "An empty assembly location is intentional here: a physical CLI assembly identifies a managed development entrypoint.")]
    public bool IsNativeAot => !RuntimeFeature.IsDynamicCodeSupported && string.IsNullOrEmpty(typeof(EnvironmentProcessPathProvider).Assembly.Location);
}
