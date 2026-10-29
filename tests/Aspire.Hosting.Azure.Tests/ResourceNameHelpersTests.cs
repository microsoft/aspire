// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class ResourceNameHelpersTests
{
    [Theory]
    [InlineData(18)]
    [InlineData(53)]
    [InlineData(64)]
    public void NamesWithinLimitAreUnchanged(int maxLength)
    {
        var name = new string('a', maxLength);

        Assert.Equal(name, ResourceNameHelpers.TruncateWithHash(name, maxLength));
        Assert.Equal("aks", ResourceNameHelpers.TruncateWithHash("aks", maxLength));
    }

    [Theory]
    [InlineData(18)]
    [InlineData(53)]
    [InlineData(64)]
    public void LongNamesRetainPrefixAndHashFullName(int maxLength)
    {
        var name = new string('a', 80);
        var result = ResourceNameHelpers.TruncateWithHash(name, maxLength);
        var otherResult = ResourceNameHelpers.TruncateWithHash(name + "b", maxLength);

        Assert.Equal(maxLength, result.Length);
        Assert.Equal(name[..(maxLength - 17)], result[..^17]);
        Assert.Matches("-[0-9a-f]{16}$", result);
        Assert.Equal(result, ResourceNameHelpers.TruncateWithHash(name, maxLength));
        Assert.NotEqual(result[^16..], otherResult[^16..]);
        Assert.Equal(result[^16..], ResourceNameHelpers.TruncateWithHash(name, maxLength + 1)[^16..]);
    }

    [Fact]
    public async Task ShortenedNamesAreStable()
    {
        var names = new[] { "akss", "production" }
            .Select(name => $"{name}-control-plane-identity-roles-{name}-kubelet-identity");

        await Verify(string.Join('\n', names.Select(name => ResourceNameHelpers.TruncateWithHash(name, 53))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void LimitMustAllowReadablePrefixAndFullHash(int maxLength)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ResourceNameHelpers.TruncateWithHash("aks", maxLength));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NameMustNotBeEmpty(string? name)
    {
        Assert.ThrowsAny<ArgumentException>(() => ResourceNameHelpers.TruncateWithHash(name!, 64));
    }
}
