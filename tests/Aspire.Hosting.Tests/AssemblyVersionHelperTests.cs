// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Reflection.Emit;
using Aspire.Shared;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "4")]
public class AssemblyVersionHelperTests
{
    [Theory]
    [InlineData("8.0.0-preview.1", "8.0.0-preview.1")]
    [InlineData("8.0.0-preview.1+asdlkjfdijee", "8.0.0-preview.1")]
    [InlineData("8.0.0-preview.1+asdlkjfdijee+someothersuffix", "8.0.0-preview.1")]
    [InlineData("+asdlkjfdijee", "+asdlkjfdijee")]
    [InlineData("Plain old text", "Plain old text")]
    [InlineData("", "")]
    public void GetDisplayVersionStripsCommitHash(string informationalVersion, string expectedDisplayVersion)
    {
        var assembly = CreateAssembly(CreateAttribute<AssemblyInformationalVersionAttribute>(informationalVersion));

        var actualDisplayVersion = AssemblyVersionHelper.GetDisplayVersion(assembly);

        Assert.Equal(expectedDisplayVersion, actualDisplayVersion);
    }

    [Fact]
    public void GetDisplayVersionUsesFileVersionWhenInformationalVersionIsMissing()
    {
        var assembly = CreateAssembly(
            CreateAttribute<AssemblyFileVersionAttribute>("42.42.42.42424"),
            CreateAttribute<AssemblyVersionAttribute>("8.0.0.0"));

        var actualDisplayVersion = AssemblyVersionHelper.GetDisplayVersion(assembly);

        Assert.Equal("42.42.42.42424", actualDisplayVersion);
    }

    [Fact]
    public void GetDisplayVersionUsesAssemblyVersionWhenInformationalAndFileVersionsAreMissing()
    {
        var assembly = CreateAssembly(CreateAttribute<AssemblyVersionAttribute>("8.0.0.0"));

        var actualDisplayVersion = AssemblyVersionHelper.GetDisplayVersion(assembly);

        Assert.Equal("8.0.0.0", actualDisplayVersion);
    }

    [Fact]
    public void GetDisplayVersionReturnsNullWhenVersionAttributesAreMissing()
    {
        var assembly = CreateAssembly();

        var actualDisplayVersion = AssemblyVersionHelper.GetDisplayVersion(assembly);

        Assert.Null(actualDisplayVersion);
    }

    private static AssemblyBuilder CreateAssembly(params CustomAttributeBuilder[] attributes)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"TestAssembly{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run);

        foreach (var attribute in attributes)
        {
            assembly.SetCustomAttribute(attribute);
        }

        return assembly;
    }

    private static CustomAttributeBuilder CreateAttribute<TAttribute>(string value)
        where TAttribute : Attribute
    {
        var constructor = typeof(TAttribute).GetConstructor([typeof(string)]);
        Assert.NotNull(constructor);

        return new CustomAttributeBuilder(constructor, [value]);
    }
}
