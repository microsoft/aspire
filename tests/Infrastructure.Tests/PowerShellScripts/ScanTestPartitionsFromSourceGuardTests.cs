// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Exercises the build-free partition discovery used by the CI runsheet builder.
/// </summary>
public sealed class ScanTestPartitionsFromSourceGuardTests(ITestOutputHelper output) : IDisposable
{
    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(output);
    private readonly string _scriptPath = Path.Combine(
        RepoRoot.Path,
        "eng",
        "scripts",
        "scan-test-partitions-from-source.ps1");

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task ScannerFindsSupportedFormsAndSkipsBuildOutputSources()
    {
        var sourceDirectory = _workspace.CreateDirectory("synthetic-source").FullName;
        var traitName = "Trait";
        await File.WriteAllTextAsync(
            Path.Combine(sourceDirectory, "Tests.cs"),
            $$"""
            [{{traitName}}("Partition", "beta")]
            public class BetaTests;

            [Xunit.{{traitName}}Attribute( "partition" , "alpha" )]
            public class AlphaTests;

            [{{traitName}}("Partition", "BETA")]
            public class DuplicateBetaTests;
            """);
        var objDirectory = Directory.CreateDirectory(Path.Combine(sourceDirectory, "obj"));
        await File.WriteAllTextAsync(
            Path.Combine(objDirectory.FullName, "Generated.cs"),
            $$"""[{{traitName}}("Partition", "generated")] public class GeneratedTests;""");
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "Empty.cs"), string.Empty);

        var outputFile = Path.Combine(_workspace.Path, "partitions.json");
        var result = await RunScannerAsync(sourceDirectory, outputFile);

        result.EnsureSuccessful();
        Assert.Equal(
            ["collection:alpha", "collection:beta", "uncollected:*"],
            await ReadPartitionsAsync(outputFile));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task ScannerRemovesStaleOutputWhenSourceHasNoPartitions()
    {
        var sourceDirectory = _workspace.CreateDirectory("source-without-partitions").FullName;
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "Tests.cs"), "public class Tests;");
        var outputFile = Path.Combine(_workspace.Path, "stale-partitions.json");
        await File.WriteAllTextAsync(outputFile, """{"testPartitions":["collection:stale"]}""");

        var result = await RunScannerAsync(sourceDirectory, outputFile);

        result.EnsureSuccessful();
        Assert.False(File.Exists(outputFile));
        Assert.Contains("Falling back to class-mode", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task ScannerDiscoversEveryRepositoryPartitionTrait()
    {
        var testsRoot = Path.Combine(RepoRoot.Path, "tests");
        var outputFile = Path.Combine(_workspace.Path, "repository-partitions.json");
        var result = await RunScannerAsync(testsRoot, outputFile);
        result.EnsureSuccessful();

        var expectedPartitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unsupportedForms = new List<string>();

        foreach (var file in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file))
            {
                continue;
            }

            var root = CSharpSyntaxTree.ParseText(await File.ReadAllTextAsync(file)).GetRoot();
            foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>().Where(IsPartitionTrait))
            {
                var relativePath = Path.GetRelativePath(RepoRoot.Path, file).Replace('\\', '/');
                var arguments = attribute.ArgumentList!.Arguments;
                if (attribute.Parent is not AttributeListSyntax { Attributes.Count: 1 })
                {
                    unsupportedForms.Add($"{relativePath}: the Partition trait must be the only attribute in its attribute list.");
                }

                if (arguments[1].Expression is not LiteralExpressionSyntax value ||
                    !value.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression))
                {
                    unsupportedForms.Add($"{relativePath}: the Partition value must be a string literal.");
                    continue;
                }

                expectedPartitions.Add($"collection:{value.Token.ValueText}");
            }
        }

        Assert.Empty(unsupportedForms);
        expectedPartitions.Add("uncollected:*");
        var scannedPartitions = await ReadPartitionsAsync(outputFile);
        // The text scan can also collect examples from comments in non-split support projects.
        // Missing a real runtime trait is unsafe; harmless extra source matches are not.
        Assert.Empty(expectedPartitions.Except(scannedPartitions, StringComparer.OrdinalIgnoreCase));
    }

    public void Dispose() => _workspace.Dispose();

    private async Task<CommandResult> RunScannerAsync(string projectDirectory, string outputFile)
    {
        using var command = new PowerShellCommand(_scriptPath, output)
            .WithTimeout(TimeSpan.FromMinutes(1));

        return await command.ExecuteAsync(
            "-ProjectDirectory",
            projectDirectory,
            "-TestPartitionsJsonFile",
            outputFile);
    }

    private static async Task<string[]> ReadPartitionsAsync(string outputFile)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputFile));
        return document.RootElement
            .GetProperty("testPartitions")
            .EnumerateArray()
            .Select(element => element.GetString()!)
            .ToArray();
    }

    private static bool IsPartitionTrait(AttributeSyntax attribute)
    {
        var arguments = attribute.ArgumentList?.Arguments;
        if (arguments is null || arguments.Value.Count < 2)
        {
            return false;
        }

        var attributeName = attribute.Name.ToString().Split('.').Last();
        if (attributeName is not ("Trait" or "TraitAttribute"))
        {
            return false;
        }

        return arguments.Value[0].Expression is LiteralExpressionSyntax key &&
            key.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression) &&
            string.Equals(key.Token.ValueText, "Partition", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBuildOutput(string file)
        => file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}
