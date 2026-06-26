// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;

namespace Aspire.Cli.Tests.Shared;

public sealed class DotNetProjectProbeTests
{
    [Fact]
    public void BuildItemsAndPropertiesArguments_ProjectAppHost_UsesMsbuildDriver()
    {
        var arguments = DotNetProjectProbe.BuildItemsAndPropertiesArguments(
            "/repo/MyApp.AppHost.csproj", items: [], properties: [], targets: []);

        // A project AppHost evaluates directly through the `dotnet msbuild` driver.
        Assert.Equal(["msbuild", "/repo/MyApp.AppHost.csproj"], arguments);
    }

    [Fact]
    public void BuildItemsAndPropertiesArguments_SingleFileAppHost_UsesBuildDriver()
    {
        var arguments = DotNetProjectProbe.BuildItemsAndPropertiesArguments(
            "/repo/apphost.cs", items: [], properties: [], targets: []);

        // A single-file AppHost (any .cs) must go through `dotnet build` so the file-based app is
        // materialized into a project before evaluation.
        Assert.Equal(["build", "/repo/apphost.cs"], arguments);
    }

    [Fact]
    public void BuildItemsAndPropertiesArguments_SingleFileDetection_IsCaseInsensitive()
    {
        var arguments = DotNetProjectProbe.BuildItemsAndPropertiesArguments(
            "/repo/AppHost.CS", items: [], properties: [], targets: []);

        Assert.Equal("build", arguments[0]);
    }

    [Fact]
    public void BuildItemsAndPropertiesArguments_Properties_PrependMSBuildVersion()
    {
        var arguments = DotNetProjectProbe.BuildItemsAndPropertiesArguments(
            "/repo/MyApp.AppHost.csproj", items: [], properties: ["IsAspireHost", "RunCommand"], targets: []);

        // MSBuildVersion must be requested first so `dotnet msbuild -getProperty` returns a JSON
        // document rather than a bare value (the dotnet/msbuild#12490 workaround). Regressing this
        // ordering silently breaks JSON parsing, so assert the exact switch value.
        Assert.Equal(
            ["msbuild", "-getProperty:MSBuildVersion,IsAspireHost,RunCommand", "/repo/MyApp.AppHost.csproj"],
            arguments);
    }

    [Fact]
    public void BuildItemsAndPropertiesArguments_SingleProperty_StillPrependsMSBuildVersion()
    {
        var arguments = DotNetProjectProbe.BuildItemsAndPropertiesArguments(
            "/repo/MyApp.AppHost.csproj", items: [], properties: ["IsAspireHost"], targets: []);

        // The single-property case is exactly what triggers the bare-value bug, so MSBuildVersion is
        // still prepended to force more than one property.
        Assert.Equal(
            ["msbuild", "-getProperty:MSBuildVersion,IsAspireHost", "/repo/MyApp.AppHost.csproj"],
            arguments);
    }

    [Fact]
    public void BuildItemsAndPropertiesArguments_Items_AreJoinedWithCommas()
    {
        var arguments = DotNetProjectProbe.BuildItemsAndPropertiesArguments(
            "/repo/MyApp.AppHost.csproj", items: ["PackageReference", "PackageVersion"], properties: [], targets: []);

        Assert.Equal(
            ["msbuild", "-getItem:PackageReference,PackageVersion", "/repo/MyApp.AppHost.csproj"],
            arguments);
    }

    [Fact]
    public void BuildItemsAndPropertiesArguments_Targets_AreJoinedWithSemicolons()
    {
        var arguments = DotNetProjectProbe.BuildItemsAndPropertiesArguments(
            "/repo/MyApp.AppHost.csproj", items: [], properties: [], targets: ["Restore", "ComputeRunArguments"]);

        // Targets use ';' (the MSBuild -t separator), unlike the ',' used for properties and items.
        Assert.Equal(
            ["msbuild", "-t:Restore;ComputeRunArguments", "/repo/MyApp.AppHost.csproj"],
            arguments);
    }

    [Fact]
    public void BuildItemsAndPropertiesArguments_AllSwitches_AreOrderedPropertiesItemsTargetsThenProject()
    {
        var arguments = DotNetProjectProbe.BuildItemsAndPropertiesArguments(
            "/repo/MyApp.AppHost.csproj",
            items: ["PackageReference"],
            properties: ["IsAspireHost"],
            targets: ["ComputeRunArguments"]);

        // The probe relies on a fixed ordering: driver, -getProperty (MSBuildVersion-first),
        // -getItem, -t, then the trailing project path.
        Assert.Equal(
            [
                "msbuild",
                "-getProperty:MSBuildVersion,IsAspireHost",
                "-getItem:PackageReference",
                "-t:ComputeRunArguments",
                "/repo/MyApp.AppHost.csproj"
            ],
            arguments);
    }

    [Fact]
    public void BuildItemsAndPropertiesArguments_EmptyCollections_OmitSwitchesAndKeepProjectLast()
    {
        var arguments = DotNetProjectProbe.BuildItemsAndPropertiesArguments(
            "/repo/MyApp.AppHost.csproj", items: [], properties: [], targets: []);

        // With nothing requested, only the driver and the trailing project path are emitted.
        Assert.Equal(["msbuild", "/repo/MyApp.AppHost.csproj"], arguments);
    }
}
