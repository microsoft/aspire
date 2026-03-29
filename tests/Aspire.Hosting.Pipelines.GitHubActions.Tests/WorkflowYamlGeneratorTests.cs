// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Pipelines.GitHubActions.Yaml;

namespace Aspire.Hosting.Pipelines.GitHubActions.Tests;

[Trait("Partition", "4")]
public class WorkflowYamlGeneratorTests
{
    [Fact]
    public void Generate_BareWorkflow_CreatesDefaultJobWithBoilerplate()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var step = CreateStep("build-app");

        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);

        Assert.Equal("deploy", yaml.Name);
        Assert.Single(yaml.Jobs);
        Assert.True(yaml.Jobs.ContainsKey("default"));

        var job = yaml.Jobs["default"];
        Assert.Contains(job.Steps, s => s.Name == "Checkout code");
        Assert.Contains(job.Steps, s => s.Name == "Setup .NET");
        Assert.Contains(job.Steps, s => s.Name == "Install Aspire CLI");
        Assert.Contains(job.Steps, s => s.Run is not null && s.Run.Contains("aspire do gha-deploy-default-stage-default-job"));
    }

    [Fact]
    public void Generate_TwoJobs_CorrectNeedsDependencies()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var buildJob = workflow.AddJob("build");
        var deployJob = workflow.AddJob("deploy");

        var buildStep = CreateStep("build-app", buildJob);
        var deployStep = CreateStep("deploy-app", deployJob, "build-app");

        var scheduling = SchedulingResolver.Resolve([buildStep, deployStep], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);

        Assert.Equal(2, yaml.Jobs.Count);
        Assert.Null(yaml.Jobs["build"].Needs);
        Assert.NotNull(yaml.Jobs["deploy"].Needs);
        Assert.Contains("build", yaml.Jobs["deploy"].Needs!);
    }

    [Fact]
    public void Generate_MultipleJobDeps_NeedsContainsAll()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var job1 = workflow.AddJob("build");
        var job2 = workflow.AddJob("test");
        var job3 = workflow.AddJob("deploy");

        var step1 = CreateStep("build-app", job1);
        var step2 = CreateStep("run-tests", job2);
        var step3 = CreateStep("deploy-app", job3, "build-app", "run-tests");

        var scheduling = SchedulingResolver.Resolve([step1, step2, step3], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);

        Assert.NotNull(yaml.Jobs["deploy"].Needs);
        Assert.Contains("build", yaml.Jobs["deploy"].Needs!);
        Assert.Contains("test", yaml.Jobs["deploy"].Needs!);
    }

    [Fact]
    public void Generate_DependentJobs_HasStateDownloadSteps()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var buildJob = workflow.AddJob("build");
        var deployJob = workflow.AddJob("deploy");

        var buildStep = CreateStep("build-app", buildJob);
        var deployStep = CreateStep("deploy-app", deployJob, "build-app");

        var scheduling = SchedulingResolver.Resolve([buildStep, deployStep], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);

        // deploy job should download state from build
        var deployJobYaml = yaml.Jobs["deploy"];
        Assert.Contains(deployJobYaml.Steps, s =>
            s.Name == "Download state from build" &&
            s.Uses == "actions/download-artifact@v4");
    }

    [Fact]
    public void Generate_AllJobs_HaveStateUploadStep()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var buildJob = workflow.AddJob("build");
        var deployJob = workflow.AddJob("deploy");

        var buildStep = CreateStep("build-app", buildJob);
        var deployStep = CreateStep("deploy-app", deployJob, "build-app");

        var scheduling = SchedulingResolver.Resolve([buildStep, deployStep], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);

        foreach (var (_, jobYaml) in yaml.Jobs)
        {
            Assert.Contains(jobYaml.Steps, s =>
                s.Name == "Upload state" &&
                s.Uses == "actions/upload-artifact@v4");
        }
    }

    [Fact]
    public void Generate_JobRunsOn_MatchesJobConfiguration()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var buildJob = workflow.AddJob("build");
        buildJob.RunsOn = "windows-latest";

        var step = CreateStep("build-app", buildJob);

        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);

        Assert.Equal("windows-latest", yaml.Jobs["build"].RunsOn);
    }

    [Fact]
    public void Generate_JobDisplayName_IsPreserved()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var buildJob = workflow.AddJob("build");
        buildJob.DisplayName = "Build Application";

        var step = CreateStep("build-app", buildJob);

        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);

        Assert.Equal("Build Application", yaml.Jobs["build"].Name);
    }

    [Fact]
    public void Generate_DefaultTriggers_WorkflowDispatchAndPush()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var step = CreateStep("build-app");

        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);

        Assert.True(yaml.On.WorkflowDispatch);
        Assert.NotNull(yaml.On.Push);
        Assert.Contains("main", yaml.On.Push!.Branches);
    }

    [Fact]
    public void SerializeRoundTrip_ProducesValidYaml()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var buildJob = workflow.AddJob("build");
        var deployJob = workflow.AddJob("deploy");
        buildJob.DisplayName = "Build & Publish";

        var buildStep = CreateStep("build-app", buildJob);
        var deployStep = CreateStep("deploy-app", deployJob, "build-app");

        var scheduling = SchedulingResolver.Resolve([buildStep, deployStep], workflow);
        var yamlModel = WorkflowYamlGenerator.Generate(scheduling, workflow);
        var yamlString = WorkflowYamlSerializer.Serialize(yamlModel);

        // Verify key structural elements
        Assert.Contains("name: deploy", yamlString);
        Assert.Contains("workflow_dispatch:", yamlString);
        Assert.Contains("push:", yamlString);
        Assert.Contains("branches:", yamlString);
        Assert.Contains("- main", yamlString);
        Assert.Contains("  build:", yamlString);
        Assert.Contains("  deploy:", yamlString);
        Assert.Contains("needs:", yamlString);
        Assert.Contains("actions/checkout@v4", yamlString);
        Assert.Contains("actions/setup-dotnet@v4", yamlString);
        Assert.Contains("aspire do", yamlString);
        Assert.DoesNotContain("--job", yamlString);
        Assert.Contains("actions/upload-artifact@v4", yamlString);
        Assert.Contains("actions/download-artifact@v4", yamlString);
        Assert.Contains("'Build & Publish'", yamlString); // Quoted because of &
    }

    // Helpers

    private static PipelineStep CreateStep(string name, IPipelineStepTarget? scheduledBy = null, string[]? tags = null)
    {
        return new PipelineStep
        {
            Name = name,
            Action = _ => Task.CompletedTask,
            ScheduledBy = scheduledBy,
            Tags = tags is not null ? [.. tags] : []
        };
    }

    // Heuristic step emission tests

    [Fact]
    public void Generate_StepWithDotNetTag_EmitsSetupDotNet()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var step = CreateStep("build-app", tags: [WellKnownDependencyTags.DotNet]);

        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);

        var job = yaml.Jobs["default"];
        Assert.Contains(job.Steps, s => s.Name == "Setup .NET" && s.Uses == "actions/setup-dotnet@v4");
    }

    [Fact]
    public void Generate_StepWithNodeJsTag_EmitsSetupNode()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var step = CreateStep("build-ts", tags: [WellKnownDependencyTags.NodeJs]);

        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);

        var job = yaml.Jobs["default"];
        Assert.Contains(job.Steps, s => s.Name == "Setup Node.js" && s.Uses == "actions/setup-node@v4");
    }

    [Fact]
    public void Generate_StepWithAzureCliTag_EmitsAzureLogin()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var step = CreateStep("provision-infra", tags: [WellKnownDependencyTags.AzureCli]);

        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);

        var job = yaml.Jobs["default"];
        Assert.Contains(job.Steps, s => s.Name == "Azure login" && s.Uses == "azure/login@v2");
    }

    [Fact]
    public void Generate_StepWithoutNodeJsTag_DoesNotEmitSetupNode()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var step = CreateStep("build-app", tags: [WellKnownDependencyTags.DotNet]);

        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);

        var job = yaml.Jobs["default"];
        Assert.DoesNotContain(job.Steps, s => s.Name == "Setup Node.js");
    }

    [Fact]
    public void Generate_StepWithoutAzureCliTag_DoesNotEmitAzureLogin()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var step = CreateStep("build-app", tags: [WellKnownDependencyTags.DotNet]);

        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);

        var job = yaml.Jobs["default"];
        Assert.DoesNotContain(job.Steps, s => s.Name == "Azure login");
    }

    [Fact]
    public void Generate_MultipleTagsOnStep_EmitsAllSetupSteps()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var step = CreateStep("deploy-all", tags: [WellKnownDependencyTags.DotNet, WellKnownDependencyTags.NodeJs, WellKnownDependencyTags.AzureCli]);

        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);

        var job = yaml.Jobs["default"];
        Assert.Contains(job.Steps, s => s.Name == "Setup .NET");
        Assert.Contains(job.Steps, s => s.Name == "Setup Node.js");
        Assert.Contains(job.Steps, s => s.Name == "Azure login");
        Assert.Contains(job.Steps, s => s.Name == "Install Aspire CLI");
    }

    [Fact]
    public void Generate_TagsAcrossJobs_IndependentSetupSteps()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var buildJob = workflow.AddJob("build");
        var deployJob = workflow.AddJob("deploy");

        var buildStep = CreateStep("build-app", buildJob, tags: [WellKnownDependencyTags.DotNet, WellKnownDependencyTags.Docker]);
        var deployStep = new PipelineStep
        {
            Name = "deploy-app",
            Action = _ => Task.CompletedTask,
            DependsOnSteps = ["build-app"],
            ScheduledBy = deployJob,
            Tags = [WellKnownDependencyTags.AzureCli]
        };

        var scheduling = SchedulingResolver.Resolve([buildStep, deployStep], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);

        // Build job: has .NET setup but NOT Azure login
        var buildJobYaml = yaml.Jobs["build"];
        Assert.Contains(buildJobYaml.Steps, s => s.Name == "Setup .NET");
        Assert.DoesNotContain(buildJobYaml.Steps, s => s.Name == "Azure login");

        // Deploy job: has Azure login but does NOT need .NET for its own steps
        // (it still gets .NET because aspire do needs it)
        var deployJobYaml = yaml.Jobs["deploy"];
        Assert.Contains(deployJobYaml.Steps, s => s.Name == "Azure login");
    }

    // Channel-aware CLI install tests

    [Fact]
    public void Generate_NoConfig_UsesDefaultInstallScript()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var step = CreateStep("build-app");

        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow, repositoryRootDirectory: null);

        var job = yaml.Jobs["default"];
        var installStep = Assert.Single(job.Steps, s => s.Name == "Install Aspire CLI");
        // No config → default (stable) channel install
        Assert.Equal("curl -sSL https://aspire.dev/install.sh | bash", installStep.Run);
    }

    [Fact]
    public void Generate_UnknownChannel_FallsBackToDefault()
    {
        using var tempDir = new TempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "aspire.config.json"), """{"channel": "preview"}""");

        var workflow = new GitHubActionsWorkflowResource("deploy");
        var step = CreateStep("build-app");

        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow, tempDir.Path);

        var job = yaml.Jobs["default"];
        var installStep = Assert.Single(job.Steps, s => s.Name == "Install Aspire CLI");
        // "preview" is not a recognized channel — falls back to default (stable) install
        Assert.Equal("curl -sSL https://aspire.dev/install.sh | bash", installStep.Run);
    }

    [Fact]
    public void Generate_DailyChannel_UsesDailyQuality()
    {
        using var tempDir = new TempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "aspire.config.json"), """{"channel": "daily"}""");

        var workflow = new GitHubActionsWorkflowResource("deploy");
        var step = CreateStep("build-app");

        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow, tempDir.Path);

        var job = yaml.Jobs["default"];
        var installStep = Assert.Single(job.Steps, s => s.Name == "Install Aspire CLI");
        Assert.Contains("-q dev", installStep.Run);
    }

    [Fact]
    public void Generate_StableChannel_UsesDefaultInstallScript()
    {
        using var tempDir = new TempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "aspire.config.json"), """{"channel": "stable"}""");

        var workflow = new GitHubActionsWorkflowResource("deploy");
        var step = CreateStep("build-app");

        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow, tempDir.Path);

        var job = yaml.Jobs["default"];
        var installStep = Assert.Single(job.Steps, s => s.Name == "Install Aspire CLI");
        // Stable channel: default install (no -q flag)
        Assert.Equal("curl -sSL https://aspire.dev/install.sh | bash", installStep.Run);
    }

    [Fact]
    public void Generate_StagingChannel_UsesStagingQuality()
    {
        using var tempDir = new TempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "aspire.config.json"), """{"channel": "staging"}""");

        var workflow = new GitHubActionsWorkflowResource("deploy");
        var step = CreateStep("build-app");

        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow, tempDir.Path);

        var job = yaml.Jobs["default"];
        var installStep = Assert.Single(job.Steps, s => s.Name == "Install Aspire CLI");
        Assert.Contains("-q staging", installStep.Run);
    }

    [Fact]
    public void Generate_PrChannel_UsesPrInstallScript()
    {
        using var tempDir = new TempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "aspire.config.json"), """{"channel": "pr-15643"}""");

        var workflow = new GitHubActionsWorkflowResource("deploy");
        var step = CreateStep("build-app");

        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow, tempDir.Path);

        var job = yaml.Jobs["default"];
        var installStep = Assert.Single(job.Steps, s => s.Name == "Install Aspire CLI");
        Assert.Contains("get-aspire-cli-pr.sh", installStep.Run);
        Assert.Contains("15643", installStep.Run);
    }

    // ConfigureWorkflow callback tests

    [Fact]
    public void ConfigureWorkflow_CallbackModifiesYaml()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        workflow.Annotations.Add(new WorkflowCustomizationAnnotation(yaml =>
        {
            foreach (var job in yaml.Jobs.Values)
            {
                job.Steps.Insert(0, new StepYaml
                {
                    Name = "Custom step",
                    Run = "echo 'hello'"
                });
            }
        }));

        var step = CreateStep("build-app");
        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yamlModel = WorkflowYamlGenerator.Generate(scheduling, workflow);

        // Apply customization (simulating what the extension does)
        foreach (var customization in workflow.Annotations.OfType<WorkflowCustomizationAnnotation>())
        {
            customization.Callback(yamlModel);
        }

        var job = yamlModel.Jobs["default"];
        Assert.Equal("Custom step", job.Steps[0].Name);
    }

    [Fact]
    public void ConfigureWorkflow_MultipleCallbacks_AppliedInOrder()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        workflow.Annotations.Add(new WorkflowCustomizationAnnotation(yaml =>
        {
            foreach (var job in yaml.Jobs.Values)
            {
                job.Steps.Add(new StepYaml { Name = "First callback" });
            }
        }));
        workflow.Annotations.Add(new WorkflowCustomizationAnnotation(yaml =>
        {
            foreach (var job in yaml.Jobs.Values)
            {
                job.Steps.Add(new StepYaml { Name = "Second callback" });
            }
        }));

        var step = CreateStep("build-app");
        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yamlModel = WorkflowYamlGenerator.Generate(scheduling, workflow);

        foreach (var customization in workflow.Annotations.OfType<WorkflowCustomizationAnnotation>())
        {
            customization.Callback(yamlModel);
        }

        var job = yamlModel.Jobs["default"];
        var lastTwo = job.Steps.TakeLast(2).ToArray();
        Assert.Equal("First callback", lastTwo[0].Name);
        Assert.Equal("Second callback", lastTwo[1].Name);
    }

    [Fact]
    public void ConfigureWorkflow_CanAddEnvVarsToAllJobs()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        workflow.Annotations.Add(new WorkflowCustomizationAnnotation(yaml =>
        {
            foreach (var job in yaml.Jobs.Values)
            {
                job.Steps.Add(new StepYaml
                {
                    Name = "Secret step",
                    Env = new Dictionary<string, string>
                    {
                        ["MY_SECRET"] = "${{ secrets.MY_SECRET }}"
                    },
                    Run = "echo $MY_SECRET"
                });
            }
        }));

        var step = CreateStep("build-app");
        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yamlModel = WorkflowYamlGenerator.Generate(scheduling, workflow);

        foreach (var customization in workflow.Annotations.OfType<WorkflowCustomizationAnnotation>())
        {
            customization.Callback(yamlModel);
        }

        var job = yamlModel.Jobs["default"];
        var secretStep = Assert.Single(job.Steps, s => s.Name == "Secret step");
        Assert.Contains("MY_SECRET", secretStep.Env!.Keys);
    }

    // Helper for creating steps with tags and dependsOn
    private static PipelineStep CreateStep(string name, IPipelineStepTarget? scheduledBy, string dependsOn, params string[] moreDependsOn)
    {
        var deps = new List<string> { dependsOn };
        deps.AddRange(moreDependsOn);
        return new PipelineStep
        {
            Name = name,
            Action = _ => Task.CompletedTask,
            DependsOnSteps = deps,
            ScheduledBy = scheduledBy
        };
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
