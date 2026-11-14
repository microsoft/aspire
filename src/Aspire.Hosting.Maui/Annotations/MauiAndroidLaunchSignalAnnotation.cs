// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Maui.Annotations;

/// <summary>
/// Annotation carrying the path of the file that an Android launch writes once MSBuild has deployed the app
/// and computed the command that starts it.
/// </summary>
/// <remarks>
/// Android resources launch through <c>dotnet run</c>, which keeps running for the lifetime of the app while
/// <c>Microsoft.Android.Run</c> streams the app's logcat output. The generated Android targets file writes this
/// file after <c>ComputeRunArguments</c>, so its presence tells the build queue that the launch process has
/// finished its MSBuild work. The file also records how to reach the app on the device, which the stop command
/// uses to stop the app.
/// </remarks>
internal sealed class MauiAndroidLaunchSignalAnnotation(IFileSystemService fileSystemService) : IResourceAnnotation
{
    private readonly Lazy<string> _filePath = new(() => Path.Combine(
        fileSystemService.TempDirectory.CreateTempSubdirectory("aspire-maui-android-launch").Path,
        "launch-signal.txt"));

    /// <summary>
    /// Gets the path of the launch signal file.
    /// </summary>
    public string FilePath => _filePath.Value;
}
