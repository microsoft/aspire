// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001
#pragma warning disable ASPIREDOCKERFILEBUILDER001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES004

using System.Text;
using System.Text.Json;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Tests.Pipelines;

[Trait("Partition", "4")]
public class PipelineOutputRegistryTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void OutputDefinition_RejectsRootedDefaultPath()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new PipelineOutputDefinition(
                "inventory",
                Path.GetFullPath(".configgen"),
                PipelineOutputKind.Directory));

        Assert.Equal("defaultPath", exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Registry_RejectsIncompleteRelocationConfiguration(bool configureStagingPath)
    {
        var bootstrapConfiguration = new ConfigurationBuilder().Build();
        using var fileSystem = new FileSystemService(bootstrapConfiguration);
        var root = fileSystem.TempDirectory.CreateTempSubdirectory("pipeline-relocation-configuration-tests").Path;
        var appHostDirectory = Path.Combine(root, "AppHost");
        Directory.CreateDirectory(appHostDirectory);
        var configurationValues = new Dictionary<string, string?>
        {
            ["AppHost:Directory"] = appHostDirectory,
            [configureStagingPath ? PipelineOutputRegistry.StagingPathConfigurationKey : PipelineOutputRegistry.TargetOutputPathConfigurationKey] =
                Path.Combine(root, configureStagingPath ? "staging" : "target")
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();
        var pipelineOptions = Options.Create(new PipelineOptions { OutputPath = Path.Combine(root, "primary") });
        var outputService = new PipelineOutputService(pipelineOptions, configuration, fileSystem);

        var exception = Assert.Throws<InvalidOperationException>(
            () => new PipelineOutputRegistry(configuration, outputService, pipelineOptions));

        Assert.Contains("must be specified together", exception.Message);
    }

    [Fact]
    public async Task MarkExecutionFailed_UnblocksPreparationWait()
    {
        using var fixture = CreateRegistry();
        var failure = new InvalidOperationException("Pipeline preparation failed.");

        fixture.Registry.MarkExecutionFailed(failure);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Registry.WaitForPreparationAsync(CancellationToken.None));
        Assert.Same(failure, exception);
        Assert.Equal(PipelineOutputExecutionState.Failed, fixture.Registry.GetExecutionState());
    }

    [Fact]
    public void Prepare_ResolvesNamedOutputsFromAppHostDirectory()
    {
        using var fixture = CreateRegistry();
        var configgen = new PipelineOutputDefinition("inventory", ".configgen", PipelineOutputKind.Directory);
        var pipelines = new PipelineOutputDefinition("pipelines", ".pipelines", PipelineOutputKind.Directory);
        var step = CreateStep("config-generator", configgen, pipelines);

        fixture.Registry.Prepare([step]);

        var resolver = new PipelineStepOutputResolver(fixture.Registry, step);
        Assert.Equal(fixture.AppHostDirectory, resolver.AppHostDirectory);
        Assert.Equal(fixture.PrimaryOutputPath, resolver.PrimaryOutput.OutputPath);
        Assert.Equal(
            Path.Combine(fixture.AppHostDirectory, ".configgen"),
            resolver.Resolve(configgen).OutputPath);
        Assert.Equal(
            Path.Combine(fixture.AppHostDirectory, ".pipelines"),
            resolver.Resolve(pipelines).LogicalTargetPath);
    }

    [Fact]
    public async Task Prepare_WithRelocation_FreezesPlanUntilAuthorized()
    {
        using var fixture = CreateRegistry(relocate: true);
        var definition = new PipelineOutputDefinition("inventory", ".configgen", PipelineOutputKind.Directory);
        var step = CreateStep("config-generator", definition);

        fixture.Registry.Prepare([step]);

        var output = new PipelineStepOutputResolver(fixture.Registry, step).Resolve(definition);
        Assert.Equal(Path.Combine(fixture.AppHostDirectory, ".configgen"), output.LogicalTargetPath);
        Assert.StartsWith(
            Path.Combine(fixture.StagingPath!, "outputs") + Path.DirectorySeparatorChar,
            output.OutputPath,
            StringComparison.Ordinal);
        Assert.Equal(PipelineOutputExecutionState.Prepared, fixture.Registry.GetExecutionState());

        var authorizationTask = fixture.Registry.WaitForExecutionAuthorizationAsync(CancellationToken.None);
        Assert.False(authorizationTask.IsCompleted);

        fixture.Registry.AuthorizeExecution();
        await authorizationTask;

        fixture.Registry.MarkExecutionSucceeded();
        Assert.Equal(PipelineOutputExecutionState.Succeeded, fixture.Registry.GetExecutionState());
    }

    [Fact]
    public void AuthorizeExecution_RejectsStepWithoutRelocationSupport()
    {
        using var fixture = CreateRegistry(relocate: true);
        var step = new PipelineStep
        {
            Name = "legacy-publisher",
            Action = _ => Task.CompletedTask
        };
        fixture.Registry.Prepare([step]);

        var exception = Assert.Throws<InvalidOperationException>(fixture.Registry.AuthorizeExecution);

        Assert.Contains("'legacy-publisher'", exception.Message);
        Assert.Equal(PipelineOutputExecutionState.Prepared, fixture.Registry.GetExecutionState());
    }

    [Fact]
    public void Prepare_AppliesConfiguredNamedOutputPath()
    {
        using var fixture = CreateRegistry(
            additionalConfiguration: new Dictionary<string, string?>
            {
                ["Pipeline:Outputs:config-generator:inventory:Path"] = "../.configgen"
            });
        var definition = new PipelineOutputDefinition("inventory", "unused", PipelineOutputKind.Directory);
        var step = CreateStep("config-generator", definition);

        fixture.Registry.Prepare([step]);

        var output = new PipelineStepOutputResolver(fixture.Registry, step).Resolve(definition);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(fixture.AppHostDirectory, "..", ".configgen")),
            output.LogicalTargetPath);
    }

    [Fact]
    public void Prepare_IsDeterministicAndAllowsIdenticalDeclarations()
    {
        using var fixture = CreateRegistry();
        var second = new PipelineOutputDefinition("second", ".pipelines", PipelineOutputKind.Directory);
        var first = new PipelineOutputDefinition("first", ".configgen", PipelineOutputKind.Directory);
        var step = CreateStep("publisher", second, first, first);

        fixture.Registry.Prepare([step]);

        Assert.Collection(
            fixture.Registry.GetOutputs(),
            output => Assert.Equal("aspire/primary", $"{output.PublisherName}/{output.Name}"),
            output => Assert.Equal("publisher/first", $"{output.PublisherName}/{output.Name}"),
            output => Assert.Equal("publisher/second", $"{output.PublisherName}/{output.Name}"));
    }

    [Fact]
    public void Prepare_AllowsNamedOutputWithPrimarySyntheticIdentifiers()
    {
        using var fixture = CreateRegistry();
        var definition = new PipelineOutputDefinition("primary", ".configgen", PipelineOutputKind.Directory);
        var step = CreateStep("aspire", definition);

        fixture.Registry.Prepare([step]);

        var primary = fixture.Registry.GetPrimaryOutput();
        var named = new PipelineStepOutputResolver(fixture.Registry, step).Resolve(definition);
        Assert.NotSame(primary, named);
        Assert.True(primary.IsPrimary);
        Assert.False(named.IsPrimary);
        Assert.Equal(Path.Combine(fixture.AppHostDirectory, ".configgen"), named.OutputPath);
        Assert.Collection(
            fixture.Registry.GetOutputs().Where(output => output.PublisherName == "aspire" && output.Name == "primary"),
            output => Assert.True(output.IsPrimary),
            output => Assert.False(output.IsPrimary));
    }

    [Fact]
    public void Prepare_RejectsConflictingDeclarations()
    {
        using var fixture = CreateRegistry();
        var step = CreateStep(
            "publisher",
            new PipelineOutputDefinition("inventory", ".configgen", PipelineOutputKind.Directory),
            new PipelineOutputDefinition("inventory", ".other-configgen", PipelineOutputKind.Directory));

        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Registry.Prepare([step]));

        Assert.Contains("conflicting metadata", exception.Message);
    }

    [Fact]
    public void Prepare_RejectsOverlappingNamedOutputs()
    {
        using var fixture = CreateRegistry();
        var step = CreateStep(
            "publisher",
            new PipelineOutputDefinition("inventory", ".configgen", PipelineOutputKind.Directory),
            new PipelineOutputDefinition("nested", Path.Combine(".configgen", "nested"), PipelineOutputKind.Directory));

        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Registry.Prepare([step]));

        Assert.Contains("overlapping target paths", exception.Message);
    }

    [Fact]
    public void Prepare_RejectsNamedOutputOverlappingRelocatedPrimaryOutput()
    {
        using var fixture = CreateRegistry(relocate: true);
        var step = CreateStep(
            "publisher",
            new PipelineOutputDefinition("duplicate", "aspire-output", PipelineOutputKind.Directory));

        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Registry.Prepare([step]));

        Assert.Contains("aspire/primary", exception.Message);
    }

    [Fact]
    public void Prepare_RejectsNamedOutputEqualToPrimaryOutputWithoutRelocation()
    {
        using var fixture = CreateRegistry();
        var step = CreateStep(
            "publisher",
            new PipelineOutputDefinition("duplicate", "aspire-output", PipelineOutputKind.Directory));

        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Registry.Prepare([step]));

        Assert.Contains("aspire/primary", exception.Message);
    }

    [Fact]
    public void Prepare_AllowsNamedOutputInsidePrimaryOutputWithoutRelocation()
    {
        using var fixture = CreateRegistry();
        var step = CreateStep(
            "publisher",
            new PipelineOutputDefinition(
                "nested",
                Path.Combine("aspire-output", "nested"),
                PipelineOutputKind.Directory));

        fixture.Registry.Prepare([step]);

        Assert.Equal(2, fixture.Registry.GetOutputs().Count);
    }

    [Fact]
    public void Prepare_RejectsTargetWithWrongKind()
    {
        using var fixture = CreateRegistry();
        var filePath = Path.Combine(fixture.AppHostDirectory, "existing-file");
        File.WriteAllText(filePath, "content");
        var step = CreateStep(
            "publisher",
            new PipelineOutputDefinition("inventory", "existing-file", PipelineOutputKind.Directory));

        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Registry.Prepare([step]));

        Assert.Contains("existing file", exception.Message);
    }

    [Fact]
    public void Resolve_RejectsDefinitionNotInFrozenPlan()
    {
        using var fixture = CreateRegistry();
        var declared = new PipelineOutputDefinition("inventory", ".configgen", PipelineOutputKind.Directory);
        var step = CreateStep("publisher", declared);
        fixture.Registry.Prepare([step]);
        var undeclared = new PipelineOutputDefinition("pipelines", ".pipelines", PipelineOutputKind.Directory);

        var exception = Assert.Throws<InvalidOperationException>(
            () => new PipelineStepOutputResolver(fixture.Registry, step).Resolve(undeclared));

        Assert.Contains("is not declared", exception.Message);
    }

    [Fact]
    public void Prepare_RejectsSecondPreparation()
    {
        using var fixture = CreateRegistry();
        fixture.Registry.Prepare([]);

        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Registry.Prepare([]));

        Assert.Contains("already been prepared", exception.Message);
    }

    [Fact]
    public void PrimaryOutput_UsesLogicalTargetKindDuringRelocation()
    {
        using var fixture = CreateRegistry(
            relocate: true,
            primaryTargetFileName: "aspire-manifest.json",
            pipelineStep: WellKnownPipelineSteps.PublishManifest);
        fixture.Registry.Prepare([]);

        var primary = fixture.Registry.GetPrimaryOutput();
        Assert.Equal(PipelineOutputKind.File, primary.Kind);
        Assert.Equal(fixture.PrimaryOutputPath, primary.OutputPath);
        Assert.Equal(fixture.PrimaryTargetPath, primary.LogicalTargetPath);
    }

    [Fact]
    public void PrimaryOutput_UsesDirectoryKindForNonManifestJsonTarget()
    {
        using var fixture = CreateRegistry(
            primaryTargetFileName: "artifacts.json",
            createPrimaryTargetDirectory: true,
            pipelineStep: "publish-docker-compose",
            additionalConfiguration: new Dictionary<string, string?>
            {
                ["Pipeline:Step"] = WellKnownPipelineSteps.PublishManifest
            });
        fixture.Registry.Prepare([]);

        var primary = fixture.Registry.GetPrimaryOutput();

        Assert.Equal(PipelineOutputKind.Directory, primary.Kind);
        Assert.Equal(fixture.PrimaryTargetPath, primary.OutputPath);
        Assert.Equal(fixture.PrimaryTargetPath, primary.LogicalTargetPath);
    }

    [Fact]
    public void Prepare_BoundsStagedOutputNamesAndRetainsHashUniqueness()
    {
        using var fixture = CreateRegistry(relocate: true);
        var first = new PipelineOutputDefinition(
            $"{new string('o', 300)}a",
            "first.json",
            PipelineOutputKind.File);
        var second = new PipelineOutputDefinition(
            $"{new string('o', 300)}b",
            "second.json",
            PipelineOutputKind.File);
        var longExtension = $".{new string('\u754c', 20)}";
        var third = new PipelineOutputDefinition(
            "long-extension",
            $"third{longExtension}",
            PipelineOutputKind.File);
        var step = CreateStep(new string('\u754c', 200), first, second, third);

        fixture.Registry.Prepare([step]);

        var resolver = new PipelineStepOutputResolver(fixture.Registry, step);
        var firstFileName = Path.GetFileName(resolver.Resolve(first).OutputPath);
        var secondFileName = Path.GetFileName(resolver.Resolve(second).OutputPath);
        var thirdFileName = Path.GetFileName(resolver.Resolve(third).OutputPath);
        Assert.All(
            [firstFileName, secondFileName, thirdFileName],
            fileName =>
            {
                Assert.InRange(
                    Encoding.UTF8.GetByteCount(fileName),
                    1,
                    PipelineOutputRegistry.MaximumStagedOutputNameByteCount);
            });
        Assert.EndsWith(".json", firstFileName, StringComparison.Ordinal);
        Assert.EndsWith(".json", secondFileName, StringComparison.Ordinal);
        Assert.EndsWith($".{new string('\u754c', 10)}", thirdFileName, StringComparison.Ordinal);
        Assert.NotEqual(firstFileName, secondFileName);
    }

    [Fact]
    public async Task PipelineExecutor_WaitsForAuthorizationBeforeBeforePublishAndSteps()
    {
        var bootstrapConfiguration = new ConfigurationBuilder().Build();
        using var fileSystem = new FileSystemService(bootstrapConfiguration);
        var root = fileSystem.TempDirectory.CreateTempSubdirectory("pipeline-authorization-tests").Path;
        var appHostDirectory = Path.Combine(root, "repo", "src", "AppHost");
        var stagingPath = Path.Combine(root, "staging");
        var primaryTargetPath = Path.Combine(appHostDirectory, "aspire-output");
        var primaryOutputPath = Path.Combine(stagingPath, "primary");
        Directory.CreateDirectory(appHostDirectory);

        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = appHostDirectory,
            testOutputHelper,
            "AppHost:Operation=publish",
            "Pipeline:Step=publish",
            $"Pipeline:OutputPath={primaryOutputPath}",
            $"{PipelineOutputRegistry.StagingPathConfigurationKey}={stagingPath}",
            $"{PipelineOutputRegistry.TargetOutputPathConfigurationKey}={primaryTargetPath}");

        var beforePublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stepExecuted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new PipelineStep
        {
            Name = "publisher",
            SupportsOutputPathRelocation = true,
            Action = _ =>
            {
                stepExecuted.TrySetResult();
                return Task.CompletedTask;
            }
        };
        publisher.RequiredBy(WellKnownPipelineSteps.Publish);
        builder.Pipeline.AddStep(publisher);
        builder.OnBeforePublish((_, _) =>
        {
            // The selected graph is frozen before this event. Mutating the source step here
            // must not introduce a cycle into the already-authorized execution plan.
            publisher.DependsOn(publisher);
            beforePublish.TrySetResult();
            return Task.CompletedTask;
        });

        using var app = builder.Build();
        await app.StartAsync();

        var registry = app.Services.GetRequiredService<PipelineOutputRegistry>();
        await registry.WaitForPreparationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        var rpcTarget = app.Services.GetRequiredService<AppHostRpcTarget>();
        var capabilities = await rpcTarget.GetCapabilitiesAsync(CancellationToken.None);
        Assert.Contains("pipeline-outputs.v1", capabilities);
        var outputPlan = await rpcTarget.GetPipelineOutputsAsync(cancellationToken: CancellationToken.None);
        Assert.Equal(appHostDirectory, outputPlan.AppHostDirectory);
        Assert.Equal(nameof(PipelineOutputExecutionState.Prepared), outputPlan.State);
        Assert.All(outputPlan.Steps, step => Assert.True(step.SupportsOutputPathRelocation));
        Assert.Collection(
            outputPlan.Outputs,
            output =>
            {
                Assert.True(output.IsPrimary);
                Assert.Equal("aspire", output.PublisherName);
            });
        Assert.False(beforePublish.Task.IsCompleted);
        Assert.False(stepExecuted.Task.IsCompleted);

        var authorization = await rpcTarget.AuthorizePipelineExecutionAsync(cancellationToken: CancellationToken.None);
        Assert.True(authorization.IsAuthorized);

        await beforePublish.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stepExecuted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await app.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(PipelineOutputExecutionState.Succeeded, registry.GetExecutionState());
    }

    [Fact]
    public async Task ManifestPublishing_WritesOnlyToRelocatedDirectoryOutput()
    {
        var bootstrapConfiguration = new ConfigurationBuilder().Build();
        using var fileSystem = new FileSystemService(bootstrapConfiguration);
        var root = fileSystem.TempDirectory.CreateTempSubdirectory("manifest-relocation-tests").Path;
        var appHostDirectory = Path.Combine(root, "repo", "src", "AppHost");
        var stagingPath = Path.Combine(root, "staging");
        var primaryTargetPath = Path.Combine(appHostDirectory, "aspire-output");
        var primaryOutputPath = Path.Combine(stagingPath, "primary");
        Directory.CreateDirectory(appHostDirectory);

        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = appHostDirectory,
            testOutputHelper,
            "AppHost:Operation=publish",
            $"Pipeline:Step={WellKnownPipelineSteps.PublishManifest}",
            $"Pipeline:OutputPath={primaryOutputPath}",
            $"{PipelineOutputRegistry.StagingPathConfigurationKey}={stagingPath}",
            $"{PipelineOutputRegistry.TargetOutputPathConfigurationKey}={primaryTargetPath}");
        builder.AddContainer("api", "alpine")
            .WithDockerfileBuilder(appHostDirectory, context => context.Builder.From("alpine"));
        using var app = builder.Build();
        await app.StartAsync();

        var registry = app.Services.GetRequiredService<PipelineOutputRegistry>();
        await registry.WaitForPreparationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        var outputPlan = await app.Services.GetRequiredService<AppHostRpcTarget>()
            .GetPipelineOutputsAsync(cancellationToken: CancellationToken.None);
        var manifestStep = Assert.Single(
            outputPlan.Steps,
            step => step.Name == WellKnownPipelineSteps.PublishManifest);
        Assert.True(manifestStep.SupportsOutputPathRelocation);

        registry.AuthorizeExecution();
        await app.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var generatedManifestPath = Path.Combine(primaryOutputPath, "aspire-manifest.json");
        var logicalManifestPath = Path.Combine(primaryTargetPath, "aspire-manifest.json");
        var generatedDockerfilePath = Path.Combine(Path.GetDirectoryName(generatedManifestPath)!, "api.Dockerfile");
        var logicalDockerfilePath = Path.Combine(Path.GetDirectoryName(logicalManifestPath)!, "api.Dockerfile");
        Assert.True(File.Exists(generatedManifestPath));
        Assert.True(File.Exists(generatedDockerfilePath));
        Assert.False(File.Exists(logicalDockerfilePath));
        Assert.False(File.Exists(primaryTargetPath));
        Assert.False(Directory.Exists(primaryTargetPath));

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(generatedManifestPath));
        var build = manifest.RootElement.GetProperty("resources").GetProperty("api").GetProperty("build");
        Assert.Equal("api.Dockerfile", build.GetProperty("dockerfile").GetString());
        Assert.Equal(
            Path.GetRelativePath(Path.GetDirectoryName(logicalManifestPath)!, appHostDirectory).Replace('\\', '/'),
            build.GetProperty("context").GetString());
        Assert.Equal(PipelineOutputExecutionState.Succeeded, registry.GetExecutionState());
    }

    [Fact]
    public async Task ManifestPublishing_FileOutputDoesNotSupportRelocation()
    {
        var bootstrapConfiguration = new ConfigurationBuilder().Build();
        using var fileSystem = new FileSystemService(bootstrapConfiguration);
        var root = fileSystem.TempDirectory.CreateTempSubdirectory("manifest-file-relocation-tests").Path;
        var appHostDirectory = Path.Combine(root, "repo", "src", "AppHost");
        var stagingPath = Path.Combine(root, "staging");
        var primaryTargetPath = Path.Combine(appHostDirectory, "aspire-manifest.json");
        var primaryOutputPath = Path.Combine(stagingPath, "primary");
        Directory.CreateDirectory(appHostDirectory);

        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = appHostDirectory,
            testOutputHelper,
            "AppHost:Operation=publish",
            $"Pipeline:Step={WellKnownPipelineSteps.PublishManifest}",
            $"Pipeline:OutputPath={primaryOutputPath}",
            $"{PipelineOutputRegistry.StagingPathConfigurationKey}={stagingPath}",
            $"{PipelineOutputRegistry.TargetOutputPathConfigurationKey}={primaryTargetPath}");
        builder.AddContainer("api", "alpine")
            .WithDockerfileBuilder(appHostDirectory, context => context.Builder.From("alpine"));
        using var app = builder.Build();
        await app.StartAsync();

        var registry = app.Services.GetRequiredService<PipelineOutputRegistry>();
        await registry.WaitForPreparationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        var outputPlan = await app.Services.GetRequiredService<AppHostRpcTarget>()
            .GetPipelineOutputsAsync(cancellationToken: CancellationToken.None);
        var manifestStep = Assert.Single(
            outputPlan.Steps,
            step => step.Name == WellKnownPipelineSteps.PublishManifest);
        Assert.False(manifestStep.SupportsOutputPathRelocation);

        var exception = Assert.Throws<InvalidOperationException>(registry.AuthorizeExecution);
        Assert.Contains($"'{WellKnownPipelineSteps.PublishManifest}'", exception.Message);
        Assert.False(File.Exists(primaryTargetPath));
        Assert.False(File.Exists(Path.Combine(appHostDirectory, "api.Dockerfile")));

        await app.StopAsync();
    }

    [Fact]
    public async Task NamedPublishing_WritesOnlyToRelocatedOutputPaths()
    {
        var bootstrapConfiguration = new ConfigurationBuilder().Build();
        using var fileSystem = new FileSystemService(bootstrapConfiguration);
        var root = fileSystem.TempDirectory.CreateTempSubdirectory("named-output-relocation-tests").Path;
        var appHostDirectory = Path.Combine(root, "repo", "src", "AppHost");
        var stagingPath = Path.Combine(root, "staging");
        var primaryTargetPath = Path.Combine(appHostDirectory, "aspire-output");
        var primaryOutputPath = Path.Combine(stagingPath, "primary");
        Directory.CreateDirectory(appHostDirectory);

        var inventory = new PipelineOutputDefinition("inventory", ".configgen", PipelineOutputKind.Directory);
        var pipelines = new PipelineOutputDefinition("pipelines", ".pipelines", PipelineOutputKind.Directory);

        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = appHostDirectory,
            testOutputHelper,
            "AppHost:Operation=publish",
            "Pipeline:Step=publish",
            $"Pipeline:OutputPath={primaryOutputPath}",
            $"{PipelineOutputRegistry.StagingPathConfigurationKey}={stagingPath}",
            $"{PipelineOutputRegistry.TargetOutputPathConfigurationKey}={primaryTargetPath}");
        var publisher = new PipelineStep
        {
            Name = "config-generator",
            Outputs = [inventory, pipelines],
            SupportsOutputPathRelocation = true,
            Action = context =>
            {
                foreach (var output in new[] { inventory, pipelines })
                {
                    var resolved = context.Outputs.Resolve(output);
                    Directory.CreateDirectory(resolved.OutputPath);
                    File.WriteAllText(Path.Combine(resolved.OutputPath, "generated.txt"), resolved.LogicalTargetPath);
                }

                return Task.CompletedTask;
            }
        };
        publisher.RequiredBy(WellKnownPipelineSteps.Publish);
        builder.Pipeline.AddStep(publisher);

        using var app = builder.Build();
        await app.StartAsync();

        var registry = app.Services.GetRequiredService<PipelineOutputRegistry>();
        await registry.WaitForPreparationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        var namedOutputs = registry.GetOutputs()
            .Where(output => output.PublisherName == publisher.Name)
            .ToDictionary(output => output.Name, StringComparer.Ordinal);
        Assert.Equal(Path.Combine(appHostDirectory, ".configgen"), namedOutputs["inventory"].LogicalTargetPath);
        Assert.Equal(Path.Combine(appHostDirectory, ".pipelines"), namedOutputs["pipelines"].LogicalTargetPath);

        registry.AuthorizeExecution();
        await app.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(File.Exists(Path.Combine(namedOutputs["inventory"].OutputPath, "generated.txt")));
        Assert.True(File.Exists(Path.Combine(namedOutputs["pipelines"].OutputPath, "generated.txt")));
        Assert.False(Directory.Exists(Path.Combine(appHostDirectory, ".configgen")));
        Assert.False(Directory.Exists(Path.Combine(appHostDirectory, ".pipelines")));
        Assert.Equal(PipelineOutputExecutionState.Succeeded, registry.GetExecutionState());
    }

    private static PipelineStep CreateStep(string name, params PipelineOutputDefinition[] outputs)
    {
        return new PipelineStep
        {
            Name = name,
            Action = _ => Task.CompletedTask,
            Outputs = [.. outputs],
            SupportsOutputPathRelocation = true
        };
    }

    private static RegistryFixture CreateRegistry(
        bool relocate = false,
        string? primaryTargetFileName = null,
        bool createPrimaryTargetDirectory = false,
        string? pipelineStep = null,
        IReadOnlyDictionary<string, string?>? additionalConfiguration = null)
    {
        var bootstrapConfiguration = new ConfigurationBuilder().Build();
        var fileSystem = new FileSystemService(bootstrapConfiguration);
        var root = fileSystem.TempDirectory.CreateTempSubdirectory("pipeline-output-tests").Path;
        var appHostDirectory = Path.Combine(root, "repo", "src", "AppHost");
        Directory.CreateDirectory(appHostDirectory);

        var primaryTargetPath = Path.Combine(
            appHostDirectory,
            primaryTargetFileName ?? "aspire-output");
        var stagingPath = relocate ? Path.Combine(root, "staging") : null;
        var primaryOutputPath = relocate
            ? Path.Combine(stagingPath!, primaryTargetFileName ?? "primary")
            : primaryTargetPath;
        if (createPrimaryTargetDirectory)
        {
            Directory.CreateDirectory(primaryTargetPath);
        }

        var configurationValues = new Dictionary<string, string?>
        {
            ["AppHost:Directory"] = appHostDirectory
        };

        if (relocate)
        {
            configurationValues[PipelineOutputRegistry.StagingPathConfigurationKey] = stagingPath;
            configurationValues[PipelineOutputRegistry.TargetOutputPathConfigurationKey] = primaryTargetPath;
        }

        if (additionalConfiguration is not null)
        {
            foreach (var (key, value) in additionalConfiguration)
            {
                configurationValues[key] = value;
            }
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();
        var pipelineOptions = Options.Create(new PipelineOptions
        {
            OutputPath = primaryOutputPath,
            Step = pipelineStep
        });
        var outputService = new PipelineOutputService(pipelineOptions, configuration, fileSystem);

        return new RegistryFixture(
            new PipelineOutputRegistry(configuration, outputService, pipelineOptions),
            fileSystem,
            appHostDirectory,
            primaryOutputPath,
            primaryTargetPath,
            stagingPath);
    }

    private sealed class RegistryFixture(
        PipelineOutputRegistry registry,
        FileSystemService fileSystem,
        string appHostDirectory,
        string primaryOutputPath,
        string primaryTargetPath,
        string? stagingPath) : IDisposable
    {
        public PipelineOutputRegistry Registry { get; } = registry;

        public string AppHostDirectory { get; } = appHostDirectory;

        public string PrimaryOutputPath { get; } = primaryOutputPath;

        public string PrimaryTargetPath { get; } = primaryTargetPath;

        public string? StagingPath { get; } = stagingPath;

        public void Dispose()
        {
            fileSystem.Dispose();
        }
    }
}
