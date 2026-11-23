// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Docker.Tests;

public class EnvFileTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData('\'', true)]
    [InlineData('\'', false)]
    [InlineData('"', true)]
    [InlineData('"', false)]
    public async Task Load_MultilineQuotedValue_PreservesContentOnSave(char quote, bool includeValues)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var envFilePath = Path.Combine(workspace.Path, ".env");
        var value = string.Join(Environment.NewLine, [
            $"  {quote}hello \\{quote}world",
            "",
            "# This is part of the value",
            "INNER=value",
            $"goodbye{quote} # Trailing comment"
        ]);
        File.WriteAllText(envFilePath, $"# Banner description{Environment.NewLine}BANNER={value}{Environment.NewLine}TAIL=preserved{Environment.NewLine}");

        var envFile = EnvFile.Load(envFilePath);

        Assert.Collection(envFile.Entries.Values,
            entry => Assert.Equal(new EnvEntry("BANNER", value, "Banner description"), entry),
            entry => Assert.Equal(new EnvEntry("TAIL", "preserved", null), entry));

        envFile.Add("NEW_KEY", null, "New publisher placeholder");
        envFile.Save(includeValues);

        await Verify(File.ReadAllText(envFilePath), "env")
            .UseParameters(quote == '\'' ? "single" : "double", includeValues);
    }

    [Theory]
    [InlineData("unquoted ' value")]
    [InlineData("'single' # comment with '")]
    [InlineData("\"double\" # comment with \"")]
    [InlineData("'escaped \\' quote'")]
    [InlineData("\"escaped \\\" quote\"")]
    [InlineData("'trailing backslashes \\\\'")]
    [InlineData("\"trailing backslashes \\\\\"")]
    public void Load_SingleLineValue_DoesNotConsumeNextEntry(string value)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var envFilePath = Path.Combine(workspace.Path, ".env");
        File.WriteAllLines(envFilePath, [$"VALUE={value}", "AFTER=preserved"]);

        var envFile = EnvFile.Load(envFilePath);

        Assert.Collection(envFile.Entries.Values,
            entry => Assert.Equal(new EnvEntry("AFTER", "preserved", null), entry),
            entry => Assert.Equal(new EnvEntry("VALUE", value, null), entry));
    }

    [Theory]
    [InlineData("'\n'")]
    [InlineData("\"\n\"")]
    [InlineData("'hello\nescaped \\' quote\nworld'")]
    [InlineData("\"hello\nescaped \\\" quote\nworld\"")]
    [InlineData("'hello\ntrailing backslashes \\\\'")]
    [InlineData("\"hello\ntrailing backslashes \\\\\"")]
    public void Load_MultilineQuotedValue_DoesNotConsumeNextEntry(string value)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var envFilePath = Path.Combine(workspace.Path, ".env");
        value = value.Replace("\n", Environment.NewLine);
        File.WriteAllText(envFilePath, $"VALUE={value}{Environment.NewLine}AFTER=preserved{Environment.NewLine}");

        var envFile = EnvFile.Load(envFilePath);

        Assert.Collection(envFile.Entries.Values,
            entry => Assert.Equal(new EnvEntry("AFTER", "preserved", null), entry),
            entry => Assert.Equal(new EnvEntry("VALUE", value, null), entry));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Load_MultilineQuotedValue_PreservesOriginalLineEndings(string lineEnding)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var envFilePath = Path.Combine(workspace.Path, ".env");
        var value = $"'hello{lineEnding}world'";
        File.WriteAllText(envFilePath, $"VALUE={value}{lineEnding}AFTER=preserved{lineEnding}");

        var envFile = EnvFile.Load(envFilePath);
        Assert.Equal(value, envFile.Entries["VALUE"].Value);
        envFile.Save(includeValues: false);
        var reloaded = EnvFile.Load(envFilePath);

        Assert.Collection(reloaded.Entries.Values,
            entry => Assert.Equal(new EnvEntry("AFTER", "preserved", null), entry),
            entry => Assert.Equal(new EnvEntry("VALUE", value, null), entry));
    }

    [Theory]
    [InlineData('\'')]
    [InlineData('"')]
    public void Load_UnterminatedQuotedValue_ThrowsWithoutRewritingFile(char quote)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var envFilePath = Path.Combine(workspace.Path, ".env");
        File.WriteAllLines(envFilePath, [$"VALUE={quote}hello", "world"]);

        var exception = Assert.Throws<FormatException>(() => EnvFile.Load(envFilePath));

        Assert.Equal("Unterminated quoted value for environment variable 'VALUE'.", exception.Message);
        Assert.Equal([$"VALUE={quote}hello", "world"], File.ReadAllLines(envFilePath));
    }

    [Theory]
    [InlineData('\'')]
    [InlineData('"')]
    public void Load_BackslashBeforeLineEnding_DoesNotEscapeClosingQuoteOnNextLine(char quote)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var envFilePath = Path.Combine(workspace.Path, ".env");
        var value = $"{quote}one\\{Environment.NewLine}{quote}";
        File.WriteAllText(envFilePath, $"VALUE={value}{Environment.NewLine}AFTER=preserved{Environment.NewLine}");

        var envFile = EnvFile.Load(envFilePath);
        Assert.Collection(envFile.Entries.Values,
            entry => Assert.Equal(new EnvEntry("AFTER", "preserved", null), entry),
            entry => Assert.Equal(new EnvEntry("VALUE", value, null), entry));

        envFile.Save(includeValues: false);
        var reloaded = EnvFile.Load(envFilePath);
        Assert.Collection(reloaded.Entries.Values,
            entry => Assert.Equal(new EnvEntry("AFTER", "preserved", null), entry),
            entry => Assert.Equal(new EnvEntry("VALUE", value, null), entry));
    }

    [Theory]
    [InlineData('\'')]
    [InlineData('"')]
    public void Load_MultilineClosingQuoteAtEndOfFile_DoesNotRequireFinalLineEnding(char quote)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var envFilePath = Path.Combine(workspace.Path, ".env");
        var value = $"{quote}one{Environment.NewLine}two{quote}";
        File.WriteAllText(envFilePath, $"VALUE={value}");

        var envFile = EnvFile.Load(envFilePath);
        Assert.Equal(new EnvEntry("VALUE", value, null), Assert.Single(envFile.Entries.Values));

        envFile.Save(includeValues: false);
        var reloaded = EnvFile.Load(envFilePath);
        Assert.Equal(new EnvEntry("VALUE", value, null), Assert.Single(reloaded.Entries.Values));
    }

    [Theory]
    [InlineData('\'', true)]
    [InlineData('\'', false)]
    [InlineData('"', true)]
    [InlineData('"', false)]
    public void Load_AssignmentAfterMultilineClosingQuote_PreservesOverridePrecedence(char quote, bool includeValues)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var envFilePath = Path.Combine(workspace.Path, ".env");
        var value = $"{quote}hello{Environment.NewLine}world{quote}";
        File.WriteAllText(envFilePath, $"Z={value} INNER=evil{Environment.NewLine}INNER=safe{Environment.NewLine}");

        var envFile = EnvFile.Load(envFilePath);
        Assert.Collection(envFile.Entries.Values,
            entry => Assert.Equal(new EnvEntry("INNER", "safe", null), entry),
            entry => Assert.Equal(new EnvEntry("Z", value, null), entry));

        envFile.Save(includeValues);
        var reloaded = EnvFile.Load(envFilePath);
        Assert.Collection(reloaded.Entries.Values,
            entry => Assert.Equal(new EnvEntry("INNER", "safe", null), entry),
            entry => Assert.Equal(new EnvEntry("Z", value, null), entry));
    }

    [Fact]
    public void Load_AdjacentMultilineQuotedAssignments_ParsesEachEntry()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var envFilePath = Path.Combine(workspace.Path, ".env");
        var firstValue = $"'first{Environment.NewLine}value'";
        var secondValue = $"\"second{Environment.NewLine}value\"";
        File.WriteAllText(envFilePath, $"Z={firstValue} A={secondValue} EXTRA=inline{Environment.NewLine}TAIL=preserved{Environment.NewLine}");

        var envFile = EnvFile.Load(envFilePath);
        Assert.Collection(envFile.Entries.Values,
            entry => Assert.Equal(new EnvEntry("A", secondValue, null), entry),
            entry => Assert.Equal(new EnvEntry("EXTRA", "inline", null), entry),
            entry => Assert.Equal(new EnvEntry("TAIL", "preserved", null), entry),
            entry => Assert.Equal(new EnvEntry("Z", firstValue, null), entry));

        envFile.Save(includeValues: false);
        var reloaded = EnvFile.Load(envFilePath);
        Assert.Collection(reloaded.Entries.Values,
            entry => Assert.Equal(new EnvEntry("A", secondValue, null), entry),
            entry => Assert.Equal(new EnvEntry("EXTRA", "inline", null), entry),
            entry => Assert.Equal(new EnvEntry("TAIL", "preserved", null), entry),
            entry => Assert.Equal(new EnvEntry("Z", firstValue, null), entry));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Load_BareCarriageReturnsInUnquotedValue_DoNotCreateAdditionalEntries(bool includeValues)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var envFilePath = Path.Combine(workspace.Path, ".env");
        const string value = "one\rINNER=evil\rAFTER=preserved";
        File.WriteAllText(envFilePath, $"VALUE={value}\r");

        var envFile = EnvFile.Load(envFilePath);
        Assert.Equal(new EnvEntry("VALUE", value, null), Assert.Single(envFile.Entries.Values));

        envFile.Save(includeValues);
        var reloaded = EnvFile.Load(envFilePath);
        Assert.Equal(new EnvEntry("VALUE", value, null), Assert.Single(reloaded.Entries.Values));
    }

    [Fact]
    public void Add_WithOnlyIfMissingTrue_DoesNotAddDuplicate()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var envFilePath = Path.Combine(workspace.Path, ".env");

        // Create initial .env file
        File.WriteAllLines(envFilePath, [
            "# Comment for KEY1",
            "KEY1=value1",
            ""
        ]);

        // Load and try to add the same key with onlyIfMissing=true
        var envFile = EnvFile.Load(envFilePath);
        envFile.Add("KEY1", "value2", "New comment", onlyIfMissing: true);
        envFile.Save();

        var lines = File.ReadAllLines(envFilePath);
        var keyLines = lines.Where(l => l.StartsWith("KEY1=")).ToArray();

        // Should still have only one KEY1 line with original value
        Assert.Single(keyLines);
        Assert.Equal("KEY1=value1", keyLines[0]);
    }

    [Fact]
    public void Add_WithOnlyIfMissingFalse_UpdatesExistingKey()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var envFilePath = Path.Combine(workspace.Path, ".env");

        // Create initial .env file
        File.WriteAllLines(envFilePath, [
            "# Comment for KEY1",
            "KEY1=value1",
            ""
        ]);

        // Load and try to add the same key with onlyIfMissing=false
        var envFile = EnvFile.Load(envFilePath);
        envFile.Add("KEY1", "value2", "New comment", onlyIfMissing: false);
        envFile.Save();

        var lines = File.ReadAllLines(envFilePath);
        var keyLines = lines.Where(l => l.StartsWith("KEY1=")).ToArray();

        // Should still have only one KEY1 line, but with updated value
        Assert.Single(keyLines);
        Assert.Equal("KEY1=value2", keyLines[0]);
    }

    [Fact]
    public void Add_WithOnlyIfMissingFalse_UpdatesImageNameWithoutDuplication()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var envFilePath = Path.Combine(workspace.Path, ".env");

        // Create initial .env file simulating a project resource
        File.WriteAllLines(envFilePath, [
            "# Default container port for project1",
            "PROJECT1_PORT=8080",
            "",
            "# Container image name for project1",
            "PROJECT1_IMAGE=project1:latest",
            ""
        ]);

        // Load the file
        var envFile = EnvFile.Load(envFilePath);

        // Add PORT with onlyIfMissing=true (should be skipped since it exists)
        envFile.Add("PROJECT1_PORT", "8080", "Default container port for project1", onlyIfMissing: true);

        // Add IMAGE with onlyIfMissing=false (should update the existing value)
        envFile.Add("PROJECT1_IMAGE", "project1:1.0.0", "Container image name for project1", onlyIfMissing: false);

        envFile.Save();

        var lines = File.ReadAllLines(envFilePath);
        var imageLines = lines.Where(l => l.StartsWith("PROJECT1_IMAGE=")).ToArray();

        // Should have exactly one IMAGE line with the new value
        Assert.Single(imageLines);
        Assert.Equal("PROJECT1_IMAGE=project1:1.0.0", imageLines[0]);

        // PORT should also still be present once
        var portLines = lines.Where(l => l.StartsWith("PROJECT1_PORT=")).ToArray();
        Assert.Single(portLines);
        Assert.Equal("PROJECT1_PORT=8080", portLines[0]);
    }

    [Fact]
    public void Add_NewKey_AddsToFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var envFilePath = Path.Combine(workspace.Path, ".env");

        // Create initial .env file
        File.WriteAllLines(envFilePath, [
            "# Comment for KEY1",
            "KEY1=value1",
            ""
        ]);

        // Load and add a new key
        var envFile = EnvFile.Load(envFilePath);
        envFile.Add("KEY2", "value2", "Comment for KEY2", onlyIfMissing: true);
        envFile.Save();

        var lines = File.ReadAllLines(envFilePath);

        // Should have both keys
        Assert.Contains("KEY1=value1", lines);
        Assert.Contains("KEY2=value2", lines);
    }

    [Fact]
    public void Load_EmptyFile_ReturnsEmptyEnvFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var envFilePath = Path.Combine(workspace.Path, ".env");

        // Create empty file
        File.WriteAllText(envFilePath, string.Empty);

        var envFile = EnvFile.Load(envFilePath);
        envFile.Add("KEY1", "value1", "Comment");
        envFile.Save();

        var lines = File.ReadAllLines(envFilePath);
        Assert.Contains("KEY1=value1", lines);
    }

    [Fact]
    public void Load_NonExistentFile_ReturnsEmptyEnvFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var envFilePath = Path.Combine(workspace.Path, ".env");

        // Don't create the file
        var envFile = EnvFile.Load(envFilePath);
        envFile.Add("KEY1", "value1", "Comment");
        envFile.Save();

        Assert.True(File.Exists(envFilePath));
        var lines = File.ReadAllLines(envFilePath);
        Assert.Contains("KEY1=value1", lines);
    }
}
