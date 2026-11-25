// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Infrastructure.Tests;

public sealed class TemporaryStorageTests
{
    [Theory]
    [InlineData("System.IO.Path.GetTempPath()")]
    [InlineData("System.IO.Path.GetTempFileName()")]
    [InlineData("System.IO.Directory.CreateTempSubdirectory()")]
    [InlineData("System.IO.Directory.CreateTempSubdirectory(\"prefix\")")]
    [InlineData("P.GetTempPath()")]
    public async Task ProductionPolicyRejectsSystemTemporaryAllocations(string expression)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "BannedApiAnalyzers");
        Assembly.LoadFrom(Path.Combine(directory, "Microsoft.CodeAnalysis.BannedApiAnalyzers.dll"));
        var assembly = Assembly.LoadFrom(Path.Combine(directory, "Microsoft.CodeAnalysis.CSharp.BannedApiAnalyzers.dll"));
        var type = Assert.Single(assembly.GetTypes(), type =>
            !type.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(type) && type.Name == "CSharpSymbolIsBannedAnalyzer");
        var analyzer = Assert.IsAssignableFrom<DiagnosticAnalyzer>(Activator.CreateInstance(type));
        var compilation = CSharpCompilation.Create("PolicyProbe",
            [CSharpSyntaxTree.ParseText($"using P = System.IO.Path; class C {{ object M() => {expression}; }}")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var options = new AnalyzerOptions([new PolicyFile(Path.Combine(RepoRoot.Path, "src", "BannedSymbols.txt"))]);
        var diagnostics = await compilation.WithAnalyzers([analyzer], options).GetAnalyzerDiagnosticsAsync();
        Assert.Equal("RS0030", Assert.Single(diagnostics).Id);
    }

    private sealed class PolicyFile(string path) : AdditionalText
    {
        public override string Path => path;

        public override SourceText GetText(CancellationToken cancellationToken = default)
            => SourceText.From(File.ReadAllText(path));
    }
}
