// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.CodeGeneration.Java.Tests;

public class JavaLanguageSupportTests
{
    [Fact]
    public void RuntimeSpec_UsesSelectedAppHostFileAsInput()
    {
        var languageSupport = new JavaLanguageSupport();

        var runtimeSpec = languageSupport.GetRuntimeSpec();

        Assert.NotNull(runtimeSpec.Execute);
        Assert.Contains("{appHostFile}", string.Join(" ", runtimeSpec.Execute.Args));
        Assert.Contains("AppHost.java", string.Join(" ", runtimeSpec.Execute.Args));
    }
}
