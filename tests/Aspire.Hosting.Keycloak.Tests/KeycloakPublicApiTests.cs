// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Keycloak.Tests;

public class KeycloakPublicApiTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorKeycloakResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;
        ParameterResource? admin = null;
        var adminPassword = new ParameterResource("adminPassword", (p) => "password");

        var action = () => new KeycloakResource(name, admin, adminPassword);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void CtorKeycloakResourceShouldThrowWhenAdminPasswordIsNull()
    {
        const string name = "keycloak";
        ParameterResource? admin = null;
        ParameterResource adminPassword = null!;

        var action = () => new KeycloakResource(name, admin, adminPassword);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(adminPassword), exception.ParamName);
    }

    [Fact]
    public void AddKeycloakShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "keycloak";

        var action = () => builder.AddKeycloak(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddKeycloakShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create();
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddKeycloak(name);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void WithDataVolumeShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<KeycloakResource> builder = null!;

        var action = () => builder.WithDataVolume();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithDataBindMountShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<KeycloakResource> builder = null!;
        const string source = "/opt/keycloak/data";

        var action = () => builder.WithDataBindMount(source);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithDataBindMountShouldThrowWhenSourceIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create()
            .AddKeycloak("keycloak");
        var source = isNull ? null! : string.Empty;

        var action = () => builder.WithDataBindMount(source);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(source), exception.ParamName);
    }

    [Fact]
    public void WithRealmImportShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<KeycloakResource> builder = null!;
        const string importDirectory = "/opt/keycloak/data/import";

        var action = () => builder.WithRealmImport(importDirectory);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithRealmImportShouldThrowWhenImportIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create()
            .AddKeycloak("keycloak");
        var import = isNull ? null! : string.Empty;

        var action = () => builder.WithRealmImport(import);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(import), exception.ParamName);
    }

    [Fact]
    public void WithRealmImportShouldThrowWhenImportDoesNotExist()
    {
        var builder = TestDistributedApplicationBuilder.Create()
            .AddKeycloak("Keycloak");

        var action = () => builder.WithRealmImport("does-not-exist");

        Assert.Throws<InvalidOperationException>(action);
    }

    [Fact]
    public async Task WithRealmImportDirectoryAddsContainerFilesAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        using var tempDirectory = new TestTempDirectory();

        var resourceName = "keycloak";
        var keycloak = builder.AddKeycloak(resourceName);

        keycloak.WithRealmImport(tempDirectory.Path);

        using var app = builder.Build();
        var keycloakResource = builder.Resources.Single(r => r.Name.Equals(resourceName, StringComparison.Ordinal));

        var containerAnnotation = keycloak.Resource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>().Single();

        var entries = await containerAnnotation.Callback(new() { Model = keycloakResource, ServiceProvider = app.Services }, CancellationToken.None);

        Assert.Equal("/opt/keycloak/data/import", containerAnnotation.DestinationPath);
    }

    [Fact]
    public async Task WithRealmImportFileAddsContainerFilesAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        using var tempDirectory = new TestTempDirectory();

        var file = "realm.json";
        var filePath = Path.Combine(tempDirectory.Path, file);
        File.WriteAllText(filePath, string.Empty);

        var resourceName = "keycloak";
        var keycloak = builder.AddKeycloak(resourceName);

        keycloak.WithRealmImport(filePath);

        using var app = builder.Build();
        var keycloakResource = builder.Resources.Single(r => r.Name.Equals(resourceName, StringComparison.Ordinal));

        var containerAnnotation = keycloak.Resource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>().Single();

        var entries = await containerAnnotation.Callback(new() { Model = keycloakResource, ServiceProvider = app.Services }, CancellationToken.None);

        Assert.Equal("/opt/keycloak/data/import", containerAnnotation.DestinationPath);
        var realmFile = Assert.IsType<ContainerFile>(Assert.Single(entries));
        Assert.Equal(file, realmFile.Name);
        Assert.Equal(filePath, realmFile.SourcePath);
    }
}
