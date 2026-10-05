// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Rust;

/// <summary>
/// Captures the cargo build/run options configured through the <c>WithCargo*</c> fluent APIs, so that a single
/// default <see cref="RustCargoArgsCallbackAnnotation"/> registered by <c>AddRustApp</c> can translate them
/// into cargo command-line arguments at execution time, regardless of the order the WithCargo* methods were
/// called relative to each other.
/// </summary>
/// <remarks>
/// This annotation is also the single source of truth for publishing. Every property here changes which file
/// cargo writes into <c>target/</c>, so the generated Dockerfile reads these properties rather than trying to
/// re-interpret the raw argument list produced by <c>WithCargoArgs</c>.
/// </remarks>
internal sealed class RustCargoOptionsAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Gets or sets a value indicating whether cargo should build/run using the <c>--release</c> profile.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> means the resource expressed no preference, which run mode treats as cargo's
    /// own default (the <c>dev</c> profile) and publishing treats as <c>--release</c>.
    /// </remarks>
    public bool? ReleaseBuild { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether cargo should build/run with <c>--locked</c>.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> means the resource expressed no preference, which run mode treats as cargo's
    /// own default (the lock file may be updated) and publishing treats as <c>--locked</c> whenever a
    /// <c>Cargo.lock</c> exists, so a published image builds the exact dependency versions that were
    /// committed.
    /// </remarks>
    public bool? Locked { get; set; }

    /// <summary>
    /// Gets or sets the cargo features to enable via <c>--features</c>.
    /// </summary>
    public IReadOnlyList<string>? Features { get; set; }

    /// <summary>
    /// Gets or sets the binary target selected with <c>--bin</c>.
    /// </summary>
    public string? BinTarget { get; set; }

    /// <summary>
    /// Gets or sets the example target selected with <c>--example</c>.
    /// </summary>
    public string? Example { get; set; }

    /// <summary>
    /// Gets or sets the workspace package selected with <c>--package</c>.
    /// </summary>
    public string? Package { get; set; }

    /// <summary>
    /// Gets or sets the target triple selected with <c>--target</c>.
    /// </summary>
    public string? Target { get; set; }

    /// <summary>
    /// Gets or sets the manifest selected with <c>--manifest-path</c>.
    /// </summary>
    /// <remarks>
    /// Cargo otherwise discovers the manifest from its working directory, which is the app directory, so
    /// this stays <see langword="null"/> unless the caller redirected it.
    /// </remarks>
    public string? ManifestPath { get; set; }

    /// <summary>
    /// Gets or sets the named profile selected with <c>--profile</c>.
    /// </summary>
    /// <remarks>
    /// When set, this wins over <see cref="ReleaseBuild"/> because cargo rejects <c>--release</c> and
    /// <c>--profile</c> together.
    /// </remarks>
    public string? Profile { get; set; }
}
