// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;

namespace Aspire.Cli.Tests.NuGet;

public class NuGetConfigurationTests
{
    [Fact]
    public void HasSourcePolicyChanges_AmbientPolicyIsUnchanged()
    {
        var configuration = new NuGetConfiguration(CreateSettings([]), [], overlay: null);

        Assert.False(configuration.HasSourcePolicyChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HasSourcePolicyChanges_MappingOrderAndCaseAreIgnored(bool clearMappings)
    {
        var settings = CreateSettings(
        [
            new("company", ["Aspire*", "Company.*"]),
            new("public", ["*"])
        ]);
        var overlay = new NuGetConfigOverlay(
            [], [new("PUBLIC", ["*"]), new("COMPANY", ["company.*", "ASPIRE*"])], false, [], null)
        {
            ClearPackageSourceMappings = clearMappings
        };

        Assert.False(new NuGetConfiguration(settings, [], overlay).HasSourcePolicyChanges);
    }

    [Theory]
    [InlineData("company", "Other.*")]
    [InlineData("other", "Aspire*")]
    public void HasSourcePolicyChanges_ChangedMappingIsDetected(string sourceKey, string pattern)
    {
        var settings = CreateSettings([new("company", ["Aspire*"])]);
        var overlay = new NuGetConfigOverlay([], [new(sourceKey, [pattern])], false, [], null);

        Assert.True(new NuGetConfiguration(settings, [], overlay).HasSourcePolicyChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HasSourcePolicyChanges_EmptyMappingOnlyChangesPolicyWhenCleared(bool clearMappings)
    {
        var settings = CreateSettings([new("company", ["Aspire*"])]);
        var overlay = new NuGetConfigOverlay([], [], false, [], null)
        {
            ClearPackageSourceMappings = clearMappings
        };

        Assert.Equal(clearMappings, new NuGetConfiguration(settings, [], overlay).HasSourcePolicyChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HasSourcePolicyChanges_DisabledSourcesAreComparedByEffectiveState(bool reenableSource)
    {
        var settings = CreateSettings([]) with
        {
            Sources = [new("company", "identity", false, false, false)],
            DisabledPackageSourceKeys = ["company"]
        };
        var overlay = new NuGetConfigOverlay([], [], true, reenableSource ? [] : ["COMPANY"], null);

        Assert.Equal(reenableSource, new NuGetConfiguration(settings, [], overlay).HasSourcePolicyChanges);
    }

    [Fact]
    public void HasSourcePolicyChanges_NewSourceIsDetected()
    {
        var overlay = new NuGetConfigOverlay([("new", "https://feed.example/v3/index.json")], [], false, [], null);

        Assert.True(new NuGetConfiguration(CreateSettings([]), [], overlay).HasSourcePolicyChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HasSourcePolicyChanges_RetiredSourceOnlyChangesPolicyWhenStillEnabled(bool isEnabled)
    {
        var overlay = new NuGetConfigOverlay([], [], false, [], null) { RetiredSourceKeys = ["old"] };
        var settings = CreateSettings([]) with
        {
            Sources = [new("old", "identity", isEnabled, false, false)]
        };

        Assert.Equal(isEnabled, new NuGetConfiguration(settings, [], overlay).HasSourcePolicyChanges);
    }

    [Fact]
    public void HasSourcePolicyChanges_CacheFolderIsNotASourcePolicyChange()
    {
        var overlay = new NuGetConfigOverlay([], [], false, [], ".packages");

        Assert.False(new NuGetConfiguration(CreateSettings([]), [], overlay).HasSourcePolicyChanges);
    }

    private static NuGetSettingsInfo CreateSettings(IReadOnlyList<NuGetPackageSourceMapping> mappings)
        => new([], "ambient", [], [], mappings, [], [], new byte[NuGetSourceIdentity.KeySizeInBytes]);
}
