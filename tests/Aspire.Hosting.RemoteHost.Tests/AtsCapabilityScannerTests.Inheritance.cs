// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Reflection.Emit;
using Aspire.TypeSystem;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public partial class AtsCapabilityScannerTests
{
    [Fact]
    public void ScanAssemblies_HiddenInternalMethods_KeepEachDeclaredExport()
    {
        var module = CreateInheritanceModule();
        var baseType = DefineMethodContext(module, "Generated.BaseRule", null, true, false,
            ("AddTo", "BaseRule.addTo", MethodAttributes.Assembly),
            ("ClearName", "BaseRule.clearName", MethodAttributes.Assembly));
        var first = DefineMethodContext(module, "Generated.FirstRule", baseType, true, false,
            ("AddTo", "FirstRule.addTo", MethodAttributes.Assembly));
        var second = DefineMethodContext(module, "Generated.SecondRule", baseType, true, false,
            ("AddTo", "SecondRule.addTo", MethodAttributes.Assembly));

        var result = AtsCapabilityScanner.ScanAssemblies([module.Assembly]);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            ["Generated/BaseRule.addTo", "Generated/BaseRule.clearName", "Generated/FirstRule.addTo", "Generated/SecondRule.addTo"],
            result.Capabilities.Select(c => c.CapabilityId).Order());
        foreach (var type in new[] { baseType, first, second })
        {
            var id = $"Generated/{type.Name}.addTo";
            var capability = Assert.Single(result.Capabilities, c => c.CapabilityId == id);
            Assert.Equal("addTo", capability.MethodName);
            Assert.Equal(AtsTypeMapping.DeriveTypeId(type), capability.TargetTypeId);
            Assert.Equal(type, result.Methods[id].DeclaringType);
            result.Methods[id].Invoke(Activator.CreateInstance(type), null);
        }
        Assert.Equal(baseType, result.Methods["Generated/BaseRule.clearName"].DeclaringType);
        var clear = Assert.Single(result.Capabilities, c => c.CapabilityId == "Generated/BaseRule.clearName");
        Assert.Equal(AtsTypeMapping.DeriveTypeId(baseType), clear.TargetTypeId);
    }

    [Fact]
    public void ScanAssemblies_UnexportedAncestor_RetainsInheritedMethod()
    {
        var module = CreateInheritanceModule();
        var baseType = DefineMethodContext(module, "Generated.UnexportedBase", null, false, false,
            ("AddTo", "base.addTo", MethodAttributes.Assembly));
        var leaf = DefineMethodContext(module, "Generated.Leaf", baseType, true, false);

        var result = AtsCapabilityScanner.ScanAssemblies([module.Assembly]);

        Assert.Empty(result.Diagnostics);
        var capability = Assert.Single(result.Capabilities);
        Assert.Equal("Generated/base.addTo", capability.CapabilityId);
        Assert.Equal(AtsTypeMapping.DeriveTypeId(leaf), capability.TargetTypeId);
        Assert.Equal(baseType, result.Methods[capability.CapabilityId].DeclaringType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScanAssemblies_IndependentDeclarationsWithSameId_StillReportDuplicate(bool inherited)
    {
        var module = CreateInheritanceModule();
        var baseType = DefineMethodContext(module, "Generated.Base", null, true, false,
            ("AddTo", "duplicate", MethodAttributes.Assembly));
        DefineMethodContext(module, "Generated.Derived", inherited ? baseType : null, true, false,
            ("AddTo", "duplicate", MethodAttributes.Assembly));

        var result = AtsCapabilityScanner.ScanAssemblies([module.Assembly]);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(AtsDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("Duplicate capability 'Generated/duplicate'", diagnostic.Message);
        Assert.Single(result.Capabilities);
    }

    [Fact]
    public void ScanAssemblies_ExposeMethods_PreservesTypeQualifiedAliases()
    {
        var module = CreateInheritanceModule();
        var baseType = DefineMethodContext(module, "Generated.Base", null, true, true,
            ("Run", null, MethodAttributes.Public));
        var derived = DefineMethodContext(module, "Generated.Derived", baseType, true, true);

        var result = AtsCapabilityScanner.ScanAssemblies([module.Assembly]);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(["Generated/Base.run", "Generated/Derived.run"], result.Capabilities.Select(c => c.CapabilityId).Order());
        Assert.Equal(baseType, result.Methods["Generated/Derived.run"].DeclaringType);
        Assert.Equal(AtsTypeMapping.DeriveTypeId(derived),
            Assert.Single(result.Capabilities, c => c.CapabilityId == "Generated/Derived.run").TargetTypeId);
    }

    [Fact]
    public void ScanAssemblies_VirtualOverride_PreservesOverrideDispatch()
    {
        var module = CreateInheritanceModule();
        var baseType = DefineMethodContext(module, "Generated.Base", null, true, false,
            ("Run", "base.run", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot));
        var derived = DefineMethodContext(module, "Generated.Derived", baseType, true, false,
            ("Run", "derived.run", MethodAttributes.Public | MethodAttributes.Virtual));

        var result = AtsCapabilityScanner.ScanAssemblies([module.Assembly]);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(["Generated/base.run", "Generated/derived.run"], result.Capabilities.Select(c => c.CapabilityId).Order());
        var method = result.Methods["Generated/derived.run"];
        Assert.Equal(derived, method.DeclaringType);
        Assert.Equal(baseType, method.GetBaseDefinition().DeclaringType);
        method.Invoke(Activator.CreateInstance(derived), null);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ScanAssemblies_InheritedMethod_RespectsNamespaceAndAssemblyScope(bool otherNamespace, bool includeBaseAssembly)
    {
        var baseModule = CreateInheritanceModule();
        var baseType = DefineMethodContext(baseModule, "Generated.Base", null, true, false,
            ("Run", "base.run", MethodAttributes.Public));
        var derivedModule = CreateInheritanceModule();
        var derivedNamespace = otherNamespace ? "Other" : "Generated";
        var derived = DefineMethodContext(derivedModule, $"{derivedNamespace}.Derived", baseType, true, false);
        Assembly[] assemblies = includeBaseAssembly
            ? [derivedModule.Assembly, baseModule.Assembly]
            : [derivedModule.Assembly];

        foreach (var scanOrder in new[] { assemblies, assemblies.Reverse().ToArray() })
        {
            var result = AtsCapabilityScanner.ScanAssemblies(scanOrder);

            Assert.Empty(result.Diagnostics);
            string[] expectedIds = otherNamespace && includeBaseAssembly
                ? ["Generated/base.run", "Other/base.run"]
                : [$"{derivedNamespace}/base.run"];
            Assert.Equal(expectedIds, result.Capabilities.Select(c => c.CapabilityId).Order());
            var capability = Assert.Single(result.Capabilities, c => c.CapabilityId == $"{derivedNamespace}/base.run");
            var owner = includeBaseAssembly && !otherNamespace ? baseType : derived;
            Assert.Equal(AtsTypeMapping.DeriveTypeId(owner), capability.TargetTypeId);
            Assert.Equal(baseType, result.Methods[capability.CapabilityId].DeclaringType);
        }
    }

    [Fact]
    public void ScanAssemblies_AssemblyLevelBaseExport_ReusesIncludedMethod()
    {
        var baseType = typeof(AssemblyExportedMethodBase);
        var derivedModule = CreateInheritanceModule();
        DefineMethodContext(derivedModule, $"{baseType.Namespace}.Derived", baseType, true, false);
        var exportModule = CreateInheritanceModule();
        ((AssemblyBuilder)exportModule.Assembly).SetCustomAttribute(new CustomAttributeBuilder(
            typeof(AspireExportAttribute).GetConstructor([typeof(Type)])!,
            [baseType],
            [typeof(AspireExportAttribute).GetProperty(nameof(AspireExportAttribute.ExposeMethods))!],
            [true]));

        var result = AtsCapabilityScanner.ScanAssemblies([derivedModule.Assembly, exportModule.Assembly]);

        Assert.Empty(result.Diagnostics);
        var capability = Assert.Single(result.Capabilities);
        Assert.Equal($"{baseType.Namespace}/base.run", capability.CapabilityId);
        Assert.Equal(AtsTypeMapping.DeriveTypeId(baseType), capability.TargetTypeId);
    }

    private static ModuleBuilder CreateInheritanceModule()
    {
        var name = $"Inheritance_{Guid.NewGuid():N}";
        return AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(name), AssemblyBuilderAccess.Run).DefineDynamicModule(name);
    }

    private static Type DefineMethodContext(
        ModuleBuilder module,
        string name,
        Type? baseType,
        bool exported,
        bool exposeMethods,
        params (string Name, string? Id, MethodAttributes Attributes)[] methods)
    {
        var builder = module.DefineType(name, TypeAttributes.Public, baseType);
        builder.DefineDefaultConstructor(MethodAttributes.Public);
        if (exported)
        {
            builder.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(AspireExportAttribute).GetConstructor(Type.EmptyTypes)!,
                [],
                [typeof(AspireExportAttribute).GetProperty(nameof(AspireExportAttribute.ExposeMethods))!],
                [exposeMethods]));
        }

        foreach (var (methodName, id, attributes) in methods)
        {
            var method = builder.DefineMethod(methodName, attributes | MethodAttributes.HideBySig, typeof(void), Type.EmptyTypes);
            if (id is not null)
            {
                method.SetCustomAttribute(new CustomAttributeBuilder(
                    typeof(AspireExportAttribute).GetConstructor([typeof(string)])!,
                    [id],
                    [typeof(AspireExportAttribute).GetProperty(nameof(AspireExportAttribute.MethodName))!],
                    [char.ToLowerInvariant(methodName[0]) + methodName[1..]]));
            }
            method.GetILGenerator().Emit(OpCodes.Ret);
        }
        return builder.CreateType()!;
    }

    public class AssemblyExportedMethodBase
    {
        [AspireExport("base.run")]
        public void Run()
        {
        }
    }
}
