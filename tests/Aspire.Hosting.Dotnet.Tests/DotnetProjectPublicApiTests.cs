// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETPROJECT001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetProjectPublicApiTests
{
    // ---- DotnetProjectResource constructor guards --------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorDotnetProjectResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;
        const string workingDirectory = "/src/app";

        var action = () => new DotnetProjectResource(name, workingDirectory);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void CtorDotnetProjectResourceShouldThrowWhenWorkingDirectoryIsNull()
    {
        const string name = "app";

        var action = () => new DotnetProjectResource(name, workingDirectory: null!);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal("workingDirectory", exception.ParamName);
    }

    // ---- AddDotnetProject guards -------------------------------------------------

    [Fact]
    public void AddDotnetProjectShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;

        var action = () => builder.AddDotnetProject("app", "app.csproj");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void AddDotnetProjectShouldThrowWhenNameIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var action = () => builder.AddDotnetProject(null!, "app.csproj");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void AddDotnetProjectShouldThrowWhenPathIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var action = () => builder.AddDotnetProject("app", null!);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal("path", exception.ParamName);
    }

    [Fact]
    public void AddDotnetProjectWithConfigureShouldThrowWhenConfigureIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var action = () => builder.AddDotnetProject("app", "app.csproj", configure: null!);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal("configure", exception.ParamName);
    }
}
