// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Reflection.Emit;
using Aspire.Hosting.ApplicationModel;
using Aspire.TypeSystem;
using Xunit;
using ThirdPartyExport = Aspire.Hosting.RemoteHost.Tests.ThirdParty.AspireExportAttribute;
using ThirdPartyExportIgnore = Aspire.Hosting.RemoteHost.Tests.ThirdParty.AspireExportIgnoreAttribute;
using ThirdPartyDtoAttr = Aspire.Hosting.RemoteHost.Tests.ThirdParty.AspireDtoAttribute;
using ThirdPartyUnion = Aspire.Hosting.RemoteHost.Tests.ThirdParty.AspireUnionAttribute;

namespace Aspire.Hosting.RemoteHost.Tests;

public class AttributeDataReaderTests
{
    private static readonly ConstructorInfo s_attributeConstructor = typeof(Attribute).GetConstructor(
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        types: Type.EmptyTypes,
        modifiers: null)!;

    #region Full Name Discovery Tests

    [Fact]
    public void GetAspireExportData_FindsOfficialAttribute_OnMethod()
    {
        var method = typeof(OfficialAttributeExports).GetMethod(nameof(OfficialAttributeExports.OfficialExportMethod))!;
        var result = AttributeDataReader.GetAspireExportData(method);

        Assert.NotNull(result);
        Assert.Equal("officialMethod", result.Id);
        Assert.Equal("Official method description", result.Description);
    }

    [Fact]
    public void GetAspireExportData_IgnoresAttribute_WithDifferentNamespace_OnMethod()
    {
        var method = typeof(ThirdPartyExports).GetMethod(nameof(ThirdPartyExports.ThirdPartyMethod))!;
        var result = AttributeDataReader.GetAspireExportData(method);

        Assert.Null(result);
    }

    [Fact]
    public void GetAspireExportData_IgnoresAttribute_WithDifferentNamespace_OnType()
    {
        var result = AttributeDataReader.GetAspireExportData(typeof(ThirdPartyResource));

        Assert.Null(result);
    }

    [Fact]
    public void HasAspireExportIgnoreData_FindsOfficialAttribute()
    {
        var property = typeof(OfficialResource).GetProperty(nameof(OfficialResource.InternalProp))!;
        var result = AttributeDataReader.HasAspireExportIgnoreData(property);

        Assert.True(result);
    }

    [Fact]
    public void HasAspireExportIgnoreData_IgnoresAttribute_WithDifferentNamespace()
    {
        var property = typeof(ThirdPartyResource).GetProperty(nameof(ThirdPartyResource.InternalProp))!;
        var result = AttributeDataReader.HasAspireExportIgnoreData(property);

        Assert.False(result);
    }

    [Fact]
    public void HasAspireExportIgnoreData_FindsOfficialAttribute_OnType()
    {
        var result = AttributeDataReader.HasAspireExportIgnoreData(typeof(OfficialIgnoredType));

        Assert.True(result);
    }

    [Fact]
    public void HasAspireDtoData_FindsOfficialAttribute()
    {
        var result = AttributeDataReader.HasAspireDtoData(typeof(OfficialDtoType));

        Assert.True(result);
    }

    [Fact]
    public void HasAspireDtoData_IgnoresAttribute_WithDifferentNamespace()
    {
        var result = AttributeDataReader.HasAspireDtoData(typeof(ThirdPartyDtoType));

        Assert.False(result);
    }

    [Fact]
    public void GetAspireUnionData_FindsOfficialAttribute_OnParameter()
    {
        var method = typeof(OfficialAttributeExports).GetMethod(nameof(OfficialAttributeExports.OfficialUnionMethod))!;
        var param = method.GetParameters()[0];
        var result = AttributeDataReader.GetAspireUnionData(param);

        Assert.NotNull(result);
        Assert.Equal(2, result.Types.Length);
        Assert.Contains(typeof(string), result.Types);
        Assert.Contains(typeof(int), result.Types);
    }

