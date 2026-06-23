// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECSHARPAPPS001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Dotnet.Tests;

public class CSharpAppPublicApiTests
{
    // ---- CSharpAppResource constructor guards --------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorCSharpAppResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;
        const string workingDirectory = "/src/app";

        var action = () => new CSharpAppResource(name, workingDirectory);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void CtorCSharpAppResourceShouldThrowWhenWorkingDirectoryIsNull()
    {
        const string name = "app";

        var action = () => new CSharpAppResource(name, workingDirectory: null!);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal("workingDirectory", exception.ParamName);
    }

    // ---- AddCSharpApp guards -------------------------------------------------

    [Fact]
    public void AddCSharpAppShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;

        var action = () => builder.AddCSharpApp("app", "app.csproj");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void AddCSharpAppShouldThrowWhenNameIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var action = () => builder.AddCSharpApp(null!, "app.csproj");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void AddCSharpAppShouldThrowWhenPathIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var action = () => builder.AddCSharpApp("app", null!);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal("path", exception.ParamName);
    }

    [Fact]
    public void AddCSharpAppWithConfigureShouldThrowWhenConfigureIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var action = () => builder.AddCSharpApp("app", "app.csproj", configure: null!);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal("configure", exception.ParamName);
    }
}
