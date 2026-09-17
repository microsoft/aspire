// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.Loader;
using Aspire.Hosting.Utils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Aspire.Hosting.Tests;

public class ResourceProjectionBinaryCompatibilityTests
{
    [Fact]
    public void IntegrationCompiledAgainstAspireHosting135LoadsMutatesAnnotationsAndQueriesEffectiveResources()
    {
        var baselineAssemblyPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "Aspire.Hosting.13.5.3.dll");
        var baselineAssemblyName = AssemblyName.GetAssemblyName(baselineAssemblyPath);

        // Compile a community-integration-shaped resource against the released 13.5 binary. Loading
        // the emitted assembly below binds that reference to the current Aspire.Hosting assembly.
        const string source = """
            using System;
            using System.Linq;
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;

            public sealed class LegacyAnnotation : IResourceAnnotation;

            public sealed class LegacyResource(string name) : IResource
            {
                public string Name { get; } = name;

                public ResourceAnnotationCollection Annotations { get; } = [];
            }

            public static class LegacyIntegration
            {
                public static IResource CreateResource()
                {
                    var resource = new LegacyResource("legacy");
                    resource.Annotations.Add(new LegacyAnnotation());
                    return resource;
                }

                public static ContainerResource? FindContainer(
                    IDistributedApplicationBuilder builder,
                    string name)
                {
                    return builder.Resources
                        .OfType<ContainerResource>()
                        .SingleOrDefault(resource => string.Equals(resource.Name, name, StringComparison.OrdinalIgnoreCase));
                }
            }
            """;

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => !string.Equals(
                Path.GetFileName(path),
                "Aspire.Hosting.dll",
                StringComparison.OrdinalIgnoreCase))
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(baselineAssemblyPath));
        var compilation = CSharpCompilation.Create(
            $"LegacyIntegration_{Guid.NewGuid():N}",
            [CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp13))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var assemblyStream = new MemoryStream();
        var result = compilation.Emit(assemblyStream);
        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));

        assemblyStream.Position = 0;
        var legacyAssembly = AssemblyLoadContext.Default.LoadFromStream(assemblyStream);
        Assert.Equal(
            baselineAssemblyName.Version,
            legacyAssembly.GetReferencedAssemblies().Single(name => name.Name == "Aspire.Hosting").Version);

        var factory = legacyAssembly.GetType("LegacyIntegration", throwOnError: true)!;
        var resource = Assert.IsAssignableFrom<IResource>(
            factory.GetMethod("CreateResource", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null));

        Assert.Equal("legacy", resource.Name);
        Assert.Single(resource.Annotations);

        var currentAnnotation = new ContainerImageAnnotation { Image = "current-image" };
        resource.Annotations.Add(currentAnnotation);

        Assert.Same(currentAnnotation, resource.Annotations[1]);
        Assert.Equal(2, resource.Annotations.Count);

        using var builder = TestDistributedApplicationBuilder.Create();
        var owner = builder.AddExecutable("worker", "worker", ".")
            .RunAsContainerImage("contoso/worker:1.0");
        var projection = owner.Resource.AsContainer();
        var discoveredContainer = factory.GetMethod(
            "FindContainer",
            BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [builder, owner.Resource.Name]);

        Assert.NotNull(projection);
        Assert.Same(projection, discoveredContainer);
        var discoveredResource = Assert.IsAssignableFrom<IResource>(discoveredContainer);
        Assert.Same(owner.Resource, discoveredResource.GetOwnerOrSelf());
    }
}