    [Fact]
    public void GetAspireUnionData_IgnoresAttribute_WithDifferentNamespace_OnParameter()
    {
        var method = typeof(ThirdPartyExports).GetMethod(nameof(ThirdPartyExports.ThirdPartyMethod))!;
        var param = method.GetParameters()[0];
        var result = AttributeDataReader.GetAspireUnionData(param);

        Assert.Null(result);
    }

    [Fact]
    public void GetObsoleteData_FindsOfficialAttribute_OnMethod()
    {
        var method = typeof(OfficialAttributeExports).GetMethod(nameof(OfficialAttributeExports.ObsoleteExportMethod))!;
        var result = AttributeDataReader.GetObsoleteData(method);

        Assert.NotNull(result);
        Assert.Equal("Official obsolete method", result.Message);
        Assert.False(result.IsError);
    }

    [Fact]
    public void GetAspireExportData_ReadsAllNamedProperties()
    {
        var method = typeof(OfficialAttributeExports).GetMethod(nameof(OfficialAttributeExports.OverriddenNameMethod))!;
        var result = AttributeDataReader.GetAspireExportData(method);

        Assert.NotNull(result);
        Assert.Equal("overriddenMethod", result.Id);
        Assert.Equal("customName", result.MethodName);
    }

    [Fact]
    public void GetAspireExportData_ExposeMethodsFlag()
    {
        var result = AttributeDataReader.GetAspireExportData(typeof(OfficialMethodsResource));

        Assert.NotNull(result);
        Assert.True(result.ExposeMethods);
        Assert.False(result.ExposeProperties);
    }

    [Fact]
    public void GetAspireExportData_MatchesByConstructorSignature_WhenNamespaceMatches()
    {
        var method = CreateCompatibleExportMethod();
        var result = AttributeDataReader.GetAspireExportData(method);

        Assert.NotNull(result);
        Assert.Equal("compatibleMethod", result.Id);
        Assert.Equal("Compatible method", result.Description);
    }

    [Fact]
    public void ScanAssembly_FindsCompatibleAttribute_WhenNamespaceMatches()
    {
        var compatibleAssembly = CreateCompatibleAssembly();
        var hostingAssembly = typeof(DistributedApplication).Assembly;

        var result = AtsCapabilityScanner.ScanAssemblies([hostingAssembly, compatibleAssembly]);

        var compatibleCapability = result.Capabilities
            .FirstOrDefault(c => c.CapabilityId.EndsWith("/compatibleMethod", StringComparison.Ordinal));

        Assert.NotNull(compatibleCapability);
        Assert.Equal("Compatible method", compatibleCapability.Description);
    }

    [Fact]
    public void ScanAssembly_IgnoresAttributes_WithDifferentNamespace()
    {
        var testAssembly = typeof(AttributeDataReaderTests).Assembly;
        var hostingAssembly = typeof(DistributedApplication).Assembly;

        var result = AtsCapabilityScanner.ScanAssemblies([hostingAssembly, testAssembly]);

        var officialCapability = result.Capabilities
            .FirstOrDefault(c => c.CapabilityId.EndsWith("/officialMethod", StringComparison.Ordinal));
        var thirdPartyCapability = result.Capabilities
            .FirstOrDefault(c => c.CapabilityId.EndsWith("/thirdPartyMethod", StringComparison.Ordinal));

        Assert.NotNull(officialCapability);
        Assert.Null(thirdPartyCapability);
    }

    #endregion

    #region Test Types Using Official Aspire.Hosting Attributes

    public static class OfficialAttributeExports
    {
        [AspireExport("officialMethod", Description = "Official method description")]
        public static void OfficialExportMethod(IResource resource)
        {
            _ = resource;
        }

        [AspireExport("overriddenMethod", MethodName = "customName")]
        public static void OverriddenNameMethod(IResource resource)
        {
            _ = resource;
        }

        [AspireExport]
        public static void OfficialUnionMethod([AspireUnion(typeof(string), typeof(int))] object value)
        {
            _ = value;
        }

