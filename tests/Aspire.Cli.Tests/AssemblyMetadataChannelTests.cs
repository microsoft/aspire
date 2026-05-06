// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;

namespace Aspire.Cli.Tests;

public class AssemblyMetadataChannelTests
{
    private static readonly string[] s_validChannels = ["stable", "staging", "daily", "pr"];

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
}
