// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Versioning;
using Aspire.Hosting.Dashboard;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "4")]
public class AppHostDiagnosticInfoTests
{
    [Fact]
    public void Format_AllClausesPresent_IncludesEachClause()
    {
        var result = AppHostDiagnosticInfo.Format("C#", "MyApp.AppHost.csproj", "13.5.0", "9.0.0", "net10.0");

        Assert.Equal("C# (`MyApp.AppHost.csproj`) using Aspire.AppHost.Sdk 13.5.0 and Aspire.Hosting.AppHost 9.0.0 targeting `net10.0`", result);
    }

    [Fact]
    public void Format_NoSdkVersion_UsesPackageVersionWithUsingConnector()
    {
        var result = AppHostDiagnosticInfo.Format("C#", "MyApp.AppHost.csproj", sdkVersion: null, "9.0.0", "net10.0");

        Assert.Equal("C# (`MyApp.AppHost.csproj`) using Aspire.Hosting.AppHost 9.0.0 targeting `net10.0`", result);
    }

    [Fact]
    public void Format_NoPackageVersion_OmitsPackageClause()
    {
        var result = AppHostDiagnosticInfo.Format("C#", "MyApp.AppHost.csproj", "13.5.0", packageVersion: null, "net10.0");

        Assert.Equal("C# (`MyApp.AppHost.csproj`) using Aspire.AppHost.Sdk 13.5.0 targeting `net10.0`", result);
    }

    [Fact]
    public void Format_NoTargetFramework_OmitsTargetingClause()
    {
        var result = AppHostDiagnosticInfo.Format("C#", "MyApp.AppHost.csproj", "13.5.0", "9.0.0", targetFramework: null);

        Assert.Equal("C# (`MyApp.AppHost.csproj`) using Aspire.AppHost.Sdk 13.5.0 and Aspire.Hosting.AppHost 9.0.0", result);
    }

    [Fact]
    public void Format_OnlyFileName_WhenAllOptionalValuesAbsent()
    {
        var result = AppHostDiagnosticInfo.Format("C#", "apphost.cs", sdkVersion: "   ", packageVersion: "", targetFramework: null);

        Assert.Equal("C# (`apphost.cs`)", result);
    }

    [Fact]
    public void Format_TypeScript_UsesLanguageLabelAndPackageVersionOnly()
    {
        var result = AppHostDiagnosticInfo.Format("TypeScript", "apphost.ts", sdkVersion: null, "9.0.0", targetFramework: null);

        Assert.Equal("TypeScript (`apphost.ts`) using Aspire.Hosting.AppHost 9.0.0", result);
    }

    [Theory]
    [InlineData(".NETCoreApp,Version=v10.0", "net10.0")]
    [InlineData(".NETCoreApp,Version=v8.0", "net8.0")]
    [InlineData(".NETCoreApp,Version=v5.0", "net5.0")]
    public void ConvertToTargetFrameworkMoniker_NetCoreApp5OrLater_ReturnsShortMoniker(string frameworkName, string expected)
    {
        Assert.Equal(expected, AppHostDiagnosticInfo.ConvertToTargetFrameworkMoniker(frameworkName));
    }

    [Theory]
    [InlineData(".NETCoreApp,Version=v3.1")]
    [InlineData(".NETFramework,Version=v4.8")]
    [InlineData("not a framework name")]
    [InlineData("")]
    [InlineData(null)]
    public void ConvertToTargetFrameworkMoniker_UnsupportedOrUnknown_ReturnsNull(string? frameworkName)
    {
        Assert.Null(AppHostDiagnosticInfo.ConvertToTargetFrameworkMoniker(frameworkName));
    }

