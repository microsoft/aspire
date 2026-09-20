// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001

namespace Aspire.Hosting.Java.Tests;

public class JavaPublicApiTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorJavaAppResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;
        const string workingDirectory = "/src/java-app";

        var action = () => new JavaAppResource(name, workingDirectory);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }
}
