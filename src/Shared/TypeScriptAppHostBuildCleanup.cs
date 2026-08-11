// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared;

/// <summary>
/// Adds cross-platform stale-output cleanup to shell-executed TypeScript AppHost builds.
/// </summary>
internal static class TypeScriptAppHostBuildCleanup
{
    // Keep in sync with TypeScriptAppHostToolchainResolver.BuildOutputDirectory and
    // TypeScriptLanguageSupport.AppHostBuildOutputDirectory - all three must point at the same
    // compiled-output directory for the cleanup below to remove the right thing.
    private const string BuildOutputDirectory = "./node_modules/.tmp/aspire-apphost";

    /// <summary>
    /// Appends a shell fallback to <paramref name="tscInvocation"/> that deletes the compiled output
    /// directory and reports failure, but only when <paramref name="tscInvocation"/> itself exits
    /// non-zero. Uses <c>node -e</c> (Node.js is already a required dependency for this AppHost via
    /// the "engines.node" constraint and the ESLint devDependency) rather than a shell-specific
    /// <c>rm -rf</c>/<c>rmdir /s</c> so the same snippet works verbatim under both cmd.exe (npm/yarn/
    /// pnpm/bun on Windows invoke scripts via cmd.exe) and a POSIX shell (macOS/Linux).
    /// </summary>
    internal static string AppendShellCleanupOnFailure(string tscInvocation)
    {
        return $"{tscInvocation} || node -e \"process.exitCode=1;require('fs').rmSync('{BuildOutputDirectory}',{{recursive:true,force:true}})\"";
    }
}
