// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.CodeGeneration.Go.Tests;

public class GoLanguageSupportTests
{
    private readonly GoLanguageSupport _languageSupport = new();

    [Fact]
    public void GetRuntimeSpec_UsesGoRun()
    {
        var runtimeSpec = _languageSupport.GetRuntimeSpec();

        Assert.Equal("go", runtimeSpec.Language);
        Assert.Equal("Go", runtimeSpec.DisplayName);
        Assert.Equal("Go", runtimeSpec.CodeGenLanguage);
        Assert.Equal(["apphost.go"], runtimeSpec.DetectionPatterns);
        Assert.Equal("go", runtimeSpec.Execute.Command);
        Assert.Equal(["run", "."], runtimeSpec.Execute.Args);
    }

    [Fact]
    public void GetRuntimeSpec_RequiresTheVersionedGoAppHostExtensionCapability()
    {
        var runtimeSpec = _languageSupport.GetRuntimeSpec();

        // The plain "go" capability is advertised by extension builds that only debug Go resources, so
        // gating AppHost launches on it would route apphost.go to a build that hands it to the .NET debugger.
        Assert.Equal("go-apphost.v1", runtimeSpec.ExtensionLaunchCapability);
    }
}
