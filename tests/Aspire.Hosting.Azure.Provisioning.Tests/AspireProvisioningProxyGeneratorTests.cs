// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.CodeAnalysis;
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
    public void CoreProvisioningTypesAreGeneratedIntoEachProxyPackage()
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

    private static void AssertDiagnostic(Diagnostic diagnostic, string id, string memberName)
    {
        Assert.Equal(id, diagnostic.Id);
        Assert.Contains($"'{memberName}'", diagnostic.GetMessage());
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    private const string SupportedSource = SupportedAttributes + CommonSource + SupportedTypes;

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

    private const string UnsupportedSource = UnsupportedAttributes + CommonSource + UnsupportedTypes;

    private const string InheritedCollisionSource = InheritedCollisionAttributes + CommonSource + InheritedCollisionTypes;

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
