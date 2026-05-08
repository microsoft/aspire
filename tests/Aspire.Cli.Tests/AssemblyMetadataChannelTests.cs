// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Cli.Packaging;

namespace Aspire.Cli.Tests;

public class AssemblyMetadataChannelTests
{
    private static readonly string[] s_validChannels = ["stable", "staging", "daily", "pr", "local"];

    [Fact]
    public void AspireCliChannel_AssemblyMetadata_IsOneOfExpectedValues()
    {
        var assembly = typeof(Aspire.Cli.Program).Assembly;

        var metadata = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "AspireCliChannel");

        Assert.NotNull(metadata);
        Assert.Contains(metadata.Value, s_validChannels);
    }

    [Fact]
    public void CliAssembly_BakesChannel_AsLocal_MatchingCsprojDefault()
    {
        var assembly = typeof(Aspire.Cli.Program).Assembly;

        var metadata = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "AspireCliChannel");

        Assert.NotNull(metadata);
        // Exact equality (not set-membership) — guards the csproj default
        // <AspireCliChannel>local</AspireCliChannel>. The membership test above passes
        // for "daily" too; this one fails if the csproj default reverts.
        Assert.Equal(PackageChannelNames.Local, metadata.Value);
    }
}
