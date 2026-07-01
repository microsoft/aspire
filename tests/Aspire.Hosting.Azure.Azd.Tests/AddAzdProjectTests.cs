// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.AppContainers;
using Aspire.Hosting.JavaScript;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Azure.Azd.Tests;

public class AddAzdProjectTests
{
    [Fact]
    public void ImportsServicesWithExpectedResourceKinds()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create();

        var import = builder.AddAzdProject(sample.AzureYamlPath);

        Assert.Equal(["api", "legacy", "web"], import.Services.Keys.OrderBy(k => k));

        // A .NET project becomes a project resource; the Docker-based and fallback services become containers.
        Assert.IsType<ProjectResource>(import.Services["web"].Resource);
        Assert.IsType<ContainerResource>(import.Services["api"].Resource);
        Assert.IsType<ContainerResource>(import.Services["legacy"].Resource);
    }

    [Fact]
    public void MapsResourcesToAzureIntegrations()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create();

        var import = builder.AddAzdProject(sample.AzureYamlPath);

        // db2 (db.mysql) has no built-in mapper, so it is not imported.
        Assert.Equal(["cache", "files", "openai", "orders", "pg", "sb", "search", "secrets"], import.Resources.Keys.OrderBy(k => k, StringComparer.Ordinal));

        Assert.IsType<AzureManagedRedisResource>(import.Resources["cache"].Resource);
        Assert.IsType<AzureKeyVaultResource>(import.Resources["secrets"].Resource);
        Assert.IsType<AzurePostgresFlexibleServerResource>(import.Resources["pg"].Resource);
        Assert.IsType<AzureCosmosDBResource>(import.Resources["orders"].Resource);
        Assert.IsType<AzureServiceBusResource>(import.Resources["sb"].Resource);
        Assert.IsType<AzureStorageResource>(import.Resources["files"].Resource);
        Assert.IsType<AzureSearchResource>(import.Resources["search"].Resource);
        Assert.IsType<AzureOpenAIResource>(import.Resources["openai"].Resource);
    }

    [Fact]
    public void ExpandsNestedChildResources()
    {
        using var sample = SampleAzdProject.Create();
        // Use publish mode: it reflects the Azure resource graph that gets deployed. In run mode the
        // importer swaps Redis/Postgres for local containers (which removes the Azure child resources),
        // so the canonical Azure shape is only observable when publishing.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzdProject(sample.AzureYamlPath);

        // Generic db.postgres provisions a single database named after the resource.
        Assert.Single(builder.Resources.OfType<AzurePostgresFlexibleServerDatabaseResource>());
        Assert.Single(builder.Resources.OfType<AzureServiceBusQueueResource>());
        Assert.Single(builder.Resources.OfType<AzureServiceBusTopicResource>());
        Assert.Single(builder.Resources.OfType<AzureBlobStorageContainerResource>());
        Assert.Single(builder.Resources.OfType<AzureOpenAIDeploymentResource>());
        // Cosmos containers live under an implicit database named after the resource.
        Assert.Single(builder.Resources.OfType<AzureCosmosDBDatabaseResource>());
        Assert.Single(builder.Resources.OfType<AzureCosmosDBContainerResource>());
    }

    [Fact]
    public void CreatesComputeEnvironmentsForHostedServices()
    {
        using var sample = SampleAzdProject.Create();
        // Azure compute environments are only materialized into the model in publish mode.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzdProject(sample.AzureYamlPath);

        Assert.Single(builder.Resources.OfType<AzureContainerAppEnvironmentResource>());
        Assert.Single(builder.Resources.OfType<AzureAppServiceEnvironmentResource>());
    }

    [Fact]
    public void DoesNotCreateComputeEnvironmentsWhenDisabled()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzdProject(sample.AzureYamlPath, options => options.CreateComputeEnvironments = false);

        Assert.Empty(builder.Resources.OfType<AzureContainerAppEnvironmentResource>());
        Assert.Empty(builder.Resources.OfType<AzureAppServiceEnvironmentResource>());
    }

    [Fact]
    public void WiresUsesToReferencedResources()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create();

        var import = builder.AddAzdProject(sample.AzureYamlPath);

        var web = import.Services["web"].Resource;
        var referenced = web.Annotations
            .OfType<ResourceRelationshipAnnotation>()
            .Select(a => a.Resource.Name)
            .ToHashSet();

        Assert.Contains("cache", referenced);
        Assert.Contains("secrets", referenced);
        Assert.Contains("pg", referenced);
    }

    [Fact]
    public void AppliesServiceEnvironmentVariables()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create();

        var import = builder.AddAzdProject(sample.AzureYamlPath);

        // The env block on the web service should be projected onto the resource as an env callback.
        Assert.Contains(import.Services["web"].Resource.Annotations, a => a is EnvironmentCallbackAnnotation);
    }

    [Fact]
    public void LoadsDefaultEnvironment()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create();

        var import = builder.AddAzdProject(sample.AzureYamlPath);

        Assert.NotNull(import.Environment);
        Assert.Equal("dev", import.Environment.Name);
        Assert.Equal("eastus2", import.Environment.Location);
    }

    [Fact]
    public void PreservesExistingInfrastructure()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create();

        var import = builder.AddAzdProject(sample.AzureYamlPath);

        Assert.NotNull(import.InfraPath);
        Assert.EndsWith("infra", import.InfraPath);
        Assert.True(Directory.Exists(import.InfraPath));
        Assert.Contains(import.Diagnostics.Items, d => d.Severity == AzdImportDiagnosticSeverity.Information && d.Message.Contains("preserved"));
    }

    [Fact]
    public void ReportsDiagnosticsForUnsupportedResourceAndHost()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create();

        var import = builder.AddAzdProject(sample.AzureYamlPath);

        // db.mysql has no mapper.
        Assert.Contains(import.Diagnostics.Items, d => d.Severity == AzdImportDiagnosticSeverity.Warning && d.Target == "db2");
        // The function host is not yet supported for automatic compute placement.
        Assert.Contains(import.Diagnostics.Items, d => d.Severity == AzdImportDiagnosticSeverity.Warning && d.Target == "legacy" && d.Message.Contains("function"));
    }

    [Fact]
    public void BindsExistingResourceInsteadOfReprovisioning()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create();

        var import = builder.AddAzdProject(sample.AzureYamlPath);

        // 'secrets' is marked existing: true in the sample. The importer binds it to the already-provisioned
        // Azure resource (the annotation form of AsExisting) so it is not recreated on run or publish.
        Assert.True(import.Resources["secrets"].Resource.IsExisting());

        // The binding is surfaced as information (not a warning); nothing is silently dropped or duplicated.
        Assert.Contains(import.Diagnostics.Items, d => d.Severity == AzdImportDiagnosticSeverity.Information && d.Target == "secrets" && d.Message.Contains("existing"));
    }

    [Fact]
    public void ReportsAzdEnvironmentContractForUses()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create();

        var import = builder.AddAzdProject(sample.AzureYamlPath);

        // web uses cache (db.redis); the customer's app code may read azd's REDIS_* variables, so the
        // importer must surface that contract rather than silently switching to Aspire conventions.
        Assert.Contains(
            import.Diagnostics.Items,
            d => d.Severity == AzdImportDiagnosticSeverity.Information && d.Target == "web" && d.Message.Contains("REDIS_HOST"));
    }

    [Fact]
    public void DetectsTerraformInfrastructureAndWarns()
    {
        var root = Directory.CreateTempSubdirectory("azd-import-tf-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "azure.yaml"), "name: tf-app\n");
            Directory.CreateDirectory(Path.Combine(root.FullName, "infra"));
            File.WriteAllText(Path.Combine(root.FullName, "infra", "main.tf"), "# terraform");

            using var builder = TestDistributedApplicationBuilder.Create();

            var import = builder.AddAzdProject(root.FullName);

            // The provider is not pinned in azure.yaml, so it is detected from the file extensions; a
            // Terraform infra folder is preserved but flagged because Aspire cannot provision it.
            Assert.Contains(
                import.Diagnostics.Items,
                d => d.Severity == AzdImportDiagnosticSeverity.Warning && d.Message.Contains("Terraform"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void AppliesDockerBuildStage()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create();

        var import = builder.AddAzdProject(sample.AzureYamlPath);

        // The api service declares docker.target: build; that must flow through as the Dockerfile build stage.
        var build = import.Services["api"].Resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
        Assert.Equal("build", build.Stage);
    }

    [Fact]
    public void MapsJavaScriptServicesToNativeJavaScriptApps()
    {
        var root = Directory.CreateTempSubdirectory("azd-import-js-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "azure.yaml"),
                """
                name: js-app
                services:
                  web:
                    project: ./src/web
                    language: ts
                    host: containerapp
                  api:
                    project: ./src/api
                    language: js
                    host: appservice
                  worker:
                    project: ./src/worker
                    language: node
                    host: function
                """);

            // "web" exposes a conventional start script (preferred), "api" exposes only dev, and
            // "worker" has no scripts block so the importer falls back to the npm default of "start".
            WritePackageJson(root.FullName, "src/web", """{ "scripts": { "start": "node server.js", "dev": "tsx watch src/server.ts" } }""");
            WritePackageJson(root.FullName, "src/api", """{ "scripts": { "dev": "node server.js" } }""");
            WritePackageJson(root.FullName, "src/worker", """{ "name": "worker" }""");

            using var builder = TestDistributedApplicationBuilder.Create();
            var import = builder.AddAzdProject(root.FullName);

            // js/ts/node services become native Aspire JavaScript apps rather than requiring a container build.
            Assert.IsType<JavaScriptAppResource>(import.Services["web"].Resource);
            Assert.IsType<JavaScriptAppResource>(import.Services["api"].Resource);
            Assert.IsType<JavaScriptAppResource>(import.Services["worker"].Resource);

            Assert.Equal("start", GetRunScript(import.Services["web"].Resource));
            Assert.Equal("dev", GetRunScript(import.Services["api"].Resource));
            Assert.Equal("start", GetRunScript(import.Services["worker"].Resource));

            // Web-hosted services get an HTTP endpoint so the imported app is reachable.
            var endpoint = import.Services["web"].Resource.Annotations.OfType<EndpointAnnotation>().Single();
            Assert.Equal("http", endpoint.UriScheme);

            // azd gives a scaffolded compute service public ingress by default, so the imported endpoint must
            // be external; otherwise a migrated front end would silently become unreachable from the internet.
            Assert.True(endpoint.IsExternal);

            // A non-web host (here treated as a plain executable) does not get an HTTP endpoint implicitly.
            Assert.DoesNotContain(import.Services["worker"].Resource.Annotations, a => a is EndpointAnnotation);
        }
        finally
        {
            root.Delete(recursive: true);
        }

        static void WritePackageJson(string root, string relativeDirectory, string contents)
        {
            var directory = Path.Combine(root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "package.json"), contents);
        }

        static string GetRunScript(IResource resource) =>
            resource.Annotations.OfType<JavaScriptRunScriptAnnotation>().Single().ScriptName;
    }

    [Fact]
    public void PublishesJavaScriptComputeServicesAsDeployableContainers()
    {
        var root = Directory.CreateTempSubdirectory("azd-import-js-publish-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "azure.yaml"),
                """
                name: js-publish-app
                services:
                  runtimeloader:
                    project: ./src/runtimeloader
                    language: ts
                    host: containerapp
                  builder:
                    project: ./src/builder
                    language: ts
                    host: containerapp
                """);

            // "runtimeloader" executes its sources directly (e.g. via tsx) with only a start script and no
            // build step; "builder" compiles with a build script before starting.
            WritePackageJson(root.FullName, "src/runtimeloader", """{ "scripts": { "start": "tsx src/server.ts" } }""");
            WritePackageJson(root.FullName, "src/builder", """{ "scripts": { "build": "tsc", "start": "node dist/server.js" } }""");

            using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
            var import = builder.AddAzdProject(root.FullName);

            var runtimeLoader = import.Services["runtimeloader"].Resource;

            // azd builds and runs a js/ts compute service as a long-lived server container. The imported app
            // must publish with a standalone runtime (PublishAsPackageScript) so it is deployed, not treated as
            // a build-only image. PublishAsPackageScript clears the default container-files source that
            // AddJavaScriptApp configures for build-only (static front end) publishing, so its absence in
            // publish mode confirms the service is deployable.
            Assert.DoesNotContain(runtimeLoader.Annotations, a => a is ContainerFilesSourceAnnotation);

            // The service has no "build" script, so the default build-script must be dropped to keep the
            // generated Dockerfile's `npm run build` from failing the image build.
            Assert.DoesNotContain(runtimeLoader.Annotations, a => a is JavaScriptBuildScriptAnnotation);

            // A service that does define a "build" script is still deployable and keeps its build step.
            var builderService = import.Services["builder"].Resource;
            Assert.DoesNotContain(builderService.Annotations, a => a is ContainerFilesSourceAnnotation);
            Assert.Contains(builderService.Annotations, a => a is JavaScriptBuildScriptAnnotation);
        }
        finally
        {
            root.Delete(recursive: true);
        }

        static void WritePackageJson(string root, string relativeDirectory, string contents)
        {
            var directory = Path.Combine(root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "package.json"), contents);
        }
    }

    [Fact]
    public void ReportsErrorWhenUsedResourceCannotBeReferenced()
    {
        var root = Directory.CreateTempSubdirectory("azd-import-storageuses-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "azure.yaml"),
                """
                name: storage-uses-app
                services:
                  app:
                    project: ./src/app
                    language: dotnet
                    host: containerapp
                    uses:
                      - files
                resources:
                  files:
                    type: storage
                """);
            Directory.CreateDirectory(Path.Combine(root.FullName, "src", "app"));
            File.WriteAllText(Path.Combine(root.FullName, "src", "app", "app.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            using var builder = TestDistributedApplicationBuilder.Create();
            var import = builder.AddAzdProject(root.FullName);

            // AzureStorageResource is not a connection-string resource, so the 'uses' edge cannot be wired
            // and must be surfaced as an error rather than silently dropped.
            Assert.True(import.Diagnostics.HasErrors);
            Assert.Contains(
                import.Diagnostics.Items,
                d => d.Severity == AzdImportDiagnosticSeverity.Error && d.Target == "app" && d.Message.Contains("connection string"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ResolvesAzdExpandableStringsInResourceGroupAndServiceEnv()
    {
        var root = Directory.CreateTempSubdirectory("azd-import-subst-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "azure.yaml"),
                """
                name: subst-app
                resourceGroup: rg-${AZURE_ENV_NAME}
                services:
                  app:
                    image: nginx:latest
                    env:
                      RESOLVED: ${AZURE_ENV_NAME}-suffix
                      LITERAL: plain-value
                      UNKNOWN: ${NOT_IN_ENV}
                """);

            // No AZURE_RESOURCE_GROUP is recorded, so the importer must resolve the azure.yaml
            // resourceGroup expandable string against the environment instead of ignoring it.
            File.WriteAllText(WriteEnvFile(root.FullName, "dev"), "AZURE_ENV_NAME=\"dev\"\nAZURE_LOCATION=\"eastus2\"\n");
            File.WriteAllText(Path.Combine(root.FullName, ".azure", "config.json"), "{ \"version\": 1, \"defaultEnvironment\": \"dev\" }");

            using var builder = TestDistributedApplicationBuilder.Create();
            var import = builder.AddAzdProject(root.FullName);

            // resourceGroup: rg-${AZURE_ENV_NAME} resolves against AZURE_ENV_NAME=dev for both the
            // provisioner option and the AzureEnvironmentResource parameter.
            Assert.Equal("rg-dev", builder.Configuration["Azure:ResourceGroup"]);
            Assert.Equal("rg-dev", builder.Configuration["Parameters:resourceGroupName"]);

            var app = Assert.IsAssignableFrom<IResourceWithEnvironment>(import.Services["app"].Resource);
#pragma warning disable CS0618 // GetEnvironmentVariableValuesAsync is obsolete but is the supported way to resolve env values in tests.
            var env = await app.GetEnvironmentVariableValuesAsync();
#pragma warning restore CS0618

            // A known variable is resolved, a literal is untouched, and an unknown token is preserved
            // (rather than blanked) so an adopter's value is never silently emptied.
            Assert.Equal("dev-suffix", env["RESOLVED"]);
            Assert.Equal("plain-value", env["LITERAL"]);
            Assert.Equal("${NOT_IN_ENV}", env["UNKNOWN"]);
        }
        finally
        {
            root.Delete(recursive: true);
        }

        static string WriteEnvFile(string projectRoot, string environmentName)
        {
            var path = Path.Combine(projectRoot, ".azure", environmentName, ".env");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return path;
        }
    }

    [Fact]
    public void ImportsDotNetFunctionHostAsAzureFunctionsProject()
    {
        var root = Directory.CreateTempSubdirectory("azd-import-fn-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "azure.yaml"),
                """
                name: fn-app
                services:
                  fn:
                    project: ./src/fn
                    language: dotnet
                    host: function
                  jsfn:
                    project: ./src/jsfn
                    language: ts
                    host: function
                """);
            Directory.CreateDirectory(Path.Combine(root.FullName, "src", "fn"));
            File.WriteAllText(Path.Combine(root.FullName, "src", "fn", "fn.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Directory.CreateDirectory(Path.Combine(root.FullName, "src", "jsfn"));
            File.WriteAllText(Path.Combine(root.FullName, "src", "jsfn", "package.json"), "{ \"scripts\": { \"start\": \"node server.js\" } }");

            // Compute environments are only materialized into the model in publish mode, so use it here to
            // assert the Functions project lands on the Azure Container Apps environment.
            using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
            var import = builder.AddAzdProject(root.FullName);

            // azd host: function on a .NET service preserves the Functions host model as an Azure Functions
            // project rather than being flattened to a generic project/container resource.
            Assert.IsType<AzureFunctionsProjectResource>(import.Services["fn"].Resource);

            // The Functions integration is .NET-specific, so a non-.NET function service is not misrouted to
            // it; the TypeScript service still becomes a JavaScript app.
            Assert.IsType<JavaScriptAppResource>(import.Services["jsfn"].Resource);

            // The Functions project is placed on the Azure Container Apps compute environment (the supported
            // Aspire Functions target), and the compute-target substitution is surfaced as a diagnostic.
            Assert.Single(builder.Resources.OfType<AzureContainerAppEnvironmentResource>());
            Assert.Contains(
                import.Diagnostics.Items,
                d => d.Severity == AzdImportDiagnosticSeverity.Warning && d.Target == "fn" && d.Message.Contains("Container Apps"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void DisambiguatesCollidingResourceNames()
    {
        var root = Directory.CreateTempSubdirectory("azd-import-collision-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "azure.yaml"),
                """
                name: collision-app
                resources:
                  my.cache:
                    type: db.redis
                  my-cache:
                    type: db.redis
                """);

            using var builder = TestDistributedApplicationBuilder.Create();
            var import = builder.AddAzdProject(root.FullName);

            // Both azd keys sanitize to "my-cache"; both must still import (keyed by their azd name), the
            // collision must be reported, and AppHost construction must not throw on a duplicate name.
            Assert.True(import.Resources.ContainsKey("my.cache"));
            Assert.True(import.Resources.ContainsKey("my-cache"));
            Assert.Contains(
                import.Diagnostics.Items,
                d => d.Severity == AzdImportDiagnosticSeverity.Warning && d.Message.Contains("collision"));

            var names = new[] { import.Resources["my.cache"].Resource.Name, import.Resources["my-cache"].Resource.Name };
            Assert.Equal(2, names.Distinct().Count());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void CustomResourceMapperTakesPrecedence()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create();

        var import = builder.AddAzdProject(sample.AzureYamlPath, options =>
            options.ResourceMappers.Add(new MySqlMapper()));

        // The custom mapper handles db.mysql, so db2 is imported and produces no "no mapper" warning.
        Assert.True(import.Resources.ContainsKey("db2"));
        Assert.DoesNotContain(import.Diagnostics.Items, d => d.Severity == AzdImportDiagnosticSeverity.Warning && d.Target == "db2");
    }

    [Fact]
    public void ResolvesAzureYamlFromDirectory()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create();

        var import = builder.AddAzdProject(sample.Root.FullName);

        Assert.Equal("contoso-app", import.Project.Name);
    }

    [Fact]
    public void ThrowsWhenAzureYamlMissing()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        Assert.Throws<FileNotFoundException>(() => builder.AddAzdProject("does-not-exist.yaml"));
    }

    [Fact]
    public void GetServiceAndGetResourceReturnTheImportedReference()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create();

        var import = builder.AddAzdProject(sample.AzureYamlPath);

        // The accessors hand back the same builder the importer materialized, so further customization
        // (WithReference, WithReplicas, ...) mutates the real imported resource.
        Assert.Same(import.Services["web"], import.GetService("web"));
        Assert.Same(import.Resources["cache"], import.GetResource("cache"));
    }

    [Fact]
    public async Task GetResourceReturnsReferenceButFailsAtStartupForUnknownName()
    {
        var root = Directory.CreateTempSubdirectory("azd-import-ref-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "azure.yaml"),
                """
                name: ref-app
                resources:
                  cache:
                    type: db.redis
                """);

            using var builder = TestDistributedApplicationBuilder.Create();
            var import = builder.AddAzdProject(root.FullName);

            // The reference is returned immediately; an invalid name does not throw at the call site.
            var missing = import.GetResource("does-not-exist");
            Assert.NotNull(missing);

            // The failure is deferred to BeforeStartEvent, which runs for both `aspire run` and publish/deploy.
            using var app = builder.Build();
            var model = app.Services.GetRequiredService<DistributedApplicationModel>();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, model)));

            Assert.Contains("does-not-exist", ex.Message);
            Assert.Contains("no such resource", ex.Message);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetServiceForAResourceNameDefersFailureWithACorrectiveHint()
    {
        var root = Directory.CreateTempSubdirectory("azd-import-ref2-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "azure.yaml"),
                """
                name: ref-app
                resources:
                  cache:
                    type: db.redis
                """);

            using var builder = TestDistributedApplicationBuilder.Create();
            var import = builder.AddAzdProject(root.FullName);

            // "cache" exists, but it is a resource, not a service.
            import.GetService("cache");

            using var app = builder.Build();
            var model = app.Services.GetRequiredService<DistributedApplicationModel>();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, model)));

            Assert.Contains("'cache' is a resource, not a service", ex.Message);
            Assert.Contains("GetResource(\"cache\")", ex.Message);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetResourceGenericReturnsTypedBuilderOverTheSameUnderlyingResource()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create();

        var import = builder.AddAzdProject(sample.AzureYamlPath);

        // The generic overload re-wraps the loosely-typed import as a strongly-typed builder (via
        // CreateResourceBuilder) so it composes with type-specific extension methods, while still pointing
        // at the same underlying resource the importer materialized.
        IResourceBuilder<AzureManagedRedisResource> cache = import.GetResource<AzureManagedRedisResource>("cache");
        Assert.Same(import.Resources["cache"].Resource, cache.Resource);
    }

    [Fact]
    public void GetServiceGenericReturnsTypedBuilderOverTheSameUnderlyingResource()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create();

        var import = builder.AddAzdProject(sample.AzureYamlPath);

        IResourceBuilder<ProjectResource> web = import.GetService<ProjectResource>("web");
        Assert.Same(import.Services["web"].Resource, web.Resource);
    }

    [Fact]
    public void GetResourceGenericThrowsImmediatelyForWrongType()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create();

        var import = builder.AddAzdProject(sample.AzureYamlPath);

        // "cache" is an AzureManagedRedisResource. A typed placeholder cannot be synthesized for the wrong
        // type, so unlike the loosely-typed overload the mismatch is reported immediately.
        var ex = Assert.Throws<InvalidOperationException>(() => import.GetResource<AzureKeyVaultResource>("cache"));
        Assert.Contains(nameof(AzureManagedRedisResource), ex.Message);
        Assert.Contains(nameof(AzureKeyVaultResource), ex.Message);
    }

    [Fact]
    public void GetResourceGenericThrowsImmediatelyForUnknownName()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create();

        var import = builder.AddAzdProject(sample.AzureYamlPath);

        var ex = Assert.Throws<InvalidOperationException>(() => import.GetResource<AzureKeyVaultResource>("does-not-exist"));
        Assert.Contains("does-not-exist", ex.Message);
        Assert.Contains("no such resource", ex.Message);
    }

    [Fact]
    public void ReportsResourceLevelUsesAsUnwiredDiagnostic()
    {
        var root = Directory.CreateTempSubdirectory("azd-import-resuses-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "azure.yaml"),
                """
                name: res-uses-app
                resources:
                  files:
                    type: storage
                    uses:
                      - secrets
                  secrets:
                    type: keyvault
                """);

            using var builder = TestDistributedApplicationBuilder.Create();
            var import = builder.AddAzdProject(root.FullName);

            // azd resource-to-resource `uses` (resources[].uses) is not wired automatically, but it must not
            // be dropped silently: the importer's contract is that anything it cannot reproduce surfaces as a
            // diagnostic so a migrating customer can re-establish the dependency.
            Assert.Contains(
                import.Diagnostics.Items,
                d => d.Severity == AzdImportDiagnosticSeverity.Warning
                    && d.Target == "files"
                    && d.Message.Contains("resource-to-resource"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void IgnoresResourceGroupWithUnresolvedBareToken()
    {
        var root = Directory.CreateTempSubdirectory("azd-import-rg-unresolved-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "azure.yaml"),
                """
                name: rg-unresolved-app
                resourceGroup: rg-$UNKNOWN_VAR
                resources:
                  cache:
                    type: db.redis
                """);

            // The environment loads (AZURE_ENV_NAME/AZURE_LOCATION present) but records no AZURE_RESOURCE_GROUP
            // and no UNKNOWN_VAR, so the azure.yaml resourceGroup expandable string cannot be resolved.
            var envPath = Path.Combine(root.FullName, ".azure", "dev", ".env");
            Directory.CreateDirectory(Path.GetDirectoryName(envPath)!);
            File.WriteAllText(envPath, "AZURE_ENV_NAME=\"dev\"\nAZURE_LOCATION=\"eastus2\"\n");
            File.WriteAllText(Path.Combine(root.FullName, ".azure", "config.json"), "{ \"version\": 1, \"defaultEnvironment\": \"dev\" }");

            using var builder = TestDistributedApplicationBuilder.Create();
            builder.AddAzdProject(root.FullName);

            // A bare "$VAR" that cannot be resolved must not leak into the provisioning target as the literal
            // "rg-$UNKNOWN_VAR" (an invalid resource group name); it is ignored instead.
            Assert.Null(builder.Configuration["Azure:ResourceGroup"]);
            Assert.Null(builder.Configuration["Parameters:resourceGroupName"]);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private sealed class MySqlMapper : IAzdResourceMapper
    {
        public bool CanMap(AzdResourceMapContext context)
            => string.Equals(context.Resource.Type, "db.mysql", StringComparison.OrdinalIgnoreCase);

        public IResourceBuilder<IResource> Map(AzdResourceMapContext context)
            => context.Builder.AddAzureKeyVault(context.ResourceName);
    }
}
