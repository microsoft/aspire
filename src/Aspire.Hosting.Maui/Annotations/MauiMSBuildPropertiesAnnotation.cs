// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Maui.Utilities;

namespace Aspire.Hosting.Maui.Annotations;

/// <summary>
/// Annotation carrying user-specified MSBuild properties for a MAUI platform resource. Build
/// properties influence build-time evaluation (for example authentication configuration); run
/// properties are launch selectors (for example a device id) applied only when launching the app.
/// </summary>
/// <remarks>
/// <para>
/// Build properties are emitted into a temporary <c>.props</c> file (mirroring how environment variables
/// are emitted into a generated <c>.targets</c> file for Android/iOS) and imported early via
/// <c>CustomBeforeMicrosoftCommonProps</c>. The same file (identical path and content) is imported by both
/// the serialized pre-build (<c>dotnet build</c>) and the launch command (<c>dotnet build /t:Run</c>) so
/// the launch sees identical build inputs and does not trigger a rebuild. Writing values into an imported
/// file also keeps them off the process command line and avoids command-line escaping concerns.
/// </para>
/// <para>
/// Run properties must not affect the build, so they are applied only to the launch command and passed as
/// <c>-p:Name=Value</c> arguments. Command-line properties also override a same-named property coming from
/// the imported build file.
/// </para>
/// </remarks>
internal sealed class MauiMSBuildPropertiesAnnotation : IResourceAnnotation
{
    // Temp directory holding the generated .props files. Provisioned via IFileSystemService by the
    // build-queue subscriber (see EnsurePropsDirectory) so the directory is tracked and deleted on
    // shutdown, mirroring how the environment .targets directory is created. Reused across build/run
    // and resource restarts so a new temp directory is not leaked each time the files are regenerated.
    private string? _propsDirectory;

    /// <summary>
    /// MSBuild properties available at build time. Last write wins per property name.
    /// </summary>
    public Dictionary<string, string> BuildProperties { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// MSBuild properties applied only to the launch/run command and passed as <c>-p:</c> arguments.
    /// Last write wins per property name.
    /// </summary>
    public Dictionary<string, string> RunProperties { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Provisions the temp directory for the generated <c>.props</c> files through the tracked file
    /// system service so it is cleaned up on shutdown, matching the environment <c>.targets</c> directory.
    /// Called by the build-queue subscriber before the build/launch; a no-op once provisioned.
    /// </summary>
    public void EnsurePropsDirectory(IFileSystemService fileSystemService)
        => _propsDirectory ??= fileSystemService.TempDirectory.CreateTempSubdirectory("aspire-maui-msbuild").Path;

    /// <summary>
    /// Writes the build-time properties to a generated <c>.props</c> file and returns the MSBuild
    /// argument that imports it, or <see langword="null"/> when there are no build-time properties.
    /// </summary>
    /// <remarks>
    /// The same file (identical path and content) is imported by both the pre-build and the launch
    /// command so the launch does not see different build inputs and rebuild.
    /// </remarks>
    public string? CreateBuildPropsArgument(string resourceName)
    {
        if (BuildProperties.Count == 0)
        {
            return null;
        }

        // EnsurePropsDirectory (called by the build-queue subscriber via IFileSystemService) normally
        // provisions this. Fall back to a self-created temp dir with the same prefix for isolated
        // arg-evaluation paths that bypass the subscriber (for example unit tests).
        _propsDirectory ??= Directory.CreateTempSubdirectory("aspire-maui-msbuild").FullName;

        var propsFilePath = MauiMSBuildPropsFileHelper.WritePropsFile(_propsDirectory, resourceName, BuildProperties);

        // CustomBeforeMicrosoftCommonProps is imported by Microsoft.Common.props before the project body,
        // so the properties are available for the whole build (including build-time evaluation). Projects
        // can still override a value in their own body, matching normal .props import semantics.
        // https://learn.microsoft.com/visualstudio/msbuild/customize-your-build#custombeforemicrosoftcommonprops
        return $"-p:CustomBeforeMicrosoftCommonProps={propsFilePath}";
    }

    /// <summary>
    /// Returns the run-only properties as <c>-p:Name=Value</c> MSBuild arguments for the launch command.
    /// </summary>
    /// <remarks>
    /// Run properties are launch selectors (for example a device id) that must not influence the build, so
    /// they are passed as command-line properties on the launch command only rather than written into the
    /// shared build <c>.props</c> file. Command-line properties also override a same-named property from the
    /// imported build file.
    /// </remarks>
    public IEnumerable<string> CreateRunPropertyArguments()
    {
        foreach (var (name, value) in RunProperties.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            yield return $"-p:{name}={value}";
        }
    }
}
