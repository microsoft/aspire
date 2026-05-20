// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.Hosting.Maui.Utilities;

namespace Aspire.Hosting.Tests;

/// <summary>
/// Tests for MauiEnvironmentHelper utility methods including targets file generation,
/// semicolon encoding, and filename sanitization.
/// </summary>
public class MauiEnvironmentHelperTests
{
    [Fact]
    public void GenerateAndroidTargetsFileContent_ProducesValidXml()
    {
        var envVars = new Dictionary<string, string>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317",
            ["MY_VAR"] = "hello"
        };

        var content = MauiEnvironmentHelper.GenerateAndroidTargetsFileContent(envVars);

        // Should be valid XML
        var doc = XDocument.Parse(content);
        Assert.NotNull(doc.Root);
        Assert.Equal("Project", doc.Root.Name.LocalName);
    }

    [Fact]
    public void GenerateAndroidTargetsFileContent_ContainsEnvironmentVariablesInItemGroup()
    {
        var envVars = new Dictionary<string, string>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317",
            ["MY_VAR"] = "hello"
        };

        var content = MauiEnvironmentHelper.GenerateAndroidTargetsFileContent(envVars);
        var doc = XDocument.Parse(content);

        var items = doc.Descendants("_GeneratedAndroidEnvironment").ToList();
        Assert.Equal(2, items.Count);

        // Items should be ordered by key
        Assert.Equal("MY_VAR=hello", items[0].Attribute("Include")?.Value);
        Assert.Equal("OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317", items[1].Attribute("Include")?.Value);
    }

    [Fact]
    public void GenerateAndroidTargetsFileContent_ContainsAspireTargetDefinition()
    {
        var envVars = new Dictionary<string, string>
        {
            ["MY_VAR"] = "value"
        };

        var content = MauiEnvironmentHelper.GenerateAndroidTargetsFileContent(envVars);
        var doc = XDocument.Parse(content);

        var target = doc.Descendants("Target")
            .FirstOrDefault(t => t.Attribute("Name")?.Value == "AspireGenerateAndroidEnvironmentFiles");
        Assert.NotNull(target);
        Assert.Equal("_GenerateEnvironmentFiles", target.Attribute("BeforeTargets")?.Value);
    }

    [Fact]
    public void GenerateAndroidTargetsFileContent_EncodesEnvironmentFilePath()
    {
        var envVars = new Dictionary<string, string>
        {
            ["KEY"] = "value"
        };

        var content = MauiEnvironmentHelper.GenerateAndroidTargetsFileContent(envVars);
        var doc = XDocument.Parse(content);

        var writeLines = doc.Descendants("WriteLinesToFile").FirstOrDefault();
        Assert.NotNull(writeLines);
        Assert.Equal("$(IntermediateOutputPath)__aspire_environment__.txt", writeLines.Attribute("File")?.Value);
    }

    [Fact]
    public void GenerateiOSTargetsFileContent_ProducesValidXml()
    {
        var envVars = new Dictionary<string, string>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317",
            ["MY_VAR"] = "hello"
        };

        var content = MauiEnvironmentHelper.GenerateiOSTargetsFileContent(envVars);

        var doc = XDocument.Parse(content);
        Assert.NotNull(doc.Root);
        Assert.Equal("Project", doc.Root.Name.LocalName);
    }

    [Fact]
    public void GenerateiOSTargetsFileContent_ContainsMlaunchEnvironmentVariables()
    {
        var envVars = new Dictionary<string, string>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317",
            ["MY_VAR"] = "hello"
        };

        var content = MauiEnvironmentHelper.GenerateiOSTargetsFileContent(envVars);
        var doc = XDocument.Parse(content);

        var items = doc.Descendants("MlaunchEnvironmentVariables").ToList();
        Assert.Equal(2, items.Count);

        // Items should be ordered by key
        Assert.Equal("MY_VAR=hello", items[0].Attribute("Include")?.Value);
        Assert.Equal("OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317", items[1].Attribute("Include")?.Value);
    }

    [Fact]
    public void GenerateiOSTargetsFileContent_ContainsDiagnosticTarget()
    {
        var envVars = new Dictionary<string, string>
        {
            ["MY_VAR"] = "value"
        };

        var content = MauiEnvironmentHelper.GenerateiOSTargetsFileContent(envVars);
        var doc = XDocument.Parse(content);

        var target = doc.Descendants("Target")
            .FirstOrDefault(t => t.Attribute("Name")?.Value == "AspireLogMlaunchEnvironmentVariables");
        Assert.NotNull(target);
        Assert.Equal("PrepareForBuild", target.Attribute("AfterTargets")?.Value);
    }

    [Fact]
    public void GenerateiOSTargetsFileContent_EncodesSemicolonsInValues()
    {
        var envVars = new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin;/usr/local/bin"
        };

        var content = MauiEnvironmentHelper.GenerateiOSTargetsFileContent(envVars);
        var doc = XDocument.Parse(content);

        var item = doc.Descendants("MlaunchEnvironmentVariables").Single();
        // Semicolons should be encoded as %3B to prevent MSBuild item separation
        Assert.Equal("PATH=/usr/bin%3B/usr/local/bin", item.Attribute("Include")?.Value);
    }

    [Theory]
    [InlineData("simple-value", "simple-value", false)]
    [InlineData("has;semicolons", "has%3Bsemicolons", true)]
    [InlineData("multiple;semi;colons", "multiple%3Bsemi%3Bcolons", true)]
    [InlineData("", "", false)]
    [InlineData("no-special-chars", "no-special-chars", false)]
    public void EncodeSemicolons_EncodesCorrectly(string input, string expectedOutput, bool expectedWasEncoded)
    {
        var result = MauiEnvironmentHelper.EncodeSemicolons(input, out var wasEncoded);

        Assert.Equal(expectedOutput, result);
        Assert.Equal(expectedWasEncoded, wasEncoded);
    }

    [Theory]
    [InlineData("simple-name", "simple-name")]
    [InlineData("name-with-dots.here", "name-with-dots.here")]
    [InlineData("valid_file_name", "valid_file_name")]
    public void SanitizeFileName_ValidNames_ReturnsUnchanged(string input, string expected)
    {
        var result = MauiEnvironmentHelper.SanitizeFileName(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SanitizeFileName_InvalidChars_ReplacesWithUnderscore()
    {
        // Build a test string with a printable character that's invalid on the current platform.
        // Skip '\0' since it has special behavior in .NET string comparisons.
        var invalidChar = Path.GetInvalidFileNameChars()
            .FirstOrDefault(c => c != '\0');
        if (invalidChar == '\0')
        {
            Assert.Skip("No printable invalid filename characters on this platform");
            return;
        }

        var input = $"name{invalidChar}test";
        var result = MauiEnvironmentHelper.SanitizeFileName(input);

        Assert.DoesNotContain(invalidChar.ToString(), result);
        Assert.Equal("name_test", result);
    }
}
