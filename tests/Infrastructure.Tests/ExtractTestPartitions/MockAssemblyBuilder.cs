// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Infrastructure.Tests;

/// <summary>
/// Builds mock test assemblies dynamically using Roslyn for testing ExtractTestPartitions.
/// </summary>
public static class MockAssemblyBuilder
{
    /// <summary>
    /// Creates an assembly with test classes having [Trait("Partition", "name")] attributes.
    /// </summary>
    public static string CreateAssemblyWithPartitions(
        string outputPath,
        params (string ClassName, string PartitionName)[] partitions)
    {
        var code = new StringBuilder();
        code.AppendLine("using System;");
        code.AppendLine();
        code.AppendLine("[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]");
        code.AppendLine("public class TraitAttribute : Attribute");
        code.AppendLine("{");
        code.AppendLine("    public TraitAttribute(string name, string value) { Name = name; Value = value; }");
        code.AppendLine("    public string Name { get; }");
        code.AppendLine("    public string Value { get; }");
        code.AppendLine("}");
        code.AppendLine();

        foreach (var (className, partitionName) in partitions)
        {
            code.AppendLine($"[Trait(\"Partition\", \"{partitionName}\")]");
            code.AppendLine($"public class {className}");
            code.AppendLine("{");
            code.AppendLine("    public void TestMethod() { }");
            code.AppendLine("}");
            code.AppendLine();
        }

        return CompileAssembly(outputPath, code.ToString());
    }

    /// <summary>
    /// Creates an assembly with test classes having [Collection("name")] attributes.
    /// </summary>
    public static string CreateAssemblyWithCollections(
        string outputPath,
        params (string ClassName, string CollectionName)[] collections)
    {
        var code = new StringBuilder();
        code.AppendLine("using System;");
        code.AppendLine();
        code.AppendLine("[AttributeUsage(AttributeTargets.Class)]");
        code.AppendLine("public class CollectionAttribute : Attribute");
        code.AppendLine("{");
        code.AppendLine("    public CollectionAttribute(string name) { Name = name; }");
        code.AppendLine("    public string Name { get; }");
        code.AppendLine("}");
        code.AppendLine();

        foreach (var (className, collectionName) in collections)
        {
            code.AppendLine($"[Collection(\"{collectionName}\")]");
            code.AppendLine($"public class {className}");
            code.AppendLine("{");
            code.AppendLine("    public void TestMethod() { }");
            code.AppendLine("}");
            code.AppendLine();
        }

        return CompileAssembly(outputPath, code.ToString());
    }

    /// <summary>
    /// Creates an assembly with test classes having both [Trait] and [Collection] attributes.
    /// </summary>
    public static string CreateAssemblyWithMixedAttributes(
        string outputPath,
        (string ClassName, string PartitionName)[] partitions,
        (string ClassName, string CollectionName)[] collections)
    {
        var code = new StringBuilder();
        code.AppendLine("using System;");
        code.AppendLine();
        code.AppendLine("[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]");
        code.AppendLine("public class TraitAttribute : Attribute");
        code.AppendLine("{");
        code.AppendLine("    public TraitAttribute(string name, string value) { Name = name; Value = value; }");
        code.AppendLine("    public string Name { get; }");
        code.AppendLine("    public string Value { get; }");
        code.AppendLine("}");
        code.AppendLine();
        code.AppendLine("[AttributeUsage(AttributeTargets.Class)]");
        code.AppendLine("public class CollectionAttribute : Attribute");
        code.AppendLine("{");
        code.AppendLine("    public CollectionAttribute(string name) { Name = name; }");
        code.AppendLine("    public string Name { get; }");
        code.AppendLine("}");
        code.AppendLine();

        foreach (var (className, partitionName) in partitions)
        {
            code.AppendLine($"[Trait(\"Partition\", \"{partitionName}\")]");
            code.AppendLine($"public class {className}");
            code.AppendLine("{");
            code.AppendLine("    public void TestMethod() { }");
            code.AppendLine("}");
            code.AppendLine();
        }

        foreach (var (className, collectionName) in collections)
        {
            code.AppendLine($"[Collection(\"{collectionName}\")]");
            code.AppendLine($"public class {className}");
            code.AppendLine("{");
            code.AppendLine("    public void TestMethod() { }");
            code.AppendLine("}");
            code.AppendLine();
        }

        return CompileAssembly(outputPath, code.ToString());
    }

