// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.EntityFrameworkCore.Tests;

public class EFMigrationPipelineTests
{
    [Fact]
    public async Task BundleOnlyProducesGenerateStep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle();

        var steps = await CreateStepsAsync(builder, migrations.Resource);

        var generateStep = Assert.Single(steps);
        Assert.Equal("mymigrations-generate-migration-bundle", generateStep.Name);
        Assert.Contains(WellKnownPipelineSteps.Publish, generateStep.RequiredBySteps);
        Assert.DoesNotContain(WellKnownPipelineSteps.Build, generateStep.RequiredBySteps);
        Assert.Empty(generateStep.DependsOnSteps);
    }

    [Fact]
    public async Task ScriptOnlyProducesScriptStep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationScript();

        var steps = await CreateStepsAsync(builder, migrations.Resource);

        var scriptStep = Assert.Single(steps);
        Assert.Equal("mymigrations-generate-migration-script", scriptStep.Name);
        Assert.Contains(WellKnownPipelineSteps.Publish, scriptStep.RequiredBySteps);
    }

    [Fact]
    public async Task ScriptAndBundleProducesAllStepsWithDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationScript()
            .PublishAsMigrationBundle();

        var steps = await CreateStepsAsync(builder, migrations.Resource);

        Assert.Equal(2, steps.Count);

        var scriptStep = Assert.Single(steps, s => s.Name == "mymigrations-generate-migration-script");
        var bundleStep = Assert.Single(steps, s => s.Name == "mymigrations-generate-migration-bundle");

        // Bundle generation depends on script to avoid parallel tool usage
        Assert.Contains(scriptStep.Name, bundleStep.DependsOnSteps);
    }

    [Fact]
    public async Task NoPublishOptionsProducesNoSteps()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        var steps = await CreateStepsAsync(builder, migrations.Resource);

        Assert.Empty(steps);
    }

    [Fact]
    public void WaitForConnectionStringResourceAddsWaitAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db);

        var waitAnnotations = migrations.Resource.Annotations.OfType<WaitAnnotation>().ToList();
        Assert.Single(waitAnnotations);

        // The waited-on resource should be the IResourceWithConnectionString
        var waitedResource = waitAnnotations[0].Resource;
        Assert.IsAssignableFrom<IResourceWithConnectionString>(waitedResource);
        Assert.Equal("mydb", waitedResource.Name);
    }

    [Fact]
    public void WaitForMultipleResourcesAddsMultipleAnnotations()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var db1 = builder.AddResource(new TestDatabaseResource("db1"));
        var db2 = builder.AddResource(new TestDatabaseResource("db2"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db1)
            .WaitFor(db2);

        var waitAnnotations = migrations.Resource.Annotations.OfType<WaitAnnotation>().ToList();
        Assert.Equal(2, waitAnnotations.Count);
    }

    [Fact]
    public void MigrationWithoutWaitAnnotationsHasNoConnectionStringDependency()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle();

        var waitAnnotations = migrations.Resource.Annotations.OfType<WaitAnnotation>().ToList();
        Assert.Empty(waitAnnotations);
    }

    [Fact]
    public void AddEFMigrations_HiddenToolResourceHasDedicatedStartCommand()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        var startCommand = migrations.Resource.ToolResource.Annotations
            .OfType<ResourceCommandAnnotation>()
            .SingleOrDefault(a => a.Name == EFCoreOperationExecutor.ToolStartCommandName);

        Assert.NotNull(startCommand);
    }

    [Fact]
    public void PublishAsMigrationBundleSetsResourceProperties()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(targetRuntime: "linux-x64", selfContained: true, publishContainer: true);

        Assert.True(migrations.Resource.PublishAsMigrationBundle);
        Assert.Equal("linux-x64", migrations.Resource.BundleTargetRuntime);
        Assert.True(migrations.Resource.BundleSelfContained);
        Assert.True(migrations.Resource.PublishBundleContainer);
    }

    [Fact]
    public void PublishAsMigrationBundleDefaultsPublishContainerToFalse()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle();

        Assert.True(migrations.Resource.PublishAsMigrationBundle);
        Assert.False(migrations.Resource.PublishBundleContainer);
    }

    [Fact]
    public void PublishAsMigrationScriptSetsResourceProperties()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationScript(idempotent: true, noTransactions: true);

        Assert.True(migrations.Resource.PublishAsMigrationScript);
        Assert.True(migrations.Resource.ScriptIdempotent);
        Assert.True(migrations.Resource.ScriptNoTransactions);
        Assert.False(migrations.Resource.PublishAsMigrationBundle);
    }

    [Fact]
    public void PublishBundleContainerDefaultsTargetRuntimeToLinuxX64()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(publishContainer: true);

        Assert.Equal("linux-x64", migrations.Resource.BundleTargetRuntime);
    }

    [Fact]
    public void PublishBundleContainerPreservesUserSpecifiedTargetRuntime()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(targetRuntime: "linux-arm64", publishContainer: true);

        Assert.Equal("linux-arm64", migrations.Resource.BundleTargetRuntime);
    }

    [Fact]
    public void PublishBundleContainerDoesNotForceSelfContained()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(publishContainer: true);

        Assert.False(migrations.Resource.BundleSelfContained);
    }

    [Fact]
    public void WithoutPublishContainerNoTargetRuntimeDefaultIsApplied()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle();

        Assert.Null(migrations.Resource.BundleTargetRuntime);
    }

    [Fact]
    public void EFMigrationResourceIsAComputeResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        // EFMigrationResource now inherits from ContainerResource so a compute environment can deploy it
        // as a container when PublishBundleContainer is true. Without that option no container image
        // is associated with the resource, so the compute environment ignores it (IsContainer() is false).
        Assert.IsAssignableFrom<ContainerResource>(migrations.Resource);
        Assert.IsAssignableFrom<IComputeResource>(migrations.Resource);
        Assert.False(migrations.Resource.IsContainer());
    }

    [Fact]
    public void PublishBundleContainerAddsDockerfileBuildAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(publishContainer: true);

        var dockerfileAnnotation = Assert.Single(migrations.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.NotNull(dockerfileAnnotation.DockerfileFactory);
        // The Dockerfile build context points at the same 'efmigrations' subfolder of the pipeline
        // output directory that the generate step writes the bundle to. TestDistributedApplicationBuilder
        // configures Pipeline:OutputPath to './' so the context resolves under the current directory.
        Assert.Equal(Path.Combine(Path.GetFullPath("./"), "efmigrations"), dockerfileAnnotation.ContextPath);
        Assert.True(migrations.Resource.IsContainer());
    }

    [Fact]
    public void PublishBundleContainerDoesNotAddDockerfileInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(publishContainer: true);

        // The container-image wiring is only meaningful during `aspire publish`. In run mode
        // the user interacts with the migration resource via its dashboard commands, so no
        // container image should be materialized locally even when publishContainer: true.
        Assert.Empty(migrations.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.False(migrations.Resource.IsContainer());

        // Flags on the resource itself are still set so subsequent publish runs behave correctly.
        Assert.True(migrations.Resource.PublishBundleContainer);
    }

    [Fact]
    public void PublishBundleContainerDoesNotAddDockerfileWhenOptionIsFalse()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle();

        Assert.Empty(migrations.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.False(migrations.Resource.IsContainer());
    }

    [Fact]
    public void GeneratedDockerfileReferencesWaitedOnConnectionStringEnvVar()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(publishContainer: true);

        var dockerfile = EFMigrationResourceBuilderExtensions.GenerateDockerfile(migrations.Resource);

        Assert.Contains("FROM mcr.microsoft.com/dotnet/runtime:10.0", dockerfile);
        // Bundle file name follows the target runtime (linux-x64 default => no .exe suffix).
        Assert.Contains("COPY --chmod=0755 mymigrations /app/efbundle", dockerfile);
        Assert.DoesNotContain("RUN chmod", dockerfile);
        Assert.Contains("ENTRYPOINT /app/efbundle -v --connection \"$ConnectionStrings__mydb\"", dockerfile);
    }

    [Fact]
    public void GeneratedDockerfileUsesSelfContainedBaseImageWhenRequested()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(selfContained: true, publishContainer: true);

        var dockerfile = EFMigrationResourceBuilderExtensions.GenerateDockerfile(migrations.Resource);

        Assert.Contains("FROM mcr.microsoft.com/dotnet/runtime-deps:10.0", dockerfile);
        Assert.DoesNotContain("chiseled", dockerfile);
    }

    [Theory]
    [InlineData("net11.0", "mcr.microsoft.com/dotnet/runtime:11.0")]
    [InlineData("net10.0", "mcr.microsoft.com/dotnet/runtime:10.0")]
    [InlineData("net8.0", "mcr.microsoft.com/dotnet/runtime:10.0")] // clamped to minimum
    [InlineData(null, "mcr.microsoft.com/dotnet/runtime:10.0")] // no framework resolved yet
    public void ResolveBaseImageUsesResolvedFrameworkWithMinimumFloor(string? resolvedFramework, string expectedImage)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(publishContainer: true);

        migrations.Resource.ResolvedFramework = resolvedFramework;

        var baseImage = EFMigrationResourceBuilderExtensions.ResolveBaseImage(migrations.Resource);
        Assert.Equal(expectedImage, baseImage);
    }

    [Fact]
    public void ResolveBaseImageReturnsUserOverrideVerbatim()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(publishContainer: true, baseImage: "myregistry.io/custom-runtime:preview");

        // Even if a framework is resolved, the user override wins.
        migrations.Resource.ResolvedFramework = "net11.0";

        var baseImage = EFMigrationResourceBuilderExtensions.ResolveBaseImage(migrations.Resource);
        Assert.Equal("myregistry.io/custom-runtime:preview", baseImage);
    }

    [Fact]
    public void ResolveBaseImageAppendsWindowsTagForWinRuntime()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(targetRuntime: "win-x64", publishContainer: true);

        var baseImage = EFMigrationResourceBuilderExtensions.ResolveBaseImage(migrations.Resource);
        Assert.Equal("mcr.microsoft.com/dotnet/runtime:10.0-nanoserver-ltsc2022", baseImage);
    }

    [Fact]
    public void GeneratedDockerfileUsesWindowsPathsAndEnvSyntaxForWinRuntime()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(targetRuntime: "win-x64", publishContainer: true);

        var dockerfile = EFMigrationResourceBuilderExtensions.GenerateDockerfile(migrations.Resource);

        Assert.Contains("FROM mcr.microsoft.com/dotnet/runtime:10.0-nanoserver-ltsc2022", dockerfile);
        Assert.Contains("WORKDIR C:\\app", dockerfile);
        Assert.Contains("COPY mymigrations.exe C:\\app\\efbundle.exe", dockerfile);
        Assert.DoesNotContain("--chmod", dockerfile);
        // Windows uses %VAR% for env-var expansion via cmd.exe.
        Assert.Contains("ENTRYPOINT C:\\app\\efbundle.exe -v --connection \"%ConnectionStrings__mydb%\"", dockerfile);
    }

    [Fact]
    public void GeneratedDockerfileFailsWhenNoWaitedOnConnectionStringResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle(publishContainer: true);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            EFMigrationResourceBuilderExtensions.GenerateDockerfile(migrations.Resource));
        Assert.Contains("'.WaitFor(<database>)'", ex.Message);
    }

    [Fact]
    public void GeneratedDockerfileFailsWhenMultipleUnrelatedWaitedOnConnectionStringResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var db1 = builder.AddResource(new TestDatabaseResource("db1"));
        var db2 = builder.AddResource(new TestDatabaseResource("db2"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db1)
            .WaitFor(db2)
            .PublishAsMigrationBundle(publishContainer: true);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            EFMigrationResourceBuilderExtensions.GenerateDockerfile(migrations.Resource));
        Assert.Contains("multiple", ex.Message);
        Assert.Contains("unrelated", ex.Message);
        Assert.Contains("'db1'", ex.Message);
        Assert.Contains("'db2'", ex.Message);
    }

    [Fact]
    public void GeneratedDockerfilePrefersLeafWhenWaitForChildAlsoWaitsOnParent()
    {
        // WaitForStart on an IResourceWithParent dependency automatically adds a wait annotation
        // for the parent as well (ResourceBuilderExtensions.WaitForStartCore). That puts BOTH the
        // server and the database in the candidate set here — but the leaf (database) is the one
        // whose connection string should be injected into the bundle container.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var server = builder.AddResource(new TestDatabaseResource("sql"));
        var database = builder.AddResource(new TestChildDatabaseResource("sqldata", server.Resource));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitForStart(database)
            .PublishAsMigrationBundle(publishContainer: true);

        // Sanity check: the automatic parent-wait really does add the server as a second candidate.
        var waitedResources = migrations.Resource.Annotations.OfType<WaitAnnotation>().Select(w => w.Resource).ToList();
        Assert.Contains(server.Resource, waitedResources);
        Assert.Contains((IResource)database.Resource, waitedResources);

        // The leaf (child) resource's connection string is what the bundle should use.
        var dockerfile = EFMigrationResourceBuilderExtensions.GenerateDockerfile(migrations.Resource);
        Assert.Contains("ConnectionStrings__sqldata", dockerfile);
    }

    [Fact]
    public void BundleFileNameForWindowsRuntimeHasExeSuffix()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle(targetRuntime: "win-x64");

        Assert.Equal("mymigrations.exe", EFResourceBuilderExtensions.GetBundleFileName(migrations.Resource));
    }

    [Fact]
    public void BundleFileNameForLinuxRuntimeHasNoExeSuffix()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle(targetRuntime: "linux-arm64");

        Assert.Equal("mymigrations", EFResourceBuilderExtensions.GetBundleFileName(migrations.Resource));
    }

    [Fact]
    public async Task PublishBundleContainerMakesBuildStepDependOnGenerateStep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(publishContainer: true);

        var steps = await CreateStepsAsync(builder, migrations.Resource);

        var generateStep = Assert.Single(steps, s => s.Name == "mymigrations-generate-migration-bundle");

        // The generate step names the specific image-build step in RequiredBySteps. Relying only
        // on RequiredBy(Build) wouldn't be enough: both the generate step and the `build-<name>`
        // step are siblings of the Build aggregation step, so without a direct edge they run in
        // parallel and the image build reads a half-written bundle from the Docker build context.
        Assert.Contains($"build-{migrations.Resource.Name}", generateStep.RequiredBySteps);
        Assert.Contains(WellKnownPipelineSteps.Publish, generateStep.RequiredBySteps);
    }

    private static async Task<List<PipelineStep>> CreateStepsAsync(
        IDistributedApplicationTestingBuilder builder,
        EFMigrationResource migrationResource)
    {
        using var serviceProvider = builder.Services.BuildServiceProvider();
        var pipelineContext = new PipelineContext(
            serviceProvider.GetRequiredService<DistributedApplicationModel>(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
            serviceProvider,
            NullLogger.Instance,
            CancellationToken.None);

        var results = new List<PipelineStep>();
        foreach (var annotation in migrationResource.Annotations.OfType<PipelineStepAnnotation>())
        {
            results.AddRange(await annotation.CreateStepsAsync(new PipelineStepFactoryContext
            {
                PipelineContext = pipelineContext,
                Resource = migrationResource
            }));
        }

        return results;
    }

    // Test classes for DbContext types
    private sealed class TestDbContext { }

    /// <summary>
    /// A minimal test resource that implements IResourceWithConnectionString and IResourceWithWaitSupport.
    /// </summary>
    private sealed class TestDatabaseResource(string name) : Resource(name), IResourceWithConnectionString, IResourceWithWaitSupport
    {
        public ReferenceExpression ConnectionStringExpression =>
            ReferenceExpression.Create($"Host=localhost;Database={Name}");
    }

    /// <summary>
    /// A minimal test child resource that links to a parent via IResourceWithParent.
    /// Mirrors the SqlServerDatabase/SqlServerServer pattern where WaitFor(child) transitively
    /// adds a WaitAnnotation for the parent.
    /// </summary>
    private sealed class TestChildDatabaseResource(string name, TestDatabaseResource parent)
        : Resource(name), IResourceWithConnectionString, IResourceWithWaitSupport, IResourceWithParent<TestDatabaseResource>
    {
        public TestDatabaseResource Parent { get; } = parent;

        public ReferenceExpression ConnectionStringExpression =>
            ReferenceExpression.Create($"{Parent};Database={Name}");
    }
}