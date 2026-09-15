// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Locks down the signing contract for Hex1b's Windows PTY host.
/// </summary>
/// <remarks>
/// Arcade signing only runs in the internal Azure DevOps pipeline
/// (eng/pipelines/templates/build_sign_native.yml), so a broken or deleted rule for
/// hex1bpty.exe would otherwise surface at release time rather than on the pull request
/// that broke it. These assertions parse eng/Signing.props directly, the same way
/// <see cref="NpmCliPackageTests"/> pins the CLI's certificate mappings.
/// </remarks>
public sealed class Hex1bPtyHostSigningTests
{
    private const string PtyHostFileName = "hex1bpty.exe";

    /// <summary>
    /// The PTY host ships from the Hex1b package, so it signs with the third-party
    /// certificate used for Hex1b.dll — not the Microsoft certificate used for binaries
    /// this repo builds. Getting this wrong would sign a third-party binary as Microsoft's.
    /// </summary>
    [Fact]
    public async Task PtyHostSignsWithThirdPartyCertificate()
    {
        var signingProps = await ReadSigningPropsAsync();

        var rules = signingProps
            .Descendants("FileSignInfo")
            .Where(element => (string?)element.Attribute("Include") == PtyHostFileName)
            .ToArray();

        Assert.True(
            rules.Length == 1,
            $"Expected exactly one FileSignInfo for '{PtyHostFileName}', but found {rules.Length}.");

        Assert.Equal("3PartySHA2", (string?)rules[0].Attribute("CertificateName"));
        Assert.Null((string?)rules[0].Attribute("CollisionPriorityId"));
    }

    /// <summary>
    /// The helper is signed at its publish path, next to aspire-managed, before CreateLayout
    /// copies it into the bundle. Without this entry the bundle would carry an unsigned binary.
    /// </summary>
    [Fact]
    public async Task PtyHostIsStagedForSigningFromTheManagedPublishOutput()
    {
        var signingProps = await ReadSigningPropsAsync();

        var items = signingProps
            .Descendants("ItemsToSign")
            .Select(element => (Include: (string?)element.Attribute("Include"), Condition: (string?)element.Attribute("Condition")))
            .Where(item => item.Include is not null && item.Include.EndsWith(PtyHostFileName, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            items.Length == 1,
            $"Expected exactly one ItemsToSign covering '{PtyHostFileName}', but found {items.Length}.");

        // Must come from Aspire.Managed's publish output: that is the copy CreateLayout stages,
        // and the only one the signing pass sees.
        Assert.Contains("Aspire.Managed", items[0].Include!, StringComparison.Ordinal);
        Assert.Contains("publish", items[0].Include!, StringComparison.Ordinal);

        // Windows-only asset, so the entry must not be picked up on other build hosts.
        Assert.Equal("$([System.OperatingSystem]::IsWindows())", items[0].Condition);
    }

    private static async Task<XDocument> ReadSigningPropsAsync()
    {
        var path = Path.Combine(RepoRoot.Path, "eng", "Signing.props");
        return XDocument.Parse(await File.ReadAllTextAsync(path));
    }
}
