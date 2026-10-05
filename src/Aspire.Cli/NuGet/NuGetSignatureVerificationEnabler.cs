// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;

namespace Aspire.Cli.NuGet;

/// <summary>
/// Enables NuGet signature verification for NuGet operations.
/// Mirrors the .NET SDK's NuGetSignatureVerificationEnabler behavior.
/// </summary>
internal static class NuGetSignatureVerificationEnabler
{
    internal const string DotNetNuGetSignatureVerification = "DOTNET_NUGET_SIGNATURE_VERIFICATION";

    private static readonly Lock s_currentProcessLock = new();
    private static int s_currentProcessScopeCount;
    private static string? s_previousCurrentProcessValue;

    /// <summary>
    /// Applies NuGet signature verification environment variables to the given dictionary.
    /// On Linux, sets DOTNET_NUGET_SIGNATURE_VERIFICATION to "true" unless the user
    /// has explicitly set it to "false". The behavior can be disabled via the
    /// <see cref="KnownFeatures.NuGetSignatureVerificationEnabled"/> feature flag.
    /// </summary>
    public static void Apply(Dictionary<string, string> environmentVariables, IFeatures features, IEnvironment environment)
    {
        if (!environment.IsLinux() ||
            !features.IsFeatureEnabled(
                KnownFeatures.NuGetSignatureVerificationEnabled,
                KnownFeatures.GetFeatureMetadata(KnownFeatures.NuGetSignatureVerificationEnabled)!.DefaultValue))
        {
            return;
        }

        var value = environment.GetEnvironmentVariable(DotNetNuGetSignatureVerification);

        // If the user explicitly set it to "false", respect that
        var effectiveValue = bool.TryParse(value, out var boolValue) && !boolValue
            ? bool.FalseString
            : bool.TrueString;

        environmentVariables[DotNetNuGetSignatureVerification] = effectiveValue;
    }

    /// <summary>
    /// Applies NuGet signature verification to the current process until the returned scope is disposed.
    /// </summary>
    /// <remarks>
    /// The aspire-managed helper received this variable only in its own environment. The in-process client needs it
    /// in the CLI's environment because NuGet reads it from there, so it is set for the duration of each operation and
    /// the previous value is restored when the last overlapping scope ends. Otherwise it would leak into every process
    /// the CLI starts afterwards.
    /// </remarks>
    public static IDisposable ApplyToCurrentProcess(IFeatures features, IEnvironment environment)
    {
        lock (s_currentProcessLock)
        {
            if (s_currentProcessScopeCount++ == 0)
            {
                s_previousCurrentProcessValue = Environment.GetEnvironmentVariable(DotNetNuGetSignatureVerification);

                var environmentVariables = new Dictionary<string, string>();
                Apply(environmentVariables, features, environment);

                if (environmentVariables.TryGetValue(DotNetNuGetSignatureVerification, out var value))
                {
                    Environment.SetEnvironmentVariable(DotNetNuGetSignatureVerification, value);
                }
            }
        }

        return new CurrentProcessScope();
    }

    private sealed class CurrentProcessScope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (s_currentProcessLock)
            {
                if (--s_currentProcessScopeCount == 0)
                {
                    Environment.SetEnvironmentVariable(DotNetNuGetSignatureVerification, s_previousCurrentProcessValue);
                    s_previousCurrentProcessValue = null;
                }
            }
        }
    }
}