    /// <summary>
    /// Creates an assembly with test classes having no partition/collection attributes.
    /// </summary>
    public static string CreateAssemblyWithNoAttributes(string outputPath, params string[] classNames)
    {
        var code = new StringBuilder();
        code.AppendLine("using System;");
        code.AppendLine();

        var names = classNames.Length > 0 ? classNames : new[] { "TestClass1", "TestClass2" };

        foreach (var className in names)
        {
            code.AppendLine($"public class {className}");
            code.AppendLine("{");
            code.AppendLine("    public void TestMethod() { }");
            code.AppendLine("}");
            code.AppendLine();
        }

        return CompileAssembly(outputPath, code.ToString());
    }

    /// <summary>
    /// Creates an assembly with classes having duplicate partition names (different casing).
    /// </summary>
    public static string CreateAssemblyWithDuplicatePartitions(
        string outputPath,
        params (string ClassName, string PartitionName)[] partitions)
    {
        // Same as CreateAssemblyWithPartitions - the deduplication is in ExtractTestPartitions
        return CreateAssemblyWithPartitions(outputPath, partitions);
    }

    /// <summary>
    /// Creates an assembly with a nested class having a partition attribute.
    /// </summary>
    public static string CreateAssemblyWithNestedTypePartitions(
        string outputPath,
        params (string OuterClassName, string InnerClassName, string PartitionName)[] nestedPartitions)
    {
        var code = new StringBuilder();
        code.AppendLine("using System;");
        code.AppendLine();
        code.AppendLine("[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]");
        code.AppendLine("public class TraitAttribute : Attribute");
        code.AppendLine("{");
        code.AppendLine("    public TraitAttribute(string name, string value) { Name = name; Value = value; }");
        code.AppendLine("    public string Name { get; }");
        code.AppendLine("    public string Value { get; }");
        code.AppendLine("}");
        code.AppendLine();

        foreach (var (outerClassName, innerClassName, partitionName) in nestedPartitions)
        {
            code.AppendLine($"public class {outerClassName}");
            code.AppendLine("{");
            code.AppendLine($"    [Trait(\"Partition\", \"{partitionName}\")]");
            code.AppendLine($"    public class {innerClassName}");
            code.AppendLine("    {");
            code.AppendLine("        public void TestMethod() { }");
            code.AppendLine("    }");
            code.AppendLine("}");
            code.AppendLine();
        }

        return CompileAssembly(outputPath, code.ToString());
    }

    /// <summary>
    /// Creates an assembly with Trait attributes of various keys (not just "Partition").
    /// </summary>
    public static string CreateAssemblyWithNonPartitionTraits(
        string outputPath,
        params (string ClassName, string TraitKey, string TraitValue)[] traits)
    {
        var code = new StringBuilder();
        code.AppendLine("using System;");
        code.AppendLine();
        code.AppendLine("[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]");
        code.AppendLine("public class TraitAttribute : Attribute");
        code.AppendLine("{");
        code.AppendLine("    public TraitAttribute(string name, string value) { Name = name; Value = value; }");
        code.AppendLine("    public string Name { get; }");
        code.AppendLine("    public string Value { get; }");
        code.AppendLine("}");
        code.AppendLine();

        foreach (var (className, traitKey, traitValue) in traits)
        {
            code.AppendLine($"[Trait(\"{traitKey}\", \"{traitValue}\")]");
            code.AppendLine($"public class {className}");
            code.AppendLine("{");
            code.AppendLine("    public void TestMethod() { }");
            code.AppendLine("}");
            code.AppendLine();
        }

        return CompileAssembly(outputPath, code.ToString());
    }

    private static string CompileAssembly(string outputPath, string code)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(code);

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location)
        };

        // Add netstandard reference for compilation
        var netstandardPath = Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!,
            "netstandard.dll");
        if (File.Exists(netstandardPath))
        {
            references.Add(MetadataReference.CreateFromFile(netstandardPath));
        }

        // Add System.Runtime reference
        var runtimePath = Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!,
            "System.Runtime.dll");
        if (File.Exists(runtimePath))
        {
            references.Add(MetadataReference.CreateFromFile(runtimePath));
        }

        var compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(outputPath),
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        EmitResult result = compilation.Emit(outputPath);

        if (!result.Success)
        {
            var errors = string.Join(Environment.NewLine,
                result.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.ToString()));
            throw new InvalidOperationException($"Failed to compile mock assembly:{Environment.NewLine}{errors}");
        }

        return outputPath;
    }
}
