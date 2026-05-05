// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;

namespace Aspire.Cli.Tests;

public class AssemblyMetadataChannelTests
{
    private static readonly string[] s_validChannels = ["stable", "staging", "daily", "pr"];

    [Fact]
    public void AspireCliAssembly_HasExactlyOneAspireCliChannelAttribute()
    {
        var assembly = typeof(Program).Assembly;
        var channelAttrs = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(a => a.Key == "AspireCliChannel")
            .ToArray();

        Assert.Single(channelAttrs);
    }

    [Fact]
    public void AspireCliAssembly_AspireCliChannelValue_IsOneOfValidChannels()
    {
        var assembly = typeof(Program).Assembly;
        var channelAttr = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(a => a.Key == "AspireCliChannel");

        Assert.NotNull(channelAttr);
        Assert.Contains(channelAttr.Value, s_validChannels);
    }
}
