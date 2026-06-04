// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Packaging;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli;

/// <summary>
/// Metadata for an Aspire feature flag.
/// </summary>
/// <param name="Name">The feature flag name (without the "features." prefix).</param>
/// <param name="Description">A description of what the feature does.</param>
/// <param name="DefaultValue">The default value if not explicitly configured.</param>
internal sealed record FeatureMetadata(string Name, string Description, bool DefaultValue);

// this is a copy of Shared/KnownResourceNames.cs
internal static class KnownFeatures
{
    public static string FeaturePrefix => "features";
    public static string UpdateNotificationsEnabled => "updateNotificationsEnabled";
    public static string ShowDeprecatedPackages => "showDeprecatedPackages";
    public static string StagingChannelEnabled => "stagingChannelEnabled";
    public static string DefaultWatchEnabled => "defaultWatchEnabled";
    public static string ShowAllTemplates => "showAllTemplates";
    public static string ExperimentalPolyglotRust => "experimentalPolyglot:rust";
    public static string ExperimentalPolyglotJava => "experimentalPolyglot:java";
    public static string ExperimentalPolyglotGo => "experimentalPolyglot:go";
    public static string ExperimentalPolyglotPython => "experimentalPolyglot:python";
    public static string NuGetSignatureVerificationEnabled => "nugetSignatureVerificationEnabled";
    public static string AspireSkillsRemoteFetchEnabled => "aspireSkillsRemoteFetchEnabled";

    private static readonly Dictionary<string, FeatureMetadata> s_featureMetadata = new()
    {
        [UpdateNotificationsEnabled] = new(
            UpdateNotificationsEnabled,
            "Check if update notifications are disabled and set version check environment variable",
            DefaultValue: true),

        [ShowDeprecatedPackages] = new(
            ShowDeprecatedPackages,
            "Show or hide deprecated packages in 'aspire add' search results",
            DefaultValue: false),

        [StagingChannelEnabled] = new(
            StagingChannelEnabled,
            "Enable or disable access to the staging channel for early access to preview features and packages",
            DefaultValue: false),

        [DefaultWatchEnabled] = new(
            DefaultWatchEnabled,
            "Enable or disable watch mode by default when running Aspire applications for automatic restarts on file changes",
            DefaultValue: false),

        [ShowAllTemplates] = new(
            ShowAllTemplates,
            "Show all available templates including experimental ones in 'aspire new' and 'aspire init' commands",
            DefaultValue: false),

        [ExperimentalPolyglotRust] = new(
            ExperimentalPolyglotRust,
            "Enable or disable experimental Rust language support for polyglot Aspire applications",
            DefaultValue: false),

        [ExperimentalPolyglotJava] = new(
            ExperimentalPolyglotJava,
            "Enable or disable experimental Java language support for polyglot Aspire applications",
            DefaultValue: false),

        [ExperimentalPolyglotGo] = new(
            ExperimentalPolyglotGo,
            "Enable or disable experimental Go language support for polyglot Aspire applications",
            DefaultValue: false),

        [ExperimentalPolyglotPython] = new(
            ExperimentalPolyglotPython,
            "Enable or disable experimental Python language support for polyglot Aspire applications",
            DefaultValue: false),

        [NuGetSignatureVerificationEnabled] = new(
            NuGetSignatureVerificationEnabled,
            "Enable or disable defaulting the DOTNET_NUGET_SIGNATURE_VERIFICATION environment variable for spawned processes",
            DefaultValue: true),

        [AspireSkillsRemoteFetchEnabled] = new(
            AspireSkillsRemoteFetchEnabled,
            "(Preview) Allow the Aspire CLI to download the aspire-skills bundle from GitHub. When disabled (the 13.4 default), the CLI only uses the cached bundle and the embedded snapshot baked into the CLI; toggle on to opt in to the remote fetch path.",
            DefaultValue: false)
    };

    /// <summary>
    /// Gets metadata for a specific feature.
    /// </summary>
    public static FeatureMetadata? GetFeatureMetadata(string featureName)
    {
        return s_featureMetadata.TryGetValue(featureName, out var metadata) ? metadata : null;
    }

    /// <summary>
    /// Gets all available feature metadata.
    /// </summary>
    public static IEnumerable<FeatureMetadata> GetAllFeatureMetadata()
    {
        return s_featureMetadata.Values.OrderBy(m => m.Name);
    }

    /// <summary>
    /// Gets all available feature names (without the "features." prefix).
    /// </summary>
    public static IEnumerable<string> GetAllFeatureNames()
    {
        return s_featureMetadata.Keys.OrderBy(name => name);
    }

    /// <summary>
    /// Determines whether the staging channel is enabled by checking both the feature flag
    /// and the configured channel. The staging channel is considered enabled if either the
    /// <see cref="StagingChannelEnabled"/> feature flag is <c>true</c>, or the configured
    /// channel is set to <c>"staging"</c>.
    /// </summary>
    /// <param name="features">The feature flags service.</param>
    /// <param name="configuration">The configuration to check for the channel setting.</param>
    /// <returns><c>true</c> if the staging channel should be available; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// Note that the channel check reads <c>configuration["channel"]</c> (the layered .NET
    /// configuration — environment variables, command-line, global / per-project
    /// <c>aspire.config.json#channel</c>), NOT
    /// <see cref="CliExecutionContext.IdentityChannel"/>. Callers that also need to expose
    /// staging for a CLI baked with <c>AspireCliChannel=staging</c> should combine this
    /// helper with an identity-channel check.
    /// </remarks>
    public static bool IsStagingChannelEnabled(IFeatures features, IConfiguration configuration)
    {
        return features.IsFeatureEnabled(StagingChannelEnabled, false)
            || string.Equals(configuration["channel"], PackageChannelNames.Staging, StringComparisons.ChannelName);
    }
}
