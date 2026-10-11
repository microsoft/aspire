// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.DotNet;

namespace Aspire.Cli.Tests.DotNet;

public class MSBuildEscapingTests
{
    [Theory]
    [InlineData("ordinary path/value", "ordinary path/value")]
    [InlineData("%*?@$();'", "%25%2A%3F%40%24%28%29%3B%27")]
    [InlineData("already%3Bescaped", "already%253Bescaped")]
    public void Escape_EncodesMSBuildSpecialCharactersExactlyOnce(string value, string expected)
        => Assert.Equal(expected, MSBuildEscaping.Escape(value));
}
