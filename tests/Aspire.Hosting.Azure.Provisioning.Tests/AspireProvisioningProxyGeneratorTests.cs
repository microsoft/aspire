// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Xunit;

namespace Aspire.Hosting.Azure.Provisioning.Tests;

public class AspireProvisioningProxyGeneratorTests
{
    [Fact]
    public void SupportedPropertyAndMethodShapesGenerateCompilableExports()
    {
        var result = ProvisioningGeneratorTest.Run(SupportedSource);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.Compilation.GetDiagnostics());

        var resourceProxy = result.Compilation.GetTypeByMetadataName(
            "ProvisioningGeneratorTests.Generated.SampleResourceProxy");
        Assert.NotNull(resourceProxy);
        Assert.Null(result.Compilation.GetTypeByMetadataName(
            "ProvisioningGeneratorTests.Generated.SystemDataProxy"));
        Assert.Equal(
            [
                "Child",
                "Children",
                "ComplexValue",
                "Count",
                "Enabled",
                "Endpoint",
                "Id",
                "Labels",
                "Mode",
                "Name",
                "OptionalCount",
                "Tags"
            ],
            GetExportedMembers<IPropertySymbol>(resourceProxy));
        Assert.Equal(
            [
                "AddTo",
                "AssignRole",
                "ClearComplexValue",
                "ClearName",
                "Format",
                "Reset",
                "Set",
                "Set",
                "Transform",
                "TransformValue"
            ],
            GetExportedMembers<IMethodSymbol>(resourceProxy));
        AssertDocumentationContains(
            resourceProxy,
            "<summary>",
            "Represents the SampleResource Azure Provisioning model.");
        AssertDocumentationContains(
            Assert.Single(resourceProxy.GetMembers("Name").OfType<IPropertySymbol>()),
            "Gets or sets the Name provisioning property.");
        AssertDocumentationContains(
            Assert.Single(resourceProxy.GetMembers("TransformValue").OfType<IMethodSymbol>()),
            "Invokes TransformValue on the SampleResource provisioning model.",
            "<param name=\"value\">",
            "<returns>");

