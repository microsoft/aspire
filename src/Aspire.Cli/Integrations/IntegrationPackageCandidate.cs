// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Packaging;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Integrations;

internal sealed record IntegrationPackageCandidate(
    IntegrationEntry Entry,
    IntegrationProviderReference Provider,
    NuGetPackage Package,
    PackageChannel Channel)
{
    public string Name => Entry.Id;

    public string QualifiedName => $"{Entry.Index.Id}/{Entry.Id}";

    public string ProviderCoordinate => $"{Provider.Type}:{Provider.Package}";

    public (string FriendlyName, NuGetPackage Package, PackageChannel Channel) ToLegacyPackage() =>
        (Name, Package, Channel);

    public bool IsExactMatch(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return string.Equals(Name, value, StringComparisons.CliInputOrOutput) ||
            string.Equals(QualifiedName, value, StringComparisons.CliInputOrOutput) ||
            Entry.Aliases.Contains(value, StringComparers.CliInputOrOutput) ||
            string.Equals(Package.Id, value, StringComparisons.NuGetPackageId) ||
            string.Equals(ProviderCoordinate, value, StringComparisons.CliInputOrOutput);
    }
}
