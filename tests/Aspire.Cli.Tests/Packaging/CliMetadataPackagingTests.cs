// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;

namespace Aspire.Cli.Tests.Packaging;

public class CliMetadataPackagingTests
{
    [Fact]
    public void Assembly_HasAspireCliChannelMetadata_WithNonEmptyValue()
    {
        var assembly = typeof(Aspire.Cli.Program).Assembly;

        var metadata = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "AspireCliChannel");

        Assert.NotNull(metadata);
        Assert.False(string.IsNullOrEmpty(metadata.Value), "AspireCliChannel assembly metadata must have a non-empty value.");
    }
}