        [Obsolete("Official obsolete method")]
        [AspireExport("obsoleteMethod")]
        public static void ObsoleteExportMethod(IResource resource)
        {
            _ = resource;
        }
    }

    [AspireExport(ExposeProperties = true)]
    public class OfficialResource : Resource
    {
        public OfficialResource(string name) : base(name) { }

        public string Visible { get; set; } = "";

        [AspireExportIgnore]
        public string InternalProp { get; set; } = "";
    }

    [AspireExport(ExposeMethods = true)]
    public class OfficialMethodsResource : Resource
    {
        public OfficialMethodsResource(string name) : base(name) { }

        public void DoSomething() { }
    }

    [AspireExportIgnore]
    public class OfficialIgnoredType
    {
    }

    [AspireDto]
    public class OfficialDtoType
    {
        public string Name { get; set; } = "";
    }

    #endregion

    #region Test Types Using Third-Party Mock Attributes (Different Namespace)

    public static class ThirdPartyExports
    {
        [ThirdPartyExport("thirdPartyMethod", Description = "Third party method")]
        public static void ThirdPartyMethod(
            [ThirdPartyUnion(typeof(string), typeof(int))] object value)
        {
            _ = value;
        }
    }

    [ThirdPartyExport(ExposeProperties = true)]
    public class ThirdPartyResource : Resource
    {
        public ThirdPartyResource(string name) : base(name) { }

        public string Visible { get; set; } = "";

        [ThirdPartyExportIgnore]
        public string InternalProp { get; set; } = "";
    }

    [ThirdPartyDtoAttr]
    public class ThirdPartyDtoType
    {
        public string Name { get; set; } = "";
    }

    #endregion

    private static Assembly CreateCompatibleAssembly()
    {
        var assemblyName = new AssemblyName($"CompatibleAtsAttributes_{Guid.NewGuid():N}");
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
        var moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName.Name!);
        var exportAttributeType = DefineCompatibleExportAttribute(moduleBuilder);

        var exportsTypeBuilder = moduleBuilder.DefineType(
            "Generated.CompatibleExports",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var methodBuilder = exportsTypeBuilder.DefineMethod(
            "CompatibleMethod",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            [typeof(IResource)]);
        methodBuilder.DefineParameter(1, ParameterAttributes.None, "resource");
        methodBuilder.SetCustomAttribute(CreateCompatibleExportAttributeBuilder(exportAttributeType));
        methodBuilder.GetILGenerator().Emit(OpCodes.Ret);

        _ = exportsTypeBuilder.CreateType();

        return assemblyBuilder;
    }

    private static MethodInfo CreateCompatibleExportMethod()
    {
        var compatibleAssembly = CreateCompatibleAssembly();

        return compatibleAssembly.GetType("Generated.CompatibleExports")!
            .GetMethod("CompatibleMethod")!;
    }

    private static Type DefineCompatibleExportAttribute(ModuleBuilder moduleBuilder)
    {
        var typeBuilder = moduleBuilder.DefineType(
            "Aspire.Hosting.AspireExportAttribute",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
            typeof(Attribute));

        _ = typeBuilder.DefineField(nameof(AspireExportAttribute.Description), typeof(string), FieldAttributes.Public);

        var constructorBuilder = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [typeof(string)]);
        constructorBuilder.DefineParameter(1, ParameterAttributes.None, "name");

        var il = constructorBuilder.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, s_attributeConstructor);
        il.Emit(OpCodes.Ret);

        return typeBuilder.CreateType();
    }

    private static CustomAttributeBuilder CreateCompatibleExportAttributeBuilder(Type exportAttributeType)
    {
        return new CustomAttributeBuilder(
            exportAttributeType.GetConstructor([typeof(string)])!,
            ["compatibleMethod"],
            [exportAttributeType.GetField(nameof(AspireExportAttribute.Description))!],
            ["Compatible method"]);
    }
}
