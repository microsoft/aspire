// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Xml.Linq;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Packaging;

namespace Aspire.Cli.Tests;

public class AssemblyMetadataChannelTests
{
    [Fact]
    public void AspireCliChannel_AssemblyMetadata_HasValidShape()
    {
        // The baked AspireCliChannel must match the shape IdentityChannelReader.IsValidChannel
        // accepts: one of `stable`, `staging`, `daily`, `local`, or `pr-<digits>`. This is
        // the smoke test that protects against a CI misconfiguration emitting the legacy
        // literal `pr` (no `-<N>` suffix) — which would build successfully and then mis-route
        // packages at runtime.
        var assembly = typeof(Aspire.Cli.Program).Assembly;

        var metadata = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "AspireCliChannel");

        Assert.NotNull(metadata);
        Assert.False(string.IsNullOrEmpty(metadata.Value), "AspireCliChannel must have a non-empty value.");
        Assert.True(
            IdentityChannelReader.IsValidChannel(metadata.Value),
            $"AspireCliChannel value '{metadata.Value}' is not in the accepted set (stable|staging|daily|local|pr-<N>).");
    }

    [Fact]
    public void Csproj_DeclaresAspireCliChannelDefault_AsLocal()
    {
        // Guards the csproj-level default <AspireCliChannel Condition="'$(AspireCliChannel)' == ''">local</AspireCliChannel>.
        // Asserting the *declared* default in the project file rather than the *baked* value on the
        // currently-built assembly keeps this test correct under any /p:AspireCliChannel=... CI build
        // (including pr-<N> builds), while still failing if the csproj default itself is reverted.
        var csprojPath = Path.Combine(GetRepoRoot(), "src", "Aspire.Cli", "Aspire.Cli.csproj");
        Assert.True(File.Exists(csprojPath), $"Expected csproj at {csprojPath}");

        var doc = XDocument.Load(csprojPath);

        var defaultElement = doc.Descendants()
            .FirstOrDefault(e =>
                e.Name.LocalName == "AspireCliChannel"
                && string.Equals(
                    (string?)e.Attribute("Condition"),
                    "'$(AspireCliChannel)' == ''",
                    StringComparison.Ordinal));

        Assert.NotNull(defaultElement);
        Assert.Equal(PackageChannelNames.Local, defaultElement.Value);
    }

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir.FullName;
    }
}

