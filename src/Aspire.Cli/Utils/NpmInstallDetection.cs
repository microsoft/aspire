// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils;

/// <summary>
/// Detects whether the Aspire CLI is running from a global npm install of the
/// <c>@microsoft/aspire-cli</c> package and provides the npm-equivalent
/// self-update command so the CLI surfaces the correct guidance instead of
/// attempting to overwrite npm-owned files with the GitHub-binary downloader.
/// </summary>
/// <remarks>
/// The npm launcher (<c>eng/clipack/npm/aspire.js</c>) sets three environment
/// variables when it spawns the native CLI binary: <c>ASPIRE_NPM_PACKAGE</c>,
/// <c>ASPIRE_NPM_PACKAGE_VERSION</c>, and <c>ASPIRE_NPM_PACKAGE_RID</c>.
/// Presence of <c>ASPIRE_NPM_PACKAGE</c> with the expected package name is
/// treated as the authoritative signal that the CLI was launched by the npm
/// launcher; the other variables are surfaced via accessors for diagnostics.
/// </remarks>
internal static class NpmInstallDetection
{
    internal const string PackageEnvironmentVariableName = "ASPIRE_NPM_PACKAGE";
    internal const string PackageVersionEnvironmentVariableName = "ASPIRE_NPM_PACKAGE_VERSION";
    internal const string PackageRidEnvironmentVariableName = "ASPIRE_NPM_PACKAGE_RID";

    internal const string ExpectedPackageName = "@microsoft/aspire-cli";

    private static readonly AsyncLocal<IEnvironmentReader?> s_environmentOverride = new();

    internal static bool IsRunningFromNpm()
    {
        return GetNpmUpdateCommand() is not null;
    }

    internal static string? GetNpmUpdateCommand()
    {
        var env = s_environmentOverride.Value ?? ProcessEnvironmentReader.Instance;
        var packageName = env.GetEnvironmentVariable(PackageEnvironmentVariableName);

        if (string.IsNullOrWhiteSpace(packageName))
        {
            return null;
        }

        // The launcher always writes the canonical "@microsoft/aspire-cli" package name.
        // Reject anything else so an unrelated env var collision does not flip the CLI
        // into the npm self-update path.
        if (!string.Equals(packageName, ExpectedPackageName, StringComparison.Ordinal))
        {
            return null;
        }

        return $"npm install -g {ExpectedPackageName}@latest";
    }

    internal static string? GetNpmPackageVersion()
    {
        var env = s_environmentOverride.Value ?? ProcessEnvironmentReader.Instance;
        var version = env.GetEnvironmentVariable(PackageVersionEnvironmentVariableName);

        return string.IsNullOrWhiteSpace(version) ? null : version;
    }

    internal static string? GetNpmPackageRid()
    {
        var env = s_environmentOverride.Value ?? ProcessEnvironmentReader.Instance;
        var rid = env.GetEnvironmentVariable(PackageRidEnvironmentVariableName);

        return string.IsNullOrWhiteSpace(rid) ? null : rid;
    }

    internal static IDisposable UseEnvironmentForTesting(IReadOnlyDictionary<string, string?> environment)
    {
        var previous = s_environmentOverride.Value;
        s_environmentOverride.Value = new DictionaryEnvironmentReader(environment);
        return new EnvironmentOverrideScope(previous);
    }

    internal interface IEnvironmentReader
    {
        string? GetEnvironmentVariable(string name);
    }

    private sealed class ProcessEnvironmentReader : IEnvironmentReader
    {
        public static readonly ProcessEnvironmentReader Instance = new();

        public string? GetEnvironmentVariable(string name)
        {
            return Environment.GetEnvironmentVariable(name);
        }
    }

    private sealed class DictionaryEnvironmentReader(IReadOnlyDictionary<string, string?> environment) : IEnvironmentReader
    {
        public string? GetEnvironmentVariable(string name)
        {
            return environment.TryGetValue(name, out var value) ? value : null;
        }
    }

    private sealed class EnvironmentOverrideScope(IEnvironmentReader? previous) : IDisposable
    {
        public void Dispose()
        {
            s_environmentOverride.Value = previous;
        }
    }
}
