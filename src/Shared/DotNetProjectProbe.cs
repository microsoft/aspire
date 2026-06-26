// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared;

/// <summary>
/// Builds the <c>dotnet</c> command-line arguments for an MSBuild "get items and properties" probe
/// of an AppHost. It is used by the Aspire CLI
/// (<c>DotNetCliRunner.GetProjectItemsAndPropertiesAsync</c>). The argument construction — driver
/// selection and the MSBuild evaluation switches — lives here in <c>src/Shared</c>, extracted from
/// <c>DotNetCliRunner</c>, so it is isolated from the CLI's process plumbing and can be reused or
/// tested on its own. The dashboard does not run this probe; its feedback diagnostics instead read
/// the AppHost description the AppHost forwards via <c>DASHBOARD__APPHOST__INFO</c>.
/// </summary>
internal static class DotNetProjectProbe
{
    /// <summary>
    /// Builds the argument list for <c>dotnet</c> that evaluates <paramref name="projectFilePath"/>
    /// and returns the requested <paramref name="items"/> and <paramref name="properties"/> (and
    /// optionally runs <paramref name="targets"/> first) as machine-readable JSON on stdout.
    /// </summary>
    /// <param name="projectFilePath">
    /// Full path to the AppHost project (<c>.csproj</c>) or single-file AppHost (<c>apphost.cs</c>).
    /// </param>
    /// <param name="items">MSBuild item types to return (passed to <c>-getItem</c>).</param>
    /// <param name="properties">MSBuild properties to return (passed to <c>-getProperty</c>).</param>
    /// <param name="targets">
    /// Targets to run before evaluation (passed to <c>-t</c>). Some run-related properties are only
    /// populated after a target such as <c>ComputeRunArguments</c> executes.
    /// </param>
    /// <returns>The ordered <c>dotnet</c> argument list.</returns>
    public static List<string> BuildItemsAndPropertiesArguments(
        string projectFilePath,
        IReadOnlyList<string> items,
        IReadOnlyList<string> properties,
        IReadOnlyList<string> targets)
    {
        // A single-file AppHost (apphost.cs) must go through the `dotnet build` driver so the
        // file-based app is materialized into a project before evaluation; a project AppHost uses
        // `dotnet msbuild` directly.
        var isSingleFileAppHost = string.Equals(Path.GetExtension(projectFilePath), ".cs", StringComparison.OrdinalIgnoreCase);

        var arguments = new List<string> { isSingleFileAppHost ? "build" : "msbuild" };

        if (properties.Count > 0)
        {
            // HACK: MSBuildVersion is requested first as a workaround. `dotnet msbuild -getProperty`
            // with a single property name returns the bare value instead of a JSON document, which
            // breaks parsing. Asking for more than one property forces the JSON shape.
            // https://github.com/dotnet/msbuild/issues/12490
            arguments.Add($"-getProperty:MSBuildVersion,{string.Join(",", properties)}");
        }

        if (items.Count > 0)
        {
            arguments.Add($"-getItem:{string.Join(",", items)}");
        }

        if (targets.Count > 0)
        {
            // Request MSBuild to actually run these targets before evaluating -getProperty / -getItem.
            // Some run-related properties (RunCommand, RunArguments, RunWorkingDirectory) are only
            // populated after the ComputeRunArguments target executes, so the direct-launch path
            // matches what `dotnet run` would have produced.
            // https://learn.microsoft.com/visualstudio/msbuild/msbuild-command-line-reference#switches
            arguments.Add($"-t:{string.Join(";", targets)}");
        }

        arguments.Add(projectFilePath);

        return arguments;
    }
}
