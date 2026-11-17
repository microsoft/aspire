// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using GenerateTemplateManifest;
using VerifyXunit;
using Xunit;

namespace Infrastructure.Tests.TemplateManifest;

public sealed class ComponentManifestTests
{
    [Fact]
    public async Task RecordsExternalClosureWithoutBuildSpecificIdentities()
    {
        var manifest = new ComponentManifest(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Aspire.Hosting/14.0.0-pr.42"
        });
        manifest.Register("nuget", "Aspire.Hosting", "14.0.0-pr.42", "fixture");
        manifest.Register("nuget", "External.Dependency", "1.0.0", "fixture");
        manifest.Register("nuget", "external.dependency", "1.0.0", "another graph");
        manifest.Register("nuget", "External.Dependency", "2.0.0", "another option");
        manifest.Register("npm", "@scope/dependency", "3.0.0", "lockfile");

        Assert.Equal(3, manifest.Count);
        await Verifier.Verify(manifest.Serialize(), "json").UseDirectory("Snapshots");
    }

    [Fact]
    public void ExclusionIsForTheBuiltVersionNotEveryVersionOfAPackage()
    {
        var manifest = new ComponentManifest(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Aspire.Hosting/14.0.0-dev"
        });
        manifest.Register("nuget", "Aspire.Hosting", "13.5.4", "fixture");

        Assert.Equal(1, manifest.Count);
    }
}
