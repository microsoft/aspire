// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

/// <summary>An application across checkouts, independent of branch names and process lifetimes.</summary>
internal readonly record struct TrayApplicationKey(string GitDirectory, string AppHostPath);

/// <summary>Immutable repository and checkout metadata, independent of tray interaction state.</summary>
internal sealed record TrayGitCheckout(TrayApplicationKey Application, string Directory, string Label, string CanonicalAppHostPath);