        var factory = result.Compilation.GetTypeByMetadataName(
            "ProvisioningGeneratorTests.Generated.AzureResourceInfrastructureProvisioningExtensions");
        Assert.NotNull(factory);
        Assert.Equal(
            [
                "AddSampleResource",
                "CreateBuiltInRole",
                "CreateChildModel",
                "GetBuiltInRoleContributor",
                "GetSampleResource",
                "GetSampleResourceByIdentifier",
                "GetSampleResources"
            ],
            GetExportedMembers<IMethodSymbol>(factory));
        var createBuiltInRole = Assert.Single(factory.GetMembers("CreateBuiltInRole").OfType<IMethodSymbol>());
        Assert.Equal(2, createBuiltInRole.Parameters.Length);
        AssertDocumentationContains(
            Assert.Single(factory.GetMembers("AddSampleResource").OfType<IMethodSymbol>()),
            "Adds a SampleResource provisioning model.",
            "<param name=\"bicepIdentifier\">",
            "<param name=\"resourceVersion\">",
            "<param name=\"infrastructure\">");
    }

    [Fact]
    public void UnsupportedMembersAndRootsGenerateDiagnostics()
    {
        var result = ProvisioningGeneratorTest.Run(UnsupportedSource);
        var diagnostics = result.RunResult.Diagnostics
            .OrderBy(static diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.GetMessage(), StringComparer.Ordinal)
            .ToArray();

        Assert.Collection(
            diagnostics,
            diagnostic => AssertDiagnostic(diagnostic, "ASPIREAZUREPROVISIONING002", "UnsupportedProperty"),
            diagnostic => AssertDiagnostic(diagnostic, "ASPIREAZUREPROVISIONING002", "this[]"),
            diagnostic => AssertDiagnostic(diagnostic, "ASPIREAZUREPROVISIONING003", "Generic"),
            diagnostic => AssertDiagnostic(diagnostic, "ASPIREAZUREPROVISIONING003", "UnsupportedParameter"),
            diagnostic => AssertDiagnostic(diagnostic, "ASPIREAZUREPROVISIONING003", "UnsupportedRef"),
            diagnostic => AssertDiagnostic(diagnostic, "ASPIREAZUREPROVISIONING003", "UnsupportedRefReturn"),
            diagnostic => AssertDiagnostic(diagnostic, "ASPIREAZUREPROVISIONING003", "UnsupportedReturn"),
            diagnostic => AssertDiagnostic(diagnostic, "ASPIREAZUREPROVISIONING004", "Collide"),
            diagnostic => AssertDiagnostic(diagnostic, "ASPIREAZUREPROVISIONING005", "Test.Provisioning.GenericRoot<>"),
            diagnostic => AssertDiagnostic(diagnostic, "ASPIREAZUREPROVISIONING005", "Test.Provisioning.RootEnum"),
            diagnostic => AssertDiagnostic(diagnostic, "ASPIREAZUREPROVISIONING005", "Test.Provisioning.StaticRoot"));

        Assert.Empty(result.Compilation.GetDiagnostics());
    }

    [Fact]
    public void DerivedMethodCollidingWithGeneratedBaseMethodGeneratesDiagnostic()
    {
        var result = ProvisioningGeneratorTest.Run(InheritedCollisionSource);
        var diagnostic = Assert.Single(result.RunResult.Diagnostics);

        AssertDiagnostic(diagnostic, "ASPIREAZUREPROVISIONING004", "Collide");
        Assert.Empty(result.Compilation.GetDiagnostics());
    }

    [Fact]
    public void UngeneratedIntermediateBaseMembersAreEmittedOnDerivedProxy()
    {
        var result = ProvisioningGeneratorTest.Run(UngeneratedIntermediateBaseSource);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.Compilation.GetDiagnostics());

        var baseProxy = result.Compilation.GetTypeByMetadataName(
            "ProvisioningGeneratorTests.Generated.BaseModelProxy");
        Assert.NotNull(baseProxy);

        var derivedProxy = result.Compilation.GetTypeByMetadataName(
            "ProvisioningGeneratorTests.Generated.DerivedModelProxy");
        Assert.NotNull(derivedProxy);
        Assert.Equal(baseProxy, derivedProxy.BaseType);
        Assert.Null(result.Compilation.GetTypeByMetadataName(
            "ProvisioningGeneratorTests.Generated.MiddleModelProxy"));
        Assert.Equal(
            ["DerivedProperty", "MiddleProperty"],
            GetExportedMembers<IPropertySymbol>(derivedProxy));
        Assert.Equal(
            ["DerivedMethod", "MiddleMethod"],
            GetExportedMembers<IMethodSymbol>(derivedProxy));
    }

    [Fact]
    public void SharedCoreTypesAreGeneratedIntoEachProxyPackage()
    {
        var result = ProvisioningGeneratorTest.Run(
            SharedProvisioningSource,
            new TestAssemblySource("Azure.Provisioning", SharedProvisioningAssemblySource));

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.Compilation.GetDiagnostics());

        var sharedProxy = result.Compilation.GetTypeByMetadataName(
            "ProvisioningGeneratorTests.Generated.ProvisioningGeneratorTestsManagedServiceIdentityProxy");
        Assert.NotNull(sharedProxy);
        Assert.Equal(Accessibility.Internal, sharedProxy.DeclaredAccessibility);
        Assert.Equal(["IdentityType"], GetExportedMembers<IPropertySymbol>(sharedProxy));
        Assert.Null(result.Compilation.GetTypeByMetadataName(
            "ProvisioningGeneratorTests.Generated.ProvisionableProxy"));
    }

    [Fact]
    public void ProxyNamesAreDisambiguatedAfterSharedTypePrefixing()
    {
        var result = ProvisioningGeneratorTest.RunWithAssemblyName(
            "Foo",
            SharedProxyNameCollisionSource,
            new TestAssemblySource("Azure.Provisioning", SharedProvisioningAssemblySource));

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.Compilation.GetDiagnostics());

        var sharedProxy = result.Compilation.GetTypeByMetadataName(
            "Foo.Generated.FooManagedServiceIdentity_1Proxy");
        Assert.NotNull(sharedProxy);
        var serviceProxy = result.Compilation.GetTypeByMetadataName(
            "Foo.Generated.FooManagedServiceIdentity_2Proxy");
        Assert.NotNull(serviceProxy);

        var identity = Assert.Single(serviceProxy.GetMembers("Identity").OfType<IPropertySymbol>());
        Assert.Equal(sharedProxy, identity.Type);
    }

    [Fact]
    public void FactoryMethodNamesAreDisambiguatedAcrossGenerationPhases()
    {
        var result = ProvisioningGeneratorTest.Run(FactoryMethodCollisionSource);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.Compilation.GetDiagnostics());

        var factory = result.Compilation.GetTypeByMetadataName(
            "ProvisioningGeneratorTests.Generated.AzureResourceInfrastructureProvisioningExtensions");
        Assert.NotNull(factory);

        var getItems = Assert.Single(factory.GetMembers("GetItems").OfType<IMethodSymbol>());
        Assert.Equal("ItemsProxy", getItems.ReturnType.Name);

        var getItems2 = Assert.Single(factory.GetMembers("GetItems2").OfType<IMethodSymbol>());
        var itemArray = Assert.IsAssignableFrom<IArrayTypeSymbol>(getItems2.ReturnType);
        Assert.Equal("ItemProxy", itemArray.ElementType.Name);
    }

    [Fact]
    public void NullableProxyConstructorParametersGenerateCompilableFactoryMethods()
    {
        var result = ProvisioningGeneratorTest.Run(NullableProxyConstructorSource);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.Compilation.GetDiagnostics());

        var factory = result.Compilation.GetTypeByMetadataName(
            "ProvisioningGeneratorTests.Generated.AzureResourceInfrastructureProvisioningExtensions");
        Assert.NotNull(factory);

        var createArgumentContainer = Assert.Single(factory.GetMembers("CreateArgumentContainer").OfType<IMethodSymbol>());
        Assert.Equal(2, createArgumentContainer.Parameters.Length);

        var parameter = createArgumentContainer.Parameters[1];
        Assert.Equal("args", parameter.Name);
        Assert.Equal("ChildModelProxy", parameter.Type.Name);
        Assert.Equal(NullableAnnotation.Annotated, parameter.NullableAnnotation);
    }

    [Fact]
    public void ArgumentsParameterRenamingAvoidsSignatureCollisions()
    {
        var result = ProvisioningGeneratorTest.Run(ParameterNameCollisionSource);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.Compilation.GetDiagnostics());

        var factory = result.Compilation.GetTypeByMetadataName(
            "ProvisioningGeneratorTests.Generated.AzureResourceInfrastructureProvisioningExtensions");
        Assert.NotNull(factory);
        var createModel = Assert.Single(factory.GetMembers("CreateParameterNameCollisionModel").OfType<IMethodSymbol>());
        Assert.Equal(
            ["infrastructure", "args_", "args"],
            createModel.Parameters.Select(static parameter => parameter.Name));
        AssertDocumentationContains(
            createModel,
            "<paramref name=\"args_\"",
            "<paramref name=\"args\"",
            "<param name=\"args_\"",
            "<param name=\"args\"");
        var factorySyntax = Assert.IsType<MethodDeclarationSyntax>(createModel.DeclaringSyntaxReferences.Single().GetSyntax());
        var factorySemanticModel = result.Compilation.GetSemanticModel(factorySyntax.SyntaxTree);
        var objectCreation = Assert.Single(
            factorySyntax.DescendantNodes()
                .OfType<ObjectCreationExpressionSyntax>()
                .Select(syntax => factorySemanticModel.GetOperation(syntax))
                .OfType<IObjectCreationOperation>(),
            operation => operation.Constructor?.ContainingType.Name == "ParameterNameCollisionModel");
        Assert.Collection(
            objectCreation.Arguments,
            argument => Assert.Equal("args_", Assert.IsAssignableFrom<IParameterReferenceOperation>(argument.Value).Parameter.Name),
            argument => Assert.Equal("args", Assert.IsAssignableFrom<IParameterReferenceOperation>(argument.Value).Parameter.Name));

        var proxy = result.Compilation.GetTypeByMetadataName(
            "ProvisioningGeneratorTests.Generated.ParameterNameCollisionModelProxy");
        Assert.NotNull(proxy);
        var combine = Assert.Single(proxy.GetMembers("Combine").OfType<IMethodSymbol>());
        Assert.Equal(
            ["args_", "args"],
            combine.Parameters.Select(static parameter => parameter.Name));
        AssertDocumentationContains(
            combine,
            "<paramref name=\"args_\"",
            "<paramref name=\"args\"",
            "<param name=\"args_\"",
            "<param name=\"args\"");

        var methodSyntax = Assert.IsType<MethodDeclarationSyntax>(combine.DeclaringSyntaxReferences.Single().GetSyntax());
        var semanticModel = result.Compilation.GetSemanticModel(methodSyntax.SyntaxTree);
        var invocation = Assert.Single(methodSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>());
        var operation = Assert.IsAssignableFrom<IInvocationOperation>(semanticModel.GetOperation(invocation));
        Assert.Collection(
            operation.Arguments,
            argument => Assert.Equal("args_", Assert.IsAssignableFrom<IParameterReferenceOperation>(argument.Value).Parameter.Name),
            argument => Assert.Equal("args", Assert.IsAssignableFrom<IParameterReferenceOperation>(argument.Value).Parameter.Name));
    }

    [Fact]
    public void NullableProxyReturningMethodsInvokeUnderlyingMethodOnce()
    {
        var result = ProvisioningGeneratorTest.Run(NullableProxyReturningMethodSource);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.Compilation.GetDiagnostics());

        var proxy = result.Compilation.GetTypeByMetadataName(
            "ProvisioningGeneratorTests.Generated.NullableMethodOwnerProxy");
        Assert.NotNull(proxy);

        var method = Assert.Single(proxy.GetMembers("GetOptionalChild").OfType<IMethodSymbol>());
        var methodSyntax = Assert.IsType<MethodDeclarationSyntax>(method.DeclaringSyntaxReferences.Single().GetSyntax());
        var body = Assert.IsType<BlockSyntax>(methodSyntax.Body);
        Assert.Collection(
            body.Statements,
            statement => Assert.IsType<LocalDeclarationStatementSyntax>(statement),
            statement => Assert.IsType<ReturnStatementSyntax>(statement));

        var semanticModel = result.Compilation.GetSemanticModel(methodSyntax.SyntaxTree);
        var invocations = methodSyntax.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(syntax => semanticModel.GetOperation(syntax))
            .OfType<IInvocationOperation>()
            .ToArray();
        var invocation = Assert.Single(invocations);
        Assert.Equal("GetOptionalChild", invocation.TargetMethod.Name);
        Assert.Equal(
            "Test.Provisioning.NullableMethodOwner",
            invocation.TargetMethod.ContainingType.ToDisplayString());

        var returnStatement = Assert.IsType<ReturnStatementSyntax>(body.Statements[1]);
        Assert.Empty(returnStatement.DescendantNodes().OfType<InvocationExpressionSyntax>());
    }

    [Fact]
    public void GeneratedNamespaceSanitizesEachAssemblyNameSegment()
    {
        var result = ProvisioningGeneratorTest.RunWithAssemblyName(
            "3P.class..4leaf",
            NamespaceSanitizationSource);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.Compilation.GetDiagnostics());

        Assert.NotNull(result.Compilation.GetTypeByMetadataName(
            "_3P._class.__._4leaf.Generated.NamespaceModelProxy"));
    }

    [Fact]
    public void MutableStructRootsGenerateDiagnosticsWhileReadonlyStructRootsRemainSupported()
    {
        var result = ProvisioningGeneratorTest.Run(MutableStructSource);
        var diagnostic = Assert.Single(result.RunResult.Diagnostics);

        AssertDiagnostic(diagnostic, "ASPIREAZUREPROVISIONING006", "Test.Provisioning.MutableCatalog");
        Assert.Empty(result.Compilation.GetDiagnostics());
        Assert.NotNull(result.Compilation.GetTypeByMetadataName(
            "ProvisioningGeneratorTests.Generated.ImmutableCatalogProxy"));
        Assert.Null(result.Compilation.GetTypeByMetadataName(
            "ProvisioningGeneratorTests.Generated.MutableCatalogProxy"));
    }

    [Fact]
    public void SharedCoreFactoryMethodsRemainPackageQualified()
    {
        var result = ProvisioningGeneratorTest.Run(
            SharedProvisioningSource,
            new TestAssemblySource("Azure.Provisioning", SharedProvisioningAssemblySource));

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.Compilation.GetDiagnostics());

        var factory = result.Compilation.GetTypeByMetadataName(
            "ProvisioningGeneratorTests.Generated.AzureResourceInfrastructureProvisioningExtensions");
        Assert.NotNull(factory);
        Assert.Single(factory.GetMembers("CreateProvisioningGeneratorTestsManagedServiceIdentity").OfType<IMethodSymbol>());
        Assert.Empty(factory.GetMembers("CreateManagedServiceIdentity"));
    }

    [Fact]
    public void XmlDocumentationPropagatesToGeneratedProxySurface()
    {
        var result = ProvisioningGeneratorTest.Run(DocumentationPropagationSource);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.Compilation.GetDiagnostics());

        var proxy = result.Compilation.GetTypeByMetadataName(
            "ProvisioningGeneratorTests.Generated.DocumentedModelProxy");
        Assert.NotNull(proxy);

        AssertDocumentationContains(proxy, "<summary>", "Represents a documented model.");

        var nameProperty = Assert.Single(proxy.GetMembers("Name").OfType<IPropertySymbol>());
        AssertDocumentationContains(nameProperty, "<summary>", "Gets or sets the display name.");

        var formatMethod = Assert.Single(proxy.GetMembers("Format").OfType<IMethodSymbol>());
        AssertDocumentationContains(
            formatMethod,
            "<summary>",
            "Formats the supplied value.",
            "<param name=\"value\">",
            "The value to format.");

        var factory = result.Compilation.GetTypeByMetadataName(
            "ProvisioningGeneratorTests.Generated.AzureResourceInfrastructureProvisioningExtensions");
        Assert.NotNull(factory);

        var createDocumentedModel = Assert.Single(factory.GetMembers("CreateDocumentedModel").OfType<IMethodSymbol>());
        AssertDocumentationContains(
            createDocumentedModel,
            "<summary>",
            "Initializes the documented model for",
            "<paramref name=\"args\"",
            "&amp; safely.",
            "<param name=\"args\">",
            "The child model argument.",
            "<param name=\"infrastructure\">");

        var getDefaultChild = Assert.Single(factory.GetMembers("GetDocumentedModelDefaultChild").OfType<IMethodSymbol>());
        AssertDocumentationContains(getDefaultChild, "<summary>", "Gets the default child.");
    }

    private static string[] GetExportedMembers<TSymbol>(INamedTypeSymbol type)
        where TSymbol : ISymbol
    {
        return type.GetMembers()
            .OfType<TSymbol>()
            .Where(static member => member.GetAttributes().Any(static attribute =>
                attribute.AttributeClass?.ToDisplayString() == "Aspire.Hosting.AspireExportAttribute"))
            .Select(static member => member.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AssertDocumentationContains(ISymbol symbol, params string[] expectedFragments)
    {
        var documentation = symbol.GetDocumentationCommentXml();
        Assert.False(string.IsNullOrWhiteSpace(documentation));

        foreach (var expectedFragment in expectedFragments)
        {
            Assert.Contains(expectedFragment, documentation, StringComparison.Ordinal);
        }
    }

    private static void AssertDiagnostic(Diagnostic diagnostic, string id, string memberName)
    {
        Assert.Equal(id, diagnostic.Id);
        Assert.Contains($"'{memberName}'", diagnostic.GetMessage());
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    private const string SupportedSource = SupportedAttributes + CommonSource + SupportedTypes;

    private const string NullableProxyConstructorSource = NullableProxyConstructorAttributes + CommonSource + NullableProxyConstructorTypes;

    private const string ParameterNameCollisionSource = ParameterNameCollisionAttributes + CommonSource + ParameterNameCollisionTypes;

    private const string NullableProxyReturningMethodSource = NullableProxyReturningMethodAttributes + CommonSource + NullableProxyReturningMethodTypes;

    private const string NamespaceSanitizationSource = NamespaceSanitizationAttributes + CommonSource + NamespaceSanitizationTypes;

    private const string MutableStructSource = MutableStructAttributes + CommonSource + MutableStructTypes;

    private const string DocumentationPropagationSource = DocumentationPropagationAttributes + CommonSource + DocumentationPropagationTypes;

    private const string SupportedAttributes = """
        [assembly: Aspire.Hosting.Azure.Provisioning.GenerateAspireProvisioningProxy(
            typeof(Test.Provisioning.SampleResource))]
        [assembly: Aspire.Hosting.Azure.Provisioning.GenerateAspireProvisioningProxy(
            typeof(Test.Provisioning.ChildModel),
            IsInfrastructureRoot = false)]

        """;

    private const string SupportedTypes = """
        namespace Test.Provisioning
        {
            public enum SampleMode
            {
                Default,
                Advanced
            }

            public sealed class ChildModel
            {
                public string Value { get; set; } = string.Empty;
            }

            public readonly struct BuiltInRole
            {
                private readonly string _value;

                public BuiltInRole(string value)
                {
                    _value = value;
                }

                public static BuiltInRole Contributor { get; } = new("contributor");

                public override string ToString() => _value;
            }

            public sealed class SampleResource : Azure.Provisioning.Primitives.ProvisionableResource
            {
                public SampleResource(string bicepIdentifier, string? resourceVersion = null)
                    : base(bicepIdentifier)
                {
                }

                public string Id { get; set; } = string.Empty;
                public bool Enabled { get; set; }
                public int Count { get; set; }
                public int? OptionalCount { get; set; }
                public System.Uri Endpoint { get; set; } = new("https://example.com");
                public SampleMode Mode { get; set; }
                public ChildModel Child { get; set; } = new();
                public Azure.Provisioning.BicepValue<string> Name { get; set; } = new("name");
                public Azure.Provisioning.BicepValue<ChildModel> ComplexValue { get; set; } = new(new());
                public Azure.Provisioning.BicepList<string> Tags { get; set; } = new();
                public Azure.Provisioning.BicepList<ChildModel> Children { get; set; } = new();
                public Azure.Provisioning.BicepDictionary<string> Labels { get; set; } = new();
                public Azure.Provisioning.SystemData SystemData { get; } = new();

                public override Azure.Provisioning.ResourceNameRequirements GetResourceNameRequirements() => new();

                public void Reset()
                {
                }

                public void AssignRole(BuiltInRole role)
                {
                }

                public string Format(string value, bool enabled) => value;

                public ChildModel Transform(ChildModel value) => value;

                public Azure.Provisioning.BicepValue<string> TransformValue(
                    Azure.Provisioning.BicepValue<string> value) => value;

                public void Set(string value)
                {
                }

                public void Set(int value)
                {
                }
            }
        }
        """;

    private const string NullableProxyConstructorAttributes = """
        [assembly: Aspire.Hosting.Azure.Provisioning.GenerateAspireProvisioningProxy(
            typeof(Test.Provisioning.ArgumentContainer),
            IsInfrastructureRoot = false)]

        """;

    private const string NullableProxyConstructorTypes = """
        namespace Test.Provisioning
        {
            public sealed class ChildModel
            {
                public string Value { get; set; } = string.Empty;
            }

            public sealed class ArgumentContainer
            {
                public ArgumentContainer(ChildModel? arguments = null)
                {
                    Child = arguments;
                }

                public ChildModel? Child { get; set; }
            }
        }
        """;

    private const string NullableProxyReturningMethodAttributes = """
        [assembly: Aspire.Hosting.Azure.Provisioning.GenerateAspireProvisioningProxy(
            typeof(Test.Provisioning.NullableMethodOwner),
            IsInfrastructureRoot = false)]
        [assembly: Aspire.Hosting.Azure.Provisioning.GenerateAspireProvisioningProxy(
            typeof(Test.Provisioning.ChildModel),
            IsInfrastructureRoot = false)]

        """;

    private const string ParameterNameCollisionAttributes = """
        [assembly: Aspire.Hosting.Azure.Provisioning.GenerateAspireProvisioningProxy(
            typeof(Test.Provisioning.ParameterNameCollisionModel),
            IsInfrastructureRoot = false)]

        """;

    private const string ParameterNameCollisionTypes = """
        namespace Test.Provisioning
        {
            public sealed class ParameterNameCollisionModel
            {
                /// <summary>Creates a model from <paramref name="arguments"/> and <paramref name="args"/>.</summary>
                /// <param name="arguments">The first value.</param>
                /// <param name="args">The second value.</param>
                public ParameterNameCollisionModel(string arguments, string args)
                {
                }

                /// <summary>Combines <paramref name="arguments"/> and <paramref name="args"/>.</summary>
                /// <param name="arguments">The first value.</param>
                /// <param name="args">The second value.</param>
                /// <returns>The combined value.</returns>
                public string Combine(string arguments, string args) => arguments + args;
            }
        }
        """;

    private const string NullableProxyReturningMethodTypes = """
        namespace Test.Provisioning
        {
            public sealed class ChildModel
            {
                public string Value { get; set; } = string.Empty;
            }

            public sealed class NullableMethodOwner
            {
                public ChildModel? GetOptionalChild(string __underlyingValue, string __mappedValue) => new();
            }
        }
        """;

    private const string NamespaceSanitizationAttributes = """
        [assembly: Aspire.Hosting.Azure.Provisioning.GenerateAspireProvisioningProxy(
            typeof(Test.Provisioning.NamespaceModel),
            IsInfrastructureRoot = false)]

        """;

    private const string NamespaceSanitizationTypes = """
        namespace Test.Provisioning
        {
            public sealed class NamespaceModel
            {
                public string Value { get; set; } = string.Empty;
            }
        }
        """;

    private const string MutableStructAttributes = """
        [assembly: Aspire.Hosting.Azure.Provisioning.GenerateAspireProvisioningProxy(
            typeof(Test.Provisioning.ImmutableCatalog),
            IsInfrastructureRoot = false)]
        [assembly: Aspire.Hosting.Azure.Provisioning.GenerateAspireProvisioningProxy(
            typeof(Test.Provisioning.MutableCatalog),
            IsInfrastructureRoot = false)]

        """;

    private const string MutableStructTypes = """
        namespace Test.Provisioning
        {
            public readonly struct ImmutableCatalog
            {
                public ImmutableCatalog(string name)
                {
                    Name = name;
                }

                public string Name { get; }
            }

            public struct MutableCatalog
            {
                public string Name { get; set; }
            }
        }
        """;

    private const string DocumentationPropagationAttributes = """
        [assembly: Aspire.Hosting.Azure.Provisioning.GenerateAspireProvisioningProxy(
            typeof(Test.Provisioning.DocumentedModel),
            IsInfrastructureRoot = false)]

        """;

    private const string DocumentationPropagationTypes = """
        namespace Test.Provisioning
        {
            /// <summary>Represents a documented child.</summary>
            public sealed class DocumentedChild
            {
                /// <summary>Gets or sets the child value.</summary>
                public string Value { get; set; } = string.Empty;
            }

            /// <summary>Represents a documented model.</summary>
            public sealed class DocumentedModel
            {
                /// <summary>Gets the default child.</summary>
                public static DocumentedChild DefaultChild { get; } = new();

                /// <summary>Initializes the documented model for <paramref name="arguments"/> &amp; safely.</summary>
                /// <param name="arguments">The child model argument.</param>
                public DocumentedModel(DocumentedChild? arguments = null)
                {
                    Child = arguments;
                }

                /// <summary>Gets or sets the child model.</summary>
                public DocumentedChild? Child { get; set; }

                /// <summary>Gets or sets the display name.</summary>
                public string Name { get; set; } = string.Empty;

                /// <summary>Formats the supplied value.</summary>
                /// <param name="value">The value to format.</param>
                public string Format(string value) => value;
            }
        }
        """;

    private const string UnsupportedSource = UnsupportedAttributes + CommonSource + UnsupportedTypes;

    private const string InheritedCollisionSource = InheritedCollisionAttributes + CommonSource + InheritedCollisionTypes;

    private const string UngeneratedIntermediateBaseSource =
        UngeneratedIntermediateBaseAttributes + CommonSource + UngeneratedIntermediateBaseTypes;

    private const string SharedProvisioningSource = """
        [assembly: Aspire.Hosting.Azure.Provisioning.GenerateAspireProvisioningProxy(
            typeof(Test.Provisioning.ServiceResource),
            IsInfrastructureRoot = false)]

        """ + AspireRuntimeSource + """
        namespace Test.Provisioning
        {
            public sealed class ServiceResource : Azure.Provisioning.Primitives.ProvisionableResource
            {
                public ServiceResource(string bicepIdentifier)
                    : base(bicepIdentifier)
                {
                }

                public Azure.Provisioning.Resources.ManagedServiceIdentity Identity { get; set; } = new();
                public Azure.Provisioning.BicepList<Azure.Core.ResourceIdentifier> NetworkAclBypassResourceIds { get; } = new();
            }
        }
        """;

    private const string SharedProxyNameCollisionSource = """
        [assembly: Aspire.Hosting.Azure.Provisioning.GenerateAspireProvisioningProxy(
            typeof(Test.Provisioning.FooManagedServiceIdentity),
            IsInfrastructureRoot = false)]

        """ + AspireRuntimeSource + """
        namespace Test.Provisioning
        {
            public sealed class FooManagedServiceIdentity
            {
                public Azure.Provisioning.Resources.ManagedServiceIdentity Identity { get; set; } = new();
            }
        }
        """;

    private const string FactoryMethodCollisionSource = """
        [assembly: Aspire.Hosting.Azure.Provisioning.GenerateAspireProvisioningProxy(
            typeof(Test.Provisioning.Item))]
        [assembly: Aspire.Hosting.Azure.Provisioning.GenerateAspireProvisioningProxy(
            typeof(Test.Provisioning.Items))]

        """ + CommonSource + """
        namespace Test.Provisioning
        {
            public sealed class Item : Azure.Provisioning.Primitives.ProvisionableResource
            {
                public Item(string bicepIdentifier)
                    : base(bicepIdentifier)
                {
                }
            }

            public sealed class Items : Azure.Provisioning.Primitives.ProvisionableResource
            {
                public Items(string bicepIdentifier)
                    : base(bicepIdentifier)
                {
                }
            }
        }
        """;

    private const string SharedProvisioningAssemblySource = AzureProvisioningSource + "\n" + """
        namespace Azure.Provisioning.Resources
        {
            public sealed class ManagedServiceIdentity
            {
                public string IdentityType { get; set; } = string.Empty;
            }
        }
        """;

    private const string InheritedCollisionAttributes = """
        [assembly: Aspire.Hosting.Azure.Provisioning.GenerateAspireProvisioningProxy(
            typeof(Test.Provisioning.BaseModel),
            IsInfrastructureRoot = false)]
        [assembly: Aspire.Hosting.Azure.Provisioning.GenerateAspireProvisioningProxy(
            typeof(Test.Provisioning.DerivedModel),
            IsInfrastructureRoot = false)]

        """;

    private const string InheritedCollisionTypes = """
        namespace Test.Provisioning
        {
            public class BaseModel
            {
                public void Collide(Azure.Provisioning.BicepValue<string> value)
                {
                }
            }

            public sealed class DerivedModel : BaseModel
            {
                public void Collide(Azure.Provisioning.BicepValue<int> value)
                {
                }
            }
        }
        """;

    private const string UngeneratedIntermediateBaseAttributes = """
        [assembly: Aspire.Hosting.Azure.Provisioning.GenerateAspireProvisioningProxy(
            typeof(Test.Provisioning.BaseModel),
            IsInfrastructureRoot = false)]
        [assembly: Aspire.Hosting.Azure.Provisioning.GenerateAspireProvisioningProxy(
            typeof(Test.Provisioning.DerivedModel),
            IsInfrastructureRoot = false)]

        """;

    private const string UngeneratedIntermediateBaseTypes = """
        namespace Test.Provisioning
        {
            public class BaseModel
            {
                public string BaseProperty { get; set; } = string.Empty;

                public void BaseMethod(string value)
                {
                }
            }

            public class MiddleModel : BaseModel
            {
                public string MiddleProperty { get; set; } = string.Empty;

                public void MiddleMethod(string value)
                {
                }
            }

            public sealed class DerivedModel : MiddleModel
            {
                public string DerivedProperty { get; set; } = string.Empty;

                public void DerivedMethod(string value)
                {
                }
            }
        }
        """;

    private const string UnsupportedAttributes = """
        [assembly: Aspire.Hosting.Azure.Provisioning.GenerateAspireProvisioningProxy(
            typeof(Test.Provisioning.UnsupportedModel),
            IsInfrastructureRoot = false,
            ExcludedMemberNames = new[] { nameof(Test.Provisioning.UnsupportedModel.ExplicitlyExcluded) })]
        [assembly: Aspire.Hosting.Azure.Provisioning.GenerateAspireProvisioningProxy(
            typeof(Test.Provisioning.GenericRoot<>),
            IsInfrastructureRoot = false)]
        [assembly: Aspire.Hosting.Azure.Provisioning.GenerateAspireProvisioningProxy(
            typeof(Test.Provisioning.RootEnum),
            IsInfrastructureRoot = false)]
        [assembly: Aspire.Hosting.Azure.Provisioning.GenerateAspireProvisioningProxy(
            typeof(Test.Provisioning.StaticRoot),
            IsInfrastructureRoot = false)]

        """;

    private const string UnsupportedTypes = """
        namespace Test.Provisioning
        {
            public sealed class UnsupportedModel
            {
                private string _value = string.Empty;

                public System.IO.Stream UnsupportedProperty { get; set; } = System.IO.Stream.Null;
                public System.IO.Stream ExplicitlyExcluded { get; set; } = System.IO.Stream.Null;
                public string this[int index] => index.ToString();

                public System.IO.Stream UnsupportedReturn() => System.IO.Stream.Null;

                public void UnsupportedParameter(System.IO.Stream value)
                {
                }

                public void UnsupportedRef(ref string value)
                {
                }

                public ref string UnsupportedRefReturn() => ref _value;

                public T Generic<T>(T value) => value;

                public void Collide(Azure.Provisioning.BicepValue<string> value)
                {
                }

                public void Collide(Azure.Provisioning.BicepValue<int> value)
                {
                }
            }

            public sealed class GenericRoot<T>
            {
            }

            public enum RootEnum
            {
                Value
            }

            public static class StaticRoot
            {
            }
        }
        """;

    private const string CommonSource = AspireRuntimeSource + "\n" + AzureProvisioningSource;

    private const string AspireRuntimeSource = """
        #nullable enable

        namespace Aspire.Hosting
        {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class AspireExportProviderAttribute : System.Attribute
            {
            }

            [System.AttributeUsage(
                System.AttributeTargets.Class |
                System.AttributeTargets.Method |
                System.AttributeTargets.Property,
                AllowMultiple = true)]
            public sealed class AspireExportAttribute : System.Attribute
            {
                public AspireExportAttribute()
                {
                }

                public AspireExportAttribute(string id)
                {
                }

                public string? MethodName { get; set; }
            }

            [System.AttributeUsage(System.AttributeTargets.Parameter | System.AttributeTargets.Property)]
            public sealed class AspireUnionAttribute : System.Attribute
            {
                public AspireUnionAttribute(params System.Type[] types)
                {
                }
            }

            public static class AzureResourceExtensions
            {
                public static string GetBicepIdentifier(object resource) => "resource";
            }
        }

        namespace Aspire.Hosting.Azure
        {
            public sealed class AzureResourceInfrastructure
            {
                private readonly System.Collections.Generic.List<
                    global::Azure.Provisioning.Primitives.ProvisionableResource> _resources = new();

                public object AspireResource { get; } = new();

                public System.Collections.Generic.IEnumerable<
                    global::Azure.Provisioning.Primitives.ProvisionableResource> GetProvisionableResources() => _resources;

                public void Add(global::Azure.Provisioning.Primitives.ProvisionableResource resource)
                {
                    _resources.Add(resource);
                }
            }
        }

        namespace Aspire.Hosting.Azure.Provisioning
        {
            public sealed class BicepValueProxy
            {
                public static BicepValueProxy Create<T>(global::Azure.Provisioning.BicepValue<T> value) => new();

                public static global::Azure.Provisioning.BicepValue<T> Convert<T>(object value) => new(default(T)!);

                public void AssignTo<T>(global::Azure.Provisioning.BicepValue<T> value)
                {
                }
            }

            public class ProvisionableResourceProxy
            {
                public ProvisionableResourceProxy(
                    global::Azure.Provisioning.Primitives.ProvisionableResource value)
                {
                    Inner = value;
                }

                public global::Azure.Provisioning.Primitives.ProvisionableResource Inner { get; }
            }
        }
        """;

    private const string AzureProvisioningSource = """
        #nullable enable

        namespace Azure.Provisioning
        {
            public abstract class BicepValue
            {
            }

            public sealed class BicepValue<T> : BicepValue
            {
                public BicepValue(T value)
                {
                    Value = value;
                }

                public T? Value { get; }

                public void ClearValue()
                {
                }

                public static implicit operator BicepValue<T>(T value) => new(value);
            }

            public sealed class BicepList<T>
            {
                private readonly System.Collections.Generic.List<BicepValue<T>> _items = new();

                public int Count => _items.Count;

                public BicepValue<T> this[int index]
                {
                    get => _items[index];
                    set => _items[index] = value;
                }

                public void Add(BicepValue<T> value) => _items.Add(value);
                public void Insert(int index, BicepValue<T> value) => _items.Insert(index, value);
                public void RemoveAt(int index) => _items.RemoveAt(index);
                public void Clear() => _items.Clear();
            }

            public sealed class BicepDictionary<T>
            {
                private readonly System.Collections.Generic.Dictionary<string, BicepValue<T>> _items = new();

                public int Count => _items.Count;
                public System.Collections.Generic.IEnumerable<string> Keys => _items.Keys;

                public BicepValue<T> this[string key]
                {
                    get => _items[key];
                    set => _items[key] = value;
                }

                public bool Remove(string key) => _items.Remove(key);
                public void Clear() => _items.Clear();
            }

            public sealed class Infrastructure
            {
            }

            public sealed class ProvisioningPlan
            {
            }

            public sealed class ResourceNameRequirements
            {
            }

            public sealed class SystemData
            {
                public string CreatedBy { get; set; } = string.Empty;
            }
        }

        namespace Azure.Provisioning.Primitives
        {
            public abstract class Provisionable
            {
                public System.Collections.Generic.IEnumerable<Provisionable> GetProvisionableResources() => [];

                public global::Azure.Provisioning.ProvisioningPlan Build() => new();
            }

            public abstract class ProvisionableConstruct : Provisionable
            {
                public global::Azure.Provisioning.Infrastructure? ParentInfrastructure { get; }
            }

            public abstract class NamedProvisionableConstruct : ProvisionableConstruct
            {
            }

            public abstract class ProvisionableResource : NamedProvisionableConstruct
            {
                protected ProvisionableResource(string bicepIdentifier)
                {
                    BicepIdentifier = bicepIdentifier;
                }

                public string BicepIdentifier { get; }

                public virtual global::Azure.Provisioning.ResourceNameRequirements GetResourceNameRequirements() => new();
            }
        }
        """;
}
