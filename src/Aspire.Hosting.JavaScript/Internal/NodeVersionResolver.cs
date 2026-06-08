// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Resolves the Node.js major version to use in the generated Dockerfile.
//
// Follows the same shape as Cloud Native Buildpacks-style tooling for Node
// selection: pinned toolchain files (.nvmrc, .node-version, .tool-versions)
// are treated as authoritative runtime intent; package.json#engines.node is
// compatibility metadata rather than a deployment image pin. If there is no
// explicit toolchain pin, we fall back to a preferred Node major.
//
// References:
//   .nvmrc          — https://github.com/nvm-sh/nvm#nvmrc
//   .node-version   — https://github.com/shadowspawn/node-version-usage
//   .tool-versions  — https://asdf-vm.com/manage/configuration.html#tool-versions
//   buildpacks Node — https://github.com/heroku/heroku-buildpack-nodejs
//
// Example .nvmrc:                          22
// Example .node-version:                   v22.1.0
// Example .tool-versions (asdf-style):
//   nodejs 22.1.0
//   python 3.12

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.JavaScript.Internal;

internal static class NodeVersionResolver
{
    /// <summary>
    /// Default Node.js major used when no toolchain pin is found.
    /// </summary>
    public const string DefaultNodeVersion = "22";

    /// <summary>
    /// Returns a default Node.js base image of the form
    /// <c>node:&lt;major&gt;-&lt;suffix&gt;</c>, with <c>&lt;major&gt;</c>
    /// resolved from local toolchain files when present.
    /// </summary>
    /// <param name="appDirectory">Application directory to inspect for toolchain files.</param>
    /// <param name="defaultSuffix">Image variant suffix (for example <c>"alpine"</c> or <c>"slim"</c>).</param>
    /// <param name="serviceProvider">Service provider used to resolve the logger.</param>
    /// <param name="workspaceRoot">
    /// Optional workspace root path. When set, the resolver inspects the workspace root first;
    /// monorepos commonly pin the Node version with a root-level <c>.nvmrc</c> /
    /// <c>.tool-versions</c> rather than per-app.
    /// </param>
    public static string GetDefaultBaseImage(string appDirectory, string defaultSuffix, IServiceProvider serviceProvider, string? workspaceRoot = null)
    {
        var logger = serviceProvider.GetService<ILogger<JavaScriptAppResource>>() ?? NullLogger<JavaScriptAppResource>.Instance;
        var nodeVersion = ResolveNodeVersion(appDirectory, logger, workspaceRoot);
        return $"node:{nodeVersion}-{defaultSuffix}";
    }

    /// <summary>
    /// Resolves the Node.js major version for a project by checking common
    /// configuration files. Returns <see cref="DefaultNodeVersion"/> when no
    /// toolchain pin is found.
    /// </summary>
    /// <remarks>
    /// The app directory is checked first so that an explicit per-app pin always wins. In workspace
    /// mode the workspace root is checked second as a fallback because the dominant convention is to
    /// pin the Node version once at the monorepo root with <c>.nvmrc</c> / <c>.tool-versions</c>
    /// rather than per-app.
    /// </remarks>
    public static string ResolveNodeVersion(string workingDirectory, ILogger logger, string? workspaceRoot = null)
    {
        if (TryDetectPinnedNodeVersion(workingDirectory, logger, out var pinnedNodeVersion))
        {
            return pinnedNodeVersion;
        }

        // Fall back to a root-level pin for workspace members that do not pin per-app. Guard against
        // re-reading the same directory when the workspace root equals the working directory.
        if (workspaceRoot is not null &&
            !string.Equals(Path.GetFullPath(workspaceRoot), Path.GetFullPath(workingDirectory), StringComparison.Ordinal) &&
            TryDetectPinnedNodeVersion(workspaceRoot, logger, out var rootPinned))
        {
            return rootPinned;
        }

        logger.LogDebug("No Node.js version detected, using default version {DefaultVersion}", DefaultNodeVersion);
        return DefaultNodeVersion;
    }

    private static bool TryDetectPinnedNodeVersion(string workingDirectory, ILogger logger, out string nodeVersion)
    {
        nodeVersion = string.Empty;

        var nvmrcPath = Path.Combine(workingDirectory, ".nvmrc");
        if (File.Exists(nvmrcPath))
        {
            var versionString = File.ReadAllText(nvmrcPath).Trim();
            if (TryParseNodeVersion(versionString, out var version))
            {
                logger.LogDebug("Detected Node.js version {Version} from .nvmrc file", version);
                nodeVersion = version;
                return true;
            }
        }

        var nodeVersionPath = Path.Combine(workingDirectory, ".node-version");
        if (File.Exists(nodeVersionPath))
        {
            var versionString = File.ReadAllText(nodeVersionPath).Trim();
            if (TryParseNodeVersion(versionString, out var version))
            {
                logger.LogDebug("Detected Node.js version {Version} from .node-version file", version);
                nodeVersion = version;
                return true;
            }
        }

        var toolVersionsPath = Path.Combine(workingDirectory, ".tool-versions");
        if (File.Exists(toolVersionsPath))
        {
            // Each line is "<tool> <version> [<version>...]". We scan for nodejs/node entries
            // and take their first version token.
            //
            // Example .tool-versions content:
            //   nodejs 22.1.0
            //   python 3.12.0
            var lines = File.ReadAllLines(toolVersionsPath);
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                var parts = trimmedLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1 &&
                    (string.Equals(parts[0], "nodejs", StringComparison.Ordinal) ||
                     string.Equals(parts[0], "node", StringComparison.Ordinal)))
                {
                    if (TryParseNodeVersion(parts[1], out var version))
                    {
                        logger.LogDebug("Detected Node.js version {Version} from .tool-versions file", version);
                        nodeVersion = version;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Attempts to parse a Node.js version string and extract the major
    /// version. Accepts forms such as <c>22</c>, <c>v22.1.0</c>, <c>&gt;=20.12</c>,
    /// <c>^18.0.0</c>.
    /// </summary>
    private static bool TryParseNodeVersion(string versionString, out string majorVersion)
    {
        majorVersion = string.Empty;

        if (string.IsNullOrWhiteSpace(versionString))
        {
            return false;
        }

        // Remove common prefixes and operators (handle multi-character operators first).
        var cleaned = versionString.Trim();
        string[] operators = [">=", "<=", "==", ">", "<", "=", "~", "^", "v", "V"];
        foreach (var op in operators)
        {
            if (cleaned.StartsWith(op, StringComparison.Ordinal))
            {
                cleaned = cleaned.Substring(op.Length).TrimStart();
                break;
            }
        }
        var cleanedVersion = cleaned.Split('.', '-', ' ')[0]; // major component only

        if (int.TryParse(cleanedVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var majorVersionNumber) && majorVersionNumber > 0)
        {
            majorVersion = majorVersionNumber.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }
}
