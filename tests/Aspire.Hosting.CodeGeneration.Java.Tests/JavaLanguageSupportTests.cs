// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Java.Tests;

public class JavaLanguageSupportTests
{
    [Fact]
    public void GetRuntimeSpec_CompilesBeforeRunningJavaDirectly()
    {
        var spec = CreateLanguageSupport().GetRuntimeSpec();

        var preExecute = Assert.Single(spec.PreExecute!);
        Assert.Equal("javac", preExecute.Command);
        Assert.All(preExecute.Args, arg => Assert.DoesNotContain("{args}", arg));

        Assert.Equal("java", spec.Execute.Command);
        Assert.Equal(["--enable-preview", "-cp", ".java-build", "AppHost", "{args}"], spec.Execute.Args);
    }

    private static ILanguageSupport CreateLanguageSupport()
    {
        var languageSupportType = typeof(AtsJavaCodeGenerator).Assembly.GetTypes().Single(type =>
            !type.IsAbstract &&
            !type.IsInterface &&
            typeof(ILanguageSupport).IsAssignableFrom(type));

        return Assert.IsAssignableFrom<ILanguageSupport>(Activator.CreateInstance(languageSupportType, nonPublic: true));
    }
}
