// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Maui.Annotations;

/// <summary>
/// Annotation carrying the Android device a launch targets.
/// </summary>
/// <remarks>
/// <c>dotnet run</c> asks which device to use when the Android SDK lists more than one, and fails when it
/// cannot prompt, as under DCP. The build queue resolves <see cref="AdbTarget"/> to a device before each
/// launch, and the launch passes it as the <c>Device</c> MSBuild property so <c>dotnet run</c> skips the prompt.
/// </remarks>
internal sealed class MauiAndroidDeviceSelectionAnnotation(string adbTarget) : IResourceAnnotation
{
    /// <summary>
    /// Gets the adb device switches the resource targets, for example <c>-e</c>, <c>-d</c>, or <c>-s emulator-5554</c>.
    /// </summary>
    public string AdbTarget { get; } = adbTarget;

    /// <summary>
    /// Gets or sets the device resolved for the current launch, or <see langword="null"/> to let <c>dotnet run</c> choose.
    /// </summary>
    public string? SelectedDevice { get; set; }
}
