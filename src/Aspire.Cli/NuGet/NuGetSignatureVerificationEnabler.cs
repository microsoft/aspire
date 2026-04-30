// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;

namespace Aspire.Cli.NuGet;

/// <summary>
/// Enables NuGet signature verification when spawning aspire-managed processes.
/// Mirrors the .NET SDK's NuGetSignatureVerificationEnabler behavior.
/// </summary>
internal static class NuGetSignatureVerificationEnabler
{
    internal const string DotNetNuGetSignatureVerification = "DOTNET_NUGET_SIGNATURE_VERIFICATION";

    /// <summary>
    /// Applies NuGet signature verification environment variables to the given dictionary.
    /// On Linux, sets DOTNET_NUGET_SIGNATURE_VERIFICATION to "true" unless the user
    /// has explicitly set it to "false". The behavior can be disabled via the
    /// <see cref="KnownFeatures.NuGetSignatureVerificationEnabled"/> feature flag.
    /// </summary>
    public static void Apply(Dictionary<string, string> environmentVariables, IFeatures features, CliExecutionContext executionContext)
    {
        if (!OperatingSystem.IsLinux() ||
            !features.IsFeatureEnabled(
                KnownFeatures.NuGetSignatureVerificationEnabled,
                KnownFeatures.GetFeatureMetadata(KnownFeatures.NuGetSignatureVerificationEnabled)!.DefaultValue))
        {
            return;
        }

        var value = executionContext.GetEnvironmentVariable(DotNetNuGetSignatureVerification);

        // If the user explicitly set it to "false", respect that
        var effectiveValue = bool.TryParse(value, out var boolValue) && !boolValue
            ? bool.FalseString
            : bool.TrueString;

        environmentVariables[DotNetNuGetSignatureVerification] = effectiveValue;
    }
}