    [Fact]
    public void Describe_CSharpProjectAppHost_IncludesSdkPackageAndTargetFramework()
    {
        var appHostAssembly = CreateAssembly(
            CreateAttribute<AssemblyMetadataAttribute>("Aspire.AppHost.Sdk.Version", "13.5.0"),
            CreateAttribute<TargetFrameworkAttribute>(".NETCoreApp,Version=v10.0"));
        var hostingAssembly = CreateAssembly(CreateAttribute<AssemblyInformationalVersionAttribute>("9.0.0+abc123"));

        var result = AppHostDiagnosticInfo.Describe(
            Path.Combine("C:\\", "apps", "MyApp", "MyApp.AppHost.csproj"),
            appHostAssembly,
            hostingAssembly);

        Assert.Equal("C# (`MyApp.AppHost.csproj`) using Aspire.AppHost.Sdk 13.5.0 and Aspire.Hosting.AppHost 9.0.0 targeting `net10.0`", result);
    }

    [Fact]
    public void Describe_SingleFileAppHost_OmitsMissingSdkVersion()
    {
        var appHostAssembly = CreateAssembly(
            CreateAttribute<TargetFrameworkAttribute>(".NETCoreApp,Version=v10.0"));
        var hostingAssembly = CreateAssembly(CreateAttribute<AssemblyInformationalVersionAttribute>("9.0.0"));

        var result = AppHostDiagnosticInfo.Describe("apphost.cs", appHostAssembly, hostingAssembly);

        Assert.Equal("C# (`apphost.cs`) using Aspire.Hosting.AppHost 9.0.0 targeting `net10.0`", result);
    }

    [Fact]
    public void Describe_UsesFileNameOnly_AndDoesNotLeakAbsoluteAppHostDirectory()
    {
        var appHostAssembly = CreateAssembly(
            CreateAttribute<TargetFrameworkAttribute>(".NETCoreApp,Version=v10.0"));
        var hostingAssembly = CreateAssembly(CreateAttribute<AssemblyInformationalVersionAttribute>("9.0.0"));
        var directory = Path.Combine("C:\\", "secret-user", "apps");

        var result = AppHostDiagnosticInfo.Describe(Path.Combine(directory, "MyApp.AppHost.csproj"), appHostAssembly, hostingAssembly);

        // Asserting the exact string proves the description contains only the file name and never the
        // absolute directory, which would leak the local user/home path into a public GitHub issue.
        Assert.Equal("C# (`MyApp.AppHost.csproj`) using Aspire.Hosting.AppHost 9.0.0 targeting `net10.0`", result);
    }

    [Fact]
    public void Describe_TypeScriptAppHost_ReportsLanguageAndAspireVersionOnly()
    {
        // Even though the loaded assembly carries SDK metadata and a target framework, a TypeScript
        // AppHost must report neither: the SDK doesn't apply and the target framework belongs to the
        // .NET RemoteHost backend, not the AppHost.
        var appHostAssembly = CreateAssembly(
            CreateAttribute<AssemblyMetadataAttribute>("Aspire.AppHost.Sdk.Version", "13.5.0"),
            CreateAttribute<TargetFrameworkAttribute>(".NETCoreApp,Version=v10.0"));
        var hostingAssembly = CreateAssembly(CreateAttribute<AssemblyInformationalVersionAttribute>("9.0.0"));

        var result = AppHostDiagnosticInfo.Describe(
            Path.Combine("C:\\", "apps", "MyApp", "apphost.ts"),
            appHostAssembly,
            hostingAssembly);

        Assert.Equal("TypeScript (`apphost.ts`) using Aspire.Hosting.AppHost 9.0.0", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("apphost.py")]
    [InlineData("package.json")]
    public void Describe_UnknownOrMissingPath_ReturnsNull(string? appHostFilePath)
    {
        var hostingAssembly = CreateAssembly(CreateAttribute<AssemblyInformationalVersionAttribute>("9.0.0"));

        var result = AppHostDiagnosticInfo.Describe(appHostFilePath, appHostAssembly: null, hostingAssembly);

        Assert.Null(result);
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

    private static CustomAttributeBuilder CreateAttribute<TAttribute>(params string[] values)
        where TAttribute : Attribute
    {
        var constructor = typeof(TAttribute).GetConstructor([.. Enumerable.Repeat(typeof(string), values.Length)]);
        Assert.NotNull(constructor);

        return new CustomAttributeBuilder(constructor, values);
    }
}
