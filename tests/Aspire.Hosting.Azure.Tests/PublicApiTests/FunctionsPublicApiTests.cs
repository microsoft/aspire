// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests.PublicApiTests;

public class FunctionsPublicApiTests
{
    [Fact]
    public void AddAzureFunctionsProjectShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "funcstorage";

        var action = () => builder.AddAzureFunctionsProject<TestProject>(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddAzureFunctionsProjectShouldThrowWhenBuilderIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddAzureFunctionsProject<TestProject>(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void WithHostStorageShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureFunctionsProjectResource> builder = null!;
        using var hostBuilder = TestDistributedApplicationBuilder.Create();
        var storage = hostBuilder.AddAzureStorage("funcstorage");

        var action = () => builder.WithHostStorage(storage);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithHostStorageShouldThrowWhenStorageIsNull()
    {
        using var hostBuilder = TestDistributedApplicationBuilder.Create();
        var builder = hostBuilder.AddAzureFunctionsProject<TestProject>("funcstorage");
        IResourceBuilder<AzureStorageResource> storage = null!;

        var action = () => builder.WithHostStorage(storage);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(storage), exception.ParamName);
    }

    [Fact]
    public void WithReferenceShouldThrowWhenSourceIsNull()
    {
        using var hostBuilder = TestDistributedApplicationBuilder.Create();
        var destination = hostBuilder.AddAzureFunctionsProject<TestProject>("funcstorage");
        IResourceBuilder<IResourceWithConnectionString> source = null!;

        var action = () =>
        {
            destination.WithReference(source);
        };

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(source), exception.ParamName);
    }

    [Fact]
    public void AddAzureFunctionsAppShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;

        var action = () => builder.AddAzureFunctionsApp("funcapp", "functions", AzureFunctionsLanguage.TypeScript);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddAzureFunctionsAppShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddAzureFunctionsApp(name, "functions", AzureFunctionsLanguage.TypeScript);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void AddAzureFunctionsAppShouldThrowWhenAppDirectoryIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        string appDirectory = null!;

        var action = () => builder.AddAzureFunctionsApp("funcapp", appDirectory, AzureFunctionsLanguage.TypeScript);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(appDirectory), exception.ParamName);
    }

    [Fact]
    public void AddAzureFunctionsAppShouldThrowWhenLanguageIsInvalid()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var action = () => builder.AddAzureFunctionsApp("funcapp", "functions", (AzureFunctionsLanguage)42);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(action);
        Assert.Equal("language", exception.ParamName);
    }

    [Fact]
    public void WithHostStorageForAppShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureFunctionsAppResource> builder = null!;
        using var hostBuilder = TestDistributedApplicationBuilder.Create();
        var storage = hostBuilder.AddAzureStorage("funcstorage");

        var action = () => builder.WithHostStorage(storage);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithHostStorageForAppShouldThrowWhenStorageIsNull()
    {
        using var hostBuilder = TestDistributedApplicationBuilder.Create();
        var builder = hostBuilder.AddAzureFunctionsApp("funcstorage", "functions", AzureFunctionsLanguage.TypeScript);
        IResourceBuilder<AzureStorageResource> storage = null!;

        var action = () => builder.WithHostStorage(storage);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(storage), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureFunctionsProjectResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;

        var action = () => new AzureFunctionsProjectResource(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureFunctionsAppResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;

        var action = () => new AzureFunctionsAppResource(name, "func", "functions", AzureFunctionsLanguage.TypeScript);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void CtorAzureFunctionsAppResourceShouldThrowWhenCommandIsEmpty()
    {
        var action = () => new AzureFunctionsAppResource("funcapp", string.Empty, "functions", AzureFunctionsLanguage.TypeScript);

        var exception = Assert.Throws<ArgumentException>(action);
        Assert.Equal("command", exception.ParamName);
    }

    [Fact]
    public void CtorAzureFunctionsAppResourceShouldThrowWhenAppDirectoryIsNull()
    {
        var action = () => new AzureFunctionsAppResource("funcapp", "func", null!, AzureFunctionsLanguage.TypeScript);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal("workingDirectory", exception.ParamName);
    }

    [Fact]
    public void CtorAzureFunctionsAppResourceShouldThrowWhenLanguageIsInvalid()
    {
        var action = () => new AzureFunctionsAppResource("funcapp", "func", "functions", (AzureFunctionsLanguage)42);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(action);
        Assert.Equal("language", exception.ParamName);
    }

    private sealed class TestProject : IProjectMetadata
    {
        public string ProjectPath => "some-path";

        public LaunchSettings LaunchSettings => new()
        {
            Profiles = new Dictionary<string, LaunchProfile>
            {
                ["funcapp"] = new()
                {
                    CommandLineArgs = "--port 7071",
                    LaunchBrowser = false,
                }
            }
        };
    }
}
