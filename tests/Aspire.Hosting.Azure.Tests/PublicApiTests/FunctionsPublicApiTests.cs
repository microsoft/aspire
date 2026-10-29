// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZUREFUNCTIONS001 // Tests exercise the experimental directory-based Functions APIs.
#pragma warning disable CS0618 // Deliberate coverage of the retained legacy Azure Functions API.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests.PublicApiTests;

public class FunctionsPublicApiTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void DirectoryBasedFunctionsApisAreExperimental()
    {
        var members = new MemberInfo[]
        {
            typeof(AzureFunctionsAppResource),
            typeof(AzureFunctionsLanguage),
            typeof(AzureFunctionsProjectResourceExtensions).GetMethod(nameof(AzureFunctionsProjectResourceExtensions.AddAzureFunctionsApp))!,
            typeof(AzureFunctionsProjectResourceExtensions).GetMethods().Single(method =>
                method.Name == nameof(AzureFunctionsProjectResourceExtensions.WithHostStorage) &&
                method.ReturnType == typeof(IResourceBuilder<AzureFunctionsAppResource>)),
            typeof(AzureFunctionsProjectResourceExtensions).GetMethods().Single(method =>
                method.Name == nameof(AzureFunctionsProjectResourceExtensions.WithReference) &&
                method.ReturnType == typeof(IResourceBuilder<AzureFunctionsAppResource>))
        };

        foreach (var member in members)
        {
            var attribute = member.GetCustomAttribute<ExperimentalAttribute>();
            Assert.NotNull(attribute);
            Assert.Equal("ASPIREAZUREFUNCTIONS001", attribute.DiagnosticId);
            Assert.Equal("https://aka.ms/aspire/diagnostics/{0}", attribute.UrlFormat);
        }
    }

    [Fact]
    public void ProjectBasedFunctionsApisAreNotExperimental()
    {
        Assert.Null(typeof(AzureFunctionsProjectResource).GetCustomAttribute<ExperimentalAttribute>());
        Assert.Null(typeof(AzureFunctionsResource).GetCustomAttribute<ExperimentalAttribute>());

        var methods = typeof(AzureFunctionsProjectResourceExtensions).GetMethods()
            .Where(method => method.ReturnType == typeof(IResourceBuilder<AzureFunctionsProjectResource>)
                || method.ReturnType == typeof(IResourceBuilder<AzureFunctionsResource>));

        Assert.NotEmpty(methods);
        Assert.All(methods, method => Assert.Null(method.GetCustomAttribute<ExperimentalAttribute>()));
    }

    [Fact]
    public void LegacyProjectFunctionsApisRetainInheritanceAndAreObsoleteWithoutErrors()
    {
        Assert.Equal(typeof(ProjectResource), typeof(AzureFunctionsProjectResource).BaseType);
        Assert.Equal(typeof(DotnetProjectResource), typeof(AzureFunctionsResource).BaseType);
        var members = typeof(AzureFunctionsProjectResourceExtensions).GetMethods()
            .Where(method => method.ReturnType == typeof(IResourceBuilder<AzureFunctionsProjectResource>))
            .Cast<MemberInfo>()
            .Append(typeof(AzureFunctionsProjectResource));

        foreach (var member in members)
        {
            var obsolete = Assert.IsType<ObsoleteAttribute>(member.GetCustomAttribute<ObsoleteAttribute>());
            Assert.False(obsolete.IsError);
            Assert.Contains("AddAzureFunctions", obsolete.Message);
        }

        var legacyPathFactory = typeof(AzureFunctionsProjectResourceExtensions).GetMethod(
            nameof(AzureFunctionsProjectResourceExtensions.AddAzureFunctionsProject),
            [typeof(IDistributedApplicationBuilder), typeof(string), typeof(string)])!;
        Assert.NotNull(legacyPathFactory.GetCustomAttribute<AspireExportAttribute>());
        Assert.Null(legacyPathFactory.GetCustomAttribute<AspireExportIgnoreAttribute>());
        var legacyHostStorage = typeof(AzureFunctionsProjectResourceExtensions).GetMethod(
            nameof(AzureFunctionsProjectResourceExtensions.WithHostStorage),
            [typeof(IResourceBuilder<AzureFunctionsProjectResource>), typeof(IResourceBuilder<AzureStorageResource>)])!;
        Assert.NotNull(legacyHostStorage.GetCustomAttribute<AspireExportAttribute>());
        Assert.Null(legacyHostStorage.GetCustomAttribute<AspireExportIgnoreAttribute>());
    }

    [Fact]
    public void FunctionsExportsUseUniqueCapabilitiesAndFriendlyTypedNames()
    {
        var exports = typeof(AzureFunctionsProjectResourceExtensions).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(method => method.GetCustomAttributes<AspireExportAttribute>().Select(export => (method, export)))
            .ToArray();
        var ids = exports.Select(item => item.export.Id ?? char.ToLowerInvariant(item.method.Name[0]) + item.method.Name[1..]).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());

        var hostStorage = Assert.Single(exports, item => item.export.Id == "withAzureFunctionsHostStorage");
        Assert.Equal("withHostStorage", hostStorage.export.MethodName);
        Assert.Equal(typeof(IResourceBuilder<AzureFunctionsResource>), hostStorage.method.ReturnType);
        var buildEnvironment = Assert.Single(exports, item => item.export.Id == "withAzureFunctionsBuildEnvironment");
        Assert.Equal("withBuildEnvironment", buildEnvironment.export.MethodName);
        Assert.Equal(typeof(IResourceBuilder<AzureFunctionsResource>), buildEnvironment.method.ReturnType);
    }

    [Theory]
    [InlineData("builder")]
    [InlineData("name")]
    [InlineData("projectPath")]
    public void AddAzureFunctionsValidatesNullArguments(string argument)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            AzureFunctionsProjectResourceExtensions.AddAzureFunctions(
                argument == "builder" ? null! : builder,
                argument == "name" ? null! : "functions",
                argument == "projectPath" ? null! : "Functions.csproj"));

        Assert.Equal(argument, exception.ParamName);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("projectPath")]
    public void AddAzureFunctionsValidatesEmptyArguments(string argument)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var exception = Assert.Throws<ArgumentException>(() =>
            builder.AddAzureFunctions(argument == "name" ? "" : "functions", argument == "projectPath" ? "" : "Functions.csproj"));

        Assert.Equal(argument, exception.ParamName);
    }

    [Fact]
    public void NewFunctionsFluentApisValidateArgumentsAndPreserveBuilderType()
    {
        IResourceBuilder<AzureFunctionsResource> missing = null!;
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var functions = builder.AddAzureFunctions("functions", path);
        var storage = builder.AddAzureStorage("storage");

        Assert.Equal("builder", Assert.Throws<ArgumentNullException>(() => missing.WithHostStorage(storage)).ParamName);
        Assert.Equal("storage", Assert.Throws<ArgumentNullException>(() => functions.WithHostStorage(null!)).ParamName);
        Assert.Equal("source", Assert.Throws<ArgumentNullException>(() =>
            AzureFunctionsProjectResourceExtensions.WithReference(functions, (IResourceBuilder<AzureBlobStorageResource>)null!)).ParamName);
        Assert.Equal("builder", Assert.Throws<ArgumentNullException>(() => missing.WithBuildEnvironment("SETTING", "value")).ParamName);
        Assert.Equal("name", Assert.Throws<ArgumentException>(() => functions.WithBuildEnvironment("", "value")).ParamName);
        Assert.Equal("value", Assert.Throws<ArgumentNullException>(() => functions.WithBuildEnvironment("SETTING", null!)).ParamName);
        Assert.Equal("callback", Assert.Throws<ArgumentNullException>(() => functions.WithBuildEnvironment((Action<EnvironmentCallbackContext>)null!)).ParamName);
        Assert.Equal("callback", Assert.Throws<ArgumentNullException>(() => functions.WithBuildEnvironment((Func<EnvironmentCallbackContext, Task>)null!)).ParamName);
        Assert.Same(functions, functions.WithReplicas(2).DisableForwardedHeaders().WithEndpointsInEnvironment(endpoint => endpoint.Name == "http").WithHostStorage(storage));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureFunctionsResourceValidatesName(bool isNull)
    {
        var name = isNull ? null! : string.Empty;

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(() => new AzureFunctionsResource(name, "."))
            : Assert.Throws<ArgumentException>(() => new AzureFunctionsResource(name, "."));

        Assert.Equal(nameof(name), exception.ParamName);
    }

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
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
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
        using var hostBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var storage = hostBuilder.AddAzureStorage("funcstorage");

        var action = () => builder.WithHostStorage(storage);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithHostStorageShouldThrowWhenStorageIsNull()
    {
        using var hostBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var builder = hostBuilder.AddAzureFunctionsProject<TestProject>("funcstorage");
        IResourceBuilder<AzureStorageResource> storage = null!;

        var action = () => builder.WithHostStorage(storage);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(storage), exception.ParamName);
    }

    [Fact]
    public void WithReferenceShouldThrowWhenSourceIsNull()
    {
        using var hostBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddAzureFunctionsAppShouldThrowWhenAppDirectoryIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var appDirectory = isNull ? null! : string.Empty;

        var action = () => builder.AddAzureFunctionsApp("funcapp", appDirectory, AzureFunctionsLanguage.TypeScript);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureFunctionsAppResourceShouldThrowWhenAppDirectoryIsNullOrEmpty(bool isNull)
    {
        var appDirectory = isNull ? null! : string.Empty;

        var action = () => new AzureFunctionsAppResource("funcapp", "func", appDirectory, AzureFunctionsLanguage.TypeScript);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(appDirectory), exception.ParamName);
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
