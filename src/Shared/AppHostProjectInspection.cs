// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Shared;

/// <summary>
/// Shared model + lookup helpers for the MSBuild "inspect the AppHost" probe used by both the
/// Aspire CLI (<c>AppHostInfoResolver</c>) and the Dashboard
/// (<c>DashboardFeedbackDiagnosticProvider</c>). Both probe an AppHost with
/// <c>dotnet build/msbuild &lt;apphost&gt; -getProperty:... -getItem:...</c> and then need to apply
/// the same Aspire package-version precedence to the resulting JSON, so the model and the
/// precedence rule live here rather than being mirrored in each consumer.
/// </summary>
internal static class AppHostProjectInspection
{
    /// <summary>
    /// Resolves an Aspire package version from the inspected item lists, honoring how a version can
    /// be declared. A direct <c>PackageReference</c> wins, then the SDK-provided
    /// <c>AspireProjectOrPackageReference</c>, then the Central Package Management
    /// <c>PackageVersion</c> entry. Within each list the supplied <paramref name="packageIds"/> are
    /// tried in order, which lets callers express a package preference (for example
    /// <c>Aspire.Hosting</c> before <c>Aspire.Hosting.AppHost</c>).
    /// </summary>
    /// <param name="items">The inspected item groups, or <see langword="null"/> when none were emitted.</param>
    /// <param name="packageIds">Package identities to look for, in preference order.</param>
    /// <returns>The first matching non-null version, or <see langword="null"/> when none is found.</returns>
    public static string? FindPackageVersion(AppHostProjectInspectionItems? items, params string[] packageIds)
    {
        if (items is null)
        {
            return null;
        }

        // List precedence is the OUTER loop and package preference is the INNER loop. This matches
        // the order MSBuild evaluation makes the values authoritative: an explicit reference in the
        // project overrides an SDK-injected reference, which overrides the CPM version pin. A
        // matching item with no Version (for example a CPM PackageReference whose version lives in a
        // separate PackageVersion entry) is treated as "not found" so the search continues.
        var lists = new[] { items.PackageReference, items.AspireProjectOrPackageReference, items.PackageVersion };

        foreach (var list in lists)
        {
            foreach (var packageId in packageIds)
            {
                if (FindVersionInList(list, packageId) is { } version)
                {
                    return version;
                }
            }
        }

        return null;
    }

    private static string? FindVersionInList(IReadOnlyList<AppHostProjectInspectionItem>? items, string packageId)
    {
        if (items is null)
        {
            return null;
        }

        foreach (var item in items)
        {
            if (string.Equals(item.Identity, packageId, StringComparison.Ordinal) && !string.IsNullOrEmpty(item.Version))
            {
                return item.Version;
            }
        }

        return null;
    }
}

/// <summary>
/// Root shape of the MSBuild <c>-getProperty</c>/<c>-getItem</c> JSON written to stdout when
/// inspecting an AppHost project.
/// </summary>
/// <remarks>
/// Example payload:
/// <code>
/// {
///   "Properties": { "AspireHostingSDKVersion": "13.5.0", "TargetFramework": "net10.0", ... },
///   "Items": {
///     "PackageReference": [ { "Identity": "Aspire.Hosting.AppHost", "Version": "9.0.0" } ],
///     "PackageVersion": [ ... ]
///   }
/// }
/// </code>
/// </remarks>
internal sealed record AppHostProjectInspectionOutput
{
    [JsonPropertyName("Properties")]
    public AppHostProjectInspectionProperties? Properties { get; init; }

    [JsonPropertyName("Items")]
    public AppHostProjectInspectionItems? Items { get; init; }
}

internal sealed record AppHostProjectInspectionProperties
{
    [JsonPropertyName("IsAspireHost")]
    public string? IsAspireHost { get; init; }

    [JsonPropertyName("AspireHostingSDKVersion")]
    public string? AspireHostingSDKVersion { get; init; }

    [JsonPropertyName("AspireUseCliBundle")]
    public string? AspireUseCliBundle { get; init; }

    [JsonPropertyName("UserSecretsId")]
    public string? UserSecretsId { get; init; }

    [JsonPropertyName("RunCommand")]
    public string? RunCommand { get; init; }

    [JsonPropertyName("TargetPath")]
    public string? TargetPath { get; init; }

    [JsonPropertyName("RunWorkingDirectory")]
    public string? RunWorkingDirectory { get; init; }

    [JsonPropertyName("RunArguments")]
    public string? RunArguments { get; init; }

    [JsonPropertyName("TargetFramework")]
    public string? TargetFramework { get; init; }

    [JsonPropertyName("TargetFrameworks")]
    public string? TargetFrameworks { get; init; }
}

internal sealed record AppHostProjectInspectionItems
{
    [JsonPropertyName("PackageReference")]
    public IReadOnlyList<AppHostProjectInspectionItem>? PackageReference { get; init; }

    [JsonPropertyName("AspireProjectOrPackageReference")]
    public IReadOnlyList<AppHostProjectInspectionItem>? AspireProjectOrPackageReference { get; init; }

    [JsonPropertyName("PackageVersion")]
    public IReadOnlyList<AppHostProjectInspectionItem>? PackageVersion { get; init; }
}

internal sealed record AppHostProjectInspectionItem
{
    [JsonPropertyName("Identity")]
    public string? Identity { get; init; }

    [JsonPropertyName("Version")]
    public string? Version { get; init; }
}
