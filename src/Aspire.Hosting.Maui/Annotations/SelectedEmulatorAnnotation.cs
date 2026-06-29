// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Maui.Annotations;

/// <summary>
/// Stores the run-mode Android emulator or iOS simulator selected for a MAUI platform resource.
/// </summary>
internal sealed class SelectedEmulatorAnnotation(MauiTargetSelectionKind targetKind) : IResourceAnnotation
{
    /// <summary>
    /// Gets the kind of target this annotation selects.
    /// </summary>
    public MauiTargetSelectionKind TargetKind { get; } = targetKind;

    /// <summary>
    /// Gets or sets the selected emulator or simulator identifier.
    /// </summary>
    public string? SelectedId { get; set; }
}

internal enum MauiTargetSelectionKind
{
    AndroidEmulator,
    IOSSimulator
}
