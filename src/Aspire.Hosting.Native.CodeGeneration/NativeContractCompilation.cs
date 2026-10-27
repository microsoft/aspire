// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Aspire.Hosting.Native.Model;

namespace Aspire.Hosting.Native.CodeGeneration;

/// <summary>Compiles the native declarations for build-time ATS scanning without a server project dependency.</summary>
internal static class NativeContractCompilation
{
    public static Assembly Compile(string outputDirectory)
    {
        // The server needs generated dispatch before it can compile, so the generator
        // cannot reference it. Compile its exact API sources against the kernel
        // with the server's assembly identity, preserving the exported type IDs.
        const string assemblyName = "Aspire.Hosting.Native.Server";
        var tool = typeof(NativeContractCompilation).Assembly;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.Diagnose);
        var sources = tool.GetManifestResourceNames()
            .Where(name => name.StartsWith("NativeContract/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = tool.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);

                return CSharpSyntaxTree.ParseText(reader.ReadToEnd(), parseOptions, name, Encoding.UTF8);
            }).ToList();
        if (sources.Count == 0)
        {
            throw new InvalidOperationException("The native contract sources are missing.");
        }
        sources.Add(CSharpSyntaxTree.ParseText("""
            global using System;
            global using System.Collections.Generic;
            global using System.IO;
            global using System.Linq;
            global using System.Net.Http;
            global using System.Threading;
            global using System.Threading.Tasks;
            """, parseOptions, encoding: Encoding.UTF8));
        var references = GetRuntimeAssemblyPaths()
            .Append(typeof(AspireExportAttribute).Assembly.Location)
            .Append(typeof(ApplicationModel).Assembly.Location)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(assemblyName, sources, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                // The declarations use the same friend access as the real server.
                // Only its public identity is needed for build-time scanning.
                cryptoPublicKey: ImmutableArray.CreateRange(typeof(ApplicationModel).Assembly.GetName().GetPublicKey()!),
                delaySign: true));
        var directory = Directory.CreateDirectory(Path.Combine(outputDirectory, "contract"));
        var assemblyPath = Path.Combine(directory.FullName, $"{assemblyName}.dll");
        using (var assembly = File.Create(assemblyPath))
        using (var documentation = File.Create(Path.ChangeExtension(assemblyPath, ".xml")))
        {
            var result = compilation.Emit(assembly, xmlDocumentationStream: documentation);
            if (!result.Success)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine,
                    result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
            }
        }

        return AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
    }

    internal static IEnumerable<string> GetRuntimeAssemblyPaths()
    {
        var platformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new InvalidOperationException("The runtime's trusted platform assembly list is missing.");
        var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        // The Windows runtime directory also contains native DLLs such as coreclr.dll.
        // The runtime's trusted assembly list contains only managed compiler inputs.
        return platformAssemblies.Split(Path.PathSeparator)
            .Where(path => path.StartsWith(runtimeDirectory, StringComparison.Ordinal));
    }
}
