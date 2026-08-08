// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils;

/// <summary>
/// The per-user directory names a single VS Code build family uses on disk.
/// </summary>
/// <param name="UserDataFolderName">
/// The <c>nameShort</c> value that names the user-data directory (for example
/// <c>%APPDATA%\Code</c> or <c>~/Library/Application Support/Code</c>).
/// </param>
/// <param name="DesktopDataFolderName">
/// The <c>dataFolderName</c> value that names the home-relative desktop folder holding extensions.
/// </param>
/// <param name="ServerDataFolderName">
/// The <c>serverDataFolderName</c> value that names the home-relative remote/server folder.
/// </param>
internal sealed record VsCodeVariant(
    string UserDataFolderName,
    string DesktopDataFolderName,
    string ServerDataFolderName);

/// <summary>
/// The single model of where a VS Code build keeps its per-user state on disk.
/// </summary>
/// <remarks>
/// Every path VS Code derives per user comes from three <c>product.json</c> fields, so the CLI keeps
/// one table of them rather than letting each caller hand-roll its own directory list.
/// <list type="bullet">
/// <item><description><c>nameShort</c> names the user-data directory.</description></item>
/// <item><description><c>dataFolderName</c> names the home-relative desktop folder.</description></item>
/// <item><description><c>serverDataFolderName</c> names the home-relative remote/server folder.</description></item>
/// </list>
/// Values are taken from the shipping manifests: the stable and Insiders builds use <c>Code</c> /
/// <c>Code - Insiders</c>, and VSCodium inherits the OSS defaults because its overlay manifest does
/// not redefine them.
/// See https://github.com/microsoft/vscode/blob/main/product.json and
/// https://github.com/VSCodium/vscodium/blob/master/product.json.
/// </remarks>
internal static class VsCodeInstallLayout
{
    private static readonly IReadOnlyList<VsCodeVariant> s_knownVariants =
    [
        new VsCodeVariant("Code", ".vscode", ".vscode-server"),
        new VsCodeVariant("Code - Insiders", ".vscode-insiders", ".vscode-server-insiders"),
        new VsCodeVariant("VSCodium", ".vscode-oss", ".vscode-server-oss")
    ];

    /// <summary>
    /// Enumerates the extension roots a VS Code build could load extensions from, most to least
    /// authoritative.
    /// </summary>
    /// <remarks>
    /// <c>VSCODE_EXTENSIONS</c> replaces the extension location outright, so when it is set it is the
    /// only root worth probing: falling through to the defaults could report an extension from
    /// <c>~/.vscode</c> that the running window would never load. This deliberately does not model
    /// <c>--extensions-dir</c> or portable mode, because a directory probe cannot tell which of
    /// several installations is the active one — callers that need the active install read the
    /// version the extension itself reports instead.
    /// </remarks>
    internal static IEnumerable<string> GetExtensionRootPaths(IEnvironment environment, DirectoryInfo homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(homeDirectory);

        var overrideDirectory = environment.GetEnvironmentVariable("VSCODE_EXTENSIONS");
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            yield return overrideDirectory;
            yield break;
        }

        var home = homeDirectory.FullName;
        foreach (var variant in s_knownVariants)
        {
            yield return Path.Combine(home, variant.DesktopDataFolderName, "extensions");
        }

        foreach (var variant in s_knownVariants)
        {
            yield return Path.Combine(home, variant.ServerDataFolderName, "extensions");
        }
    }

    /// <summary>
    /// Enumerates the home-relative remote/server data folder names for every known VS Code build.
    /// </summary>
    internal static IEnumerable<string> ServerDataFolderNames
        => s_knownVariants.Select(variant => variant.ServerDataFolderName);

    /// <summary>
    /// Enumerates the user-data directory names for every known VS Code build.
    /// </summary>
    internal static IEnumerable<string> UserDataFolderNames
        => s_knownVariants.Select(variant => variant.UserDataFolderName);
}
