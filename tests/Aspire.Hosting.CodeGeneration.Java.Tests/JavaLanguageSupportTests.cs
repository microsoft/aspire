// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.CodeGeneration.Java.Tests;

public class JavaLanguageSupportTests
{
    [Fact]
    public void RuntimeSpec_CompilesSelectedAppHostFileDirectly()
    {
        var languageSupport = new JavaLanguageSupport();

        var runtimeSpec = languageSupport.GetRuntimeSpec();

        Assert.NotNull(runtimeSpec.Execute);
        var args = string.Join(" ", runtimeSpec.Execute.Args);
        Assert.Contains("javac", args);
        Assert.Contains("\"{appHostFile}\"", args);
    }
}
