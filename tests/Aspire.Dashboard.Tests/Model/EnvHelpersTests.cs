// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class EnvHelpersTests
{
    [Fact]
    public void ConvertToEnvFormat_ReturnsExpectedFormat()
    {
        // Arrange
        var environmentVariables = new Dictionary<string, string?>
        {
            ["SIMPLE_VAR"] = "simple-value",
            ["VAR_WITH_SPACES"] = "value with spaces",
            ["EMPTY_VAR"] = ""
        };

        // Act
        var result = EnvHelpers.ConvertToEnvFormat(environmentVariables);

        // Assert
        var expected = """
            EMPTY_VAR=
            SIMPLE_VAR=simple-value
            VAR_WITH_SPACES="value with spaces"

            """;
        Assert.Equal(expected.Trim(), result.Trim(), ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void ConvertToEnvFormat_HandlesSpecialCharacters()
    {
        // Arrange
        var environmentVariables = new Dictionary<string, string?>
        {
            ["VAR_WITH_BACKSLASH"] = "path\\to\\file",
            ["VAR_WITH_BACKTICK"] = "value`with`backticks",
            ["VAR_WITH_DOLLAR"] = "$HOME/path",
            ["VAR_WITH_EQUALS"] = "key=value",
            ["VAR_WITH_HASH"] = "value#with#hashes",
            ["VAR_WITH_NEWLINE"] = "line1\nline2",
            ["VAR_WITH_QUOTES"] = "value with \"quotes\""
        };

        // Act
        var result = EnvHelpers.ConvertToEnvFormat(environmentVariables);

        // Assert
        var expected = """
            VAR_WITH_BACKSLASH="path\\to\\file"
            VAR_WITH_BACKTICK="value`with`backticks"
            VAR_WITH_DOLLAR="$HOME/path"
            VAR_WITH_EQUALS="key=value"
            VAR_WITH_HASH="value#with#hashes"
            VAR_WITH_NEWLINE="line1\nline2"
            VAR_WITH_QUOTES="value with \"quotes\""

            """;
        Assert.Equal(expected.Trim(), result.Trim(), ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void ConvertToEnvFormat_SortsVariablesAlphabetically()
    {
        // Arrange
        var environmentVariables = new Dictionary<string, string?>
        {
            ["ZEBRA"] = "last",
            ["APPLE"] = "first",
            ["MIDDLE"] = "middle"
        };

        // Act
        var result = EnvHelpers.ConvertToEnvFormat(environmentVariables);

        // Assert
        var expected = """
            APPLE=first
            MIDDLE=middle
            ZEBRA=last

            """;
        Assert.Equal(expected.Trim(), result.Trim(), ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void ConvertToEnvFormat_HandlesNullValue()
    {
        // Arrange
        var environmentVariables = new Dictionary<string, string?>
        {
            ["NULL_VAR"] = null
        };

        // Act
        var result = EnvHelpers.ConvertToEnvFormat(environmentVariables);

        // Assert
        var expected = """
            NULL_VAR=

            """;
        Assert.Equal(expected.Trim(), result.Trim(), ignoreLineEndingDifferences: true);
    }
}
