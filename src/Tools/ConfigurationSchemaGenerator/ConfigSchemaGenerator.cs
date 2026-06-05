// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Configuration.Binder.SourceGeneration;

namespace ConfigurationSchemaGenerator;

public partial class ConfigSchemaGenerator
{
    private const string ConfigurationSchemaAttributeName = "Aspire.ConfigurationSchemaAttribute";
    private const string LoggingCategoriesAttributeName = "Aspire.LoggingCategoriesAttribute";

    public static void GenerateSchema(string inputAssembly, string[] references, string outputFile)
    {
        var inputReference = CreateMetadataReference(inputAssembly);
        var compilation = CSharpCompilation.Create(
            "ConfigGenerator",
            references: references.Select(CreateMetadataReference)
                .Concat([inputReference]));

        var assemblySymbol = (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(inputReference);
        var configSchemaInfo = GetConfigurationSchema(assemblySymbol);

        if (configSchemaInfo is not null)
        {
            var parser = new ConfigurationBindingGenerator.Parser(configSchemaInfo, new KnownTypeSymbols(compilation));
            var spec = parser.GetSchemaGenerationSpec(CancellationToken.None);

            var emitter = new ConfigSchemaEmitter(spec, compilation);
            var schema = emitter.GenerateSchema();

            // Keep generated schemas stable across platforms and aligned with the checked-in JSON files.
            schema = schema.ReplaceLineEndings("\n");

            if (schema.Length == 0 || schema[^1] != '\n')
            {
                // Ensure the file always ends in a newline to stop certain text editors from injecting it.
                schema += '\n';
            }

            File.WriteAllText(outputFile, schema);
        }
    }

    internal static PortableExecutableReference CreateMetadataReference(string path)
    {
        var docPath = Path.ChangeExtension(path, "xml");
        var documentationProvider = XmlDocumentationProvider.CreateFromFile(docPath);

        return MetadataReference.CreateFromFile(path, documentation: documentationProvider);
    }

    internal static ConfigSchemaAttributeInfo? GetConfigurationSchema(IAssemblySymbol assembly)
    {
        List<INamedTypeSymbol>? types = null;
        List<string>? configurationPaths = null;
        List<string>? exclusionPaths = null;
        List<string>? logCategories = null;

        foreach (var attribute in assembly.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == ConfigurationSchemaAttributeName)
            {
                ImmutableArray<TypedConstant> args = attribute.ConstructorArguments;
                if (args.Length != 3)
                {
                    throw new InvalidOperationException("ConfigurationSchemaAttribute should only be used with 3 ctor arguments.");
                }

                var path = (string)args[0].Value;
                if (string.IsNullOrEmpty(path))
                {
                    path = $"{ConfigSchemaEmitter.RootPathPrefix}{Guid.NewGuid()}";
                }

                (configurationPaths ??= new()).Add(path);
                (types ??= new()).Add((INamedTypeSymbol)args[1].Value);

                var exclusionPathsArg = args[2];
                if (!exclusionPathsArg.IsNull)
                {
                    (exclusionPaths ??= new()).AddRange(exclusionPathsArg.Values.Select(v => $"{path}:{(string)v.Value}"));
                }
            }
            else if (attribute.AttributeClass?.ToDisplayString() == LoggingCategoriesAttributeName)
            {
                ImmutableArray<TypedConstant> args = attribute.ConstructorArguments;
                if (args.Length != 1)
                {
                    throw new InvalidOperationException("LoggingCategoriesAttribute should only be used with 1 ctor argument.");
                }

                (logCategories ??= new()).AddRange(args[0].Values.Select(v => (string)v.Value));
            }
        }

        if (types == null && logCategories == null)
        {
            return null;
        }

        return new ConfigSchemaAttributeInfo(types, configurationPaths, exclusionPaths, logCategories);
    }

    /// <summary>
    /// Data about configuration schema directly from the ConfigurationSchemaAttribute.
    /// </summary>
    internal sealed record ConfigSchemaAttributeInfo(List<INamedTypeSymbol>? Types, List<string>? ConfigurationPaths, List<string>? ExclusionPaths, List<string>? LogCategories);
}
