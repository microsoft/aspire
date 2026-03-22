// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Primitives;
using Azure.Provisioning.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents a context for publishing Azure bicep templates for a distributed application.
/// </summary>
/// <remarks>
/// This context facilitates the generation of bicep templates using the provided application model,
/// publisher options, and execution context. It handles resource configuration and ensures
/// that the bicep template is created in the specified output path.
/// </remarks>
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class AzurePublishingContext(
    string outputPath,
    AzureProvisioningOptions provisioningOptions,
    IServiceProvider serviceProvider,
    ILogger logger,
    IReportingStep reportingStep)
{
    private ILogger Logger => logger;

    private IReportingStep ReportingStep => reportingStep;

    private IServiceProvider ServiceProvider => serviceProvider;

    /// <summary>
    /// Gets the main.bicep infrastructure for the distributed application.
    /// </summary>
    public Infrastructure MainInfrastructure { get; } = new()
    {
        TargetScope = DeploymentScope.Subscription
    };

    /// <summary>
    /// Gets a dictionary that maps parameter resources to provisioning parameters.
    /// </summary>
    /// <remarks>
    /// The value is the <see cref="ProvisioningParameter"/> of the <see cref="MainInfrastructure"/>
    /// that was created to be filled with the value of the Aspire <see cref="ParameterResource"/>.
    /// </remarks>
    public Dictionary<ParameterResource, ProvisioningParameter> ParameterLookup { get; } = [];

    /// <summary>
    /// Gets a dictionary that maps output references to provisioning outputs.
    /// </summary>
    /// <remarks>
    /// The value is the <see cref="ProvisioningOutput"/> of the <see cref="MainInfrastructure"/>
    /// that was created with the value of the output referenced by the Aspire <see cref="BicepOutputReference"/>.
    /// </remarks>
    public Dictionary<BicepOutputReference, ProvisioningOutput> OutputLookup { get; } = [];

    /// <summary>
    /// Gets a dictionary that provides direct lookup of output references based on bicep identifier.
    /// </summary>
    /// <remarks>
    /// This reverse lookup map allows efficient retrieval of <see cref="BicepOutputReference"/>
    /// instances using their corresponding bicep identifier strings.
    /// </remarks>
    internal Dictionary<string, BicepOutputReference> ReverseOutputLookup { get; } = [];

    /// <summary>
    /// Writes the specified distributed application model to the output path using Bicep templates.
    /// </summary>
    /// <param name="model">The distributed application model to write to the output path.</param>
    /// <param name="environment">The Azure environment resource.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the async operation.</returns>
    public async Task WriteModelAsync(DistributedApplicationModel model, AzureEnvironmentResource environment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(outputPath);

        if (model.Resources.Count == 0)
        {
            Logger.LogInformation("No resources found in the model");
            return;
        }

        var writeTask = await ReportingStep.CreateTaskAsync("Writing Azure Bicep templates", cancellationToken).ConfigureAwait(false);

        await using (writeTask.ConfigureAwait(false))
        {
            try
            {
                await WriteAzureArtifactsOutputAsync(ReportingStep, model, environment, cancellationToken).ConfigureAwait(false);

                await SaveToDiskAsync(outputPath).ConfigureAwait(false);

                await writeTask.SucceedAsync($"Azure Bicep templates written successfully to {outputPath}.", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await writeTask.FailAsync($"Failed to write Azure Bicep templates: {ex.Message}", cancellationToken).ConfigureAwait(false);

                Logger.LogError(ex, "Failed to write Azure Bicep templates to {OutputPath}", outputPath);
                throw;
            }
        }
    }

    private async Task WriteAzureArtifactsOutputAsync(IReportingStep step, DistributedApplicationModel model, AzureEnvironmentResource environment, CancellationToken cancellationToken)
    {
        var outputDirectory = new DirectoryInfo(outputPath);
        if (!outputDirectory.Exists)
        {
            outputDirectory.Create();
        }

        var fileSystemService = ServiceProvider.GetRequiredService<IFileSystemService>();
        var tempDirectory = fileSystemService.TempDirectory.CreateTempSubdirectory("aspire-bicep").Path;

        var bicepResourcesToPublish = model.Resources.OfType<AzureBicepResource>()
            .Where(r => !r.IsExcludedFromPublish())
            .ToList();

        await MapParameterAsync(environment.ResourceGroupName, cancellationToken).ConfigureAwait(false);
        await MapParameterAsync(environment.Location, cancellationToken).ConfigureAwait(false);
        await MapParameterAsync(environment.PrincipalId, cancellationToken).ConfigureAwait(false);

        var resourceGroupParam = ParameterLookup[environment.ResourceGroupName];
        MainInfrastructure.Add(resourceGroupParam);

        var locationParam = ParameterLookup[environment.Location];
        MainInfrastructure.Add(locationParam);

        var principalId = ParameterLookup[environment.PrincipalId];
        MainInfrastructure.Add(principalId);

        var rg = new ResourceGroup("rg")
        {
            Name = resourceGroupParam,
            Location = locationParam,
        };

        var moduleMap = new Dictionary<AzureBicepResource, ModuleImport>();

        foreach (var resource in bicepResourcesToPublish)
        {
            var file = resource.GetBicepTemplateFile(tempDirectory);

            var moduleDirectory = outputDirectory.CreateSubdirectory(resource.Name);

            var modulePath = Path.Combine(moduleDirectory.FullName, $"{resource.Name}.bicep");

            File.Copy(file.Path, modulePath, true);

            var identifier = Infrastructure.NormalizeBicepIdentifier(resource.Name);

            var module = new ModuleImport(identifier, $"{resource.Name}/{resource.Name}.bicep")
            {
                Name = resource.Name
            };

            moduleMap[resource] = module;
        }

        foreach (var resource in bicepResourcesToPublish)
        {
            // Map parameters from existing resources
            if (resource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingAnnotation))
            {
                await VisitAsync(existingAnnotation.ResourceGroup, MapParameterAsync, cancellationToken).ConfigureAwait(false);
                await VisitAsync(existingAnnotation.Name, MapParameterAsync, cancellationToken).ConfigureAwait(false);
            }

            // Map parameters for the resource itself
            foreach (var parameter in resource.Parameters)
            {
                await VisitAsync(parameter.Value, MapParameterAsync, cancellationToken).ConfigureAwait(false);
            }
        }

        static BicepValue<string> GetOutputs(ModuleImport module, string outputName) =>
            new MemberExpression(new MemberExpression(new IdentifierExpression(module.BicepIdentifier), "outputs"), outputName);

        FormattableString EvalExpr(ReferenceExpression expr)
        {
            var args = new object[expr.ValueProviders.Count];

            for (var i = 0; i < expr.ValueProviders.Count; i++)
            {
                args[i] = Eval(expr.ValueProviders[i]);
            }

            return FormattableStringFactory.Create(expr.Format, args);
        }

        object EvalConditionalExpr(ReferenceExpression expr)
        {
            var conditionVal = Eval(expr.Condition!);

            // If the condition resolves to a static string, evaluate at publish time
            if (conditionVal is string staticCondition)
            {
                var branch = string.Equals(staticCondition, expr.MatchValue, StringComparison.OrdinalIgnoreCase)
                    ? expr.WhenTrue!
                    : expr.WhenFalse!;
                return Eval(branch);
            }

            // Condition is a Bicep parameter/output — emit a ternary expression
            var whenTrueVal = ResolveValue(Eval(expr.WhenTrue!));
            var whenFalseVal = ResolveValue(Eval(expr.WhenFalse!));

            var conditional = new ConditionalExpression(
                new BinaryExpression(ResolveValue(conditionVal).Compile(), BinaryBicepOperator.Equal, new StringLiteralExpression(expr.MatchValue!)),
                whenTrueVal.Compile(),
                whenFalseVal.Compile());

            return new BicepValue<string>(conditional);
        }

        object Eval(object? value) => value switch
        {
            BicepOutputReference b => GetOutputs(moduleMap[b.Resource], b.Name),
            ParameterResource p => ParameterLookup[p],
            ConnectionStringReference r => Eval(r.Resource.ConnectionStringExpression),
            IResourceWithConnectionString cs => Eval(cs.ConnectionStringExpression),
            ReferenceExpression { IsConditional: true } re => EvalConditionalExpr(re),
            ReferenceExpression re => EvalExpr(re),
            string s => s,
            _ => ""
        };

        static BicepValue<string> ResolveValue(object val)
        {
            return val switch
            {
                BicepValue<string> s => s,
                string s => s,
                ProvisioningParameter p => p,
                FormattableString fs => BicepFunction.Interpolate(fs),
                _ => throw new NotSupportedException("Unsupported value type " + val.GetType())
            };
        }

        var computeEnvironments = new List<IAzureComputeEnvironmentResource>();

        var computeEnvironmentTask = await step.CreateTaskAsync(
            "Analyzing model for compute environments.",
            cancellationToken: cancellationToken
            ).ConfigureAwait(false);

        foreach (var resource in bicepResourcesToPublish)
        {
            if (resource is IAzureComputeEnvironmentResource computeEnvironment)
            {
                computeEnvironments.Add(computeEnvironment);
            }

            var task = await step.CreateTaskAsync(
                $"Processing Azure resource {resource.Name}",
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);

            BicepValue<string> scope = resource.Scope?.ResourceGroup switch
            {
                string rgName => new FunctionCallExpression(new IdentifierExpression("resourceGroup"), new StringLiteralExpression(rgName)),
                ParameterResource p => new FunctionCallExpression(new IdentifierExpression("resourceGroup"), ParameterLookup[p].Value.Compile()),
                _ => new IdentifierExpression(rg.BicepIdentifier)
            };

            var module = moduleMap[resource];
            module.Scope = scope;
            module.Parameters.Add("location", locationParam);

            foreach (var parameter in resource.Parameters)
            {
                if (parameter.Key == AzureBicepResource.KnownParameters.UserPrincipalId && parameter.Value is null)
                {
                    module.Parameters.Add(parameter.Key, principalId);
                    continue;
                }
                if (parameter.Key == AzureBicepResource.KnownParameters.PrincipalId && parameter.Value is null)
                {
                    module.Parameters.Add(parameter.Key, principalId);
                    continue;
                }

                var value = ResolveValue(Eval(parameter.Value));

                module.Parameters.Add(parameter.Key, value);
            }

            await task.SucceedAsync(
                $"Wrote bicep module for resource {resource.Name} to {module.Path}",
                cancellationToken: cancellationToken
            ).ConfigureAwait(false);
        }

        var (message, state) = computeEnvironments.Count switch
        {
            0 => ("No azure compute environments found in the model.", CompletionState.CompletedWithWarning),
            _ => ($"Found {computeEnvironments.Count} compute environment(s) in the model.", CompletionState.Completed)
        };

        // Report the completion of the compute environment task.
        await computeEnvironmentTask.CompleteAsync(message, state, cancellationToken).ConfigureAwait(false);

        var outputs = new Dictionary<string, BicepOutputReference>();

        void CaptureBicepOutputs(object value)
        {
            if (value is BicepOutputReference bo)
            {
                outputs[bo.ValueExpression] = bo;
            }
        }

        void CaptureBicepOutputsFromParameters(IResourceWithParameters resource)
        {
            foreach (var parameter in resource.Parameters)
            {
                Visit(parameter.Value, CaptureBicepOutputs);
            }
        }

        // Capture any bicep outputs referenced from resources outside of the MainInfrastructure.
        // These include DeploymentTarget resources and any other resources that have parameters that reference bicep outputs.
        foreach (var resource in model.Resources)
        {
            if (resource.GetDeploymentTargetAnnotation() is { } annotation && annotation.DeploymentTarget is AzureBicepResource br)
            {
                // Materialize Dockerfile factory if present
                if (resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var dockerfileBuildAnnotation) &&
                    dockerfileBuildAnnotation.DockerfileFactory is not null)
                {
                    var context = new DockerfileFactoryContext
                    {
                        Services = ServiceProvider,
                        Resource = resource,
                        CancellationToken = cancellationToken
                    };
                    await dockerfileBuildAnnotation.MaterializeDockerfileAsync(context, cancellationToken).ConfigureAwait(false);

                    // Copy to a resource-specific path in the output folder for publishing
                    var resourceDockerfilePath = Path.Combine(outputPath, $"{resource.Name}.Dockerfile");
                    Directory.CreateDirectory(outputPath);
                    File.Copy(dockerfileBuildAnnotation.DockerfilePath, resourceDockerfilePath, overwrite: true);
                }

                var task = await step.CreateTaskAsync(
                    $"Processing deployment target {resource.Name}",
                    cancellationToken: default
                )
                .ConfigureAwait(false);

                var moduleDirectory = outputDirectory.CreateSubdirectory(resource.Name);

                var modulePath = Path.Combine(moduleDirectory.FullName, $"{resource.Name}.bicep");

                var file = br.GetBicepTemplateFile(tempDirectory);

                File.Copy(file.Path, modulePath, true);

                CaptureBicepOutputsFromParameters(br);

                await task.SucceedAsync(
                    $"Wrote bicep module for deployment target {resource.Name} to {modulePath}",
                    cancellationToken: default
                ).ConfigureAwait(false);
            }
            else if (resource is IResourceWithParameters rwp && !bicepResourcesToPublish.Contains(resource))
            {
                CaptureBicepOutputsFromParameters(rwp);
            }
        }

        foreach (var (_, pp) in ParameterLookup)
        {
            MainInfrastructure.Add(pp);
        }

        MainInfrastructure.Add(rg);

        foreach (var (_, module) in moduleMap)
        {
            MainInfrastructure.Add(module);
        }

        foreach (var (_, output) in outputs)
        {
            var module = moduleMap[output.Resource];

            var identifier = Infrastructure.NormalizeBicepIdentifier($"{output.Resource.Name}_{output.Name}");

            var bicepOutput = new ProvisioningOutput(identifier, typeof(string))
            {
                Value = GetOutputs(module, output.Name)
            };

            OutputLookup[output] = bicepOutput;
            ReverseOutputLookup[bicepOutput.BicepIdentifier] = output;
            MainInfrastructure.Add(bicepOutput);
        }
    }

    private async Task MapParameterAsync(object candidate, CancellationToken cancellationToken = default)
    {
        if (candidate is ParameterResource p && !ParameterLookup.ContainsKey(p))
        {
            var pid = Infrastructure.NormalizeBicepIdentifier(p.Name);

            var pp = new ProvisioningParameter(pid, typeof(string))
            {
                IsSecure = p.Secret
            };

            if (!p.Secret && p.Default is not null)
            {
                var value = await p.GetValueAsync(cancellationToken).ConfigureAwait(false);
                if (value is not null)
                {
                    pp.Value = value;
                }
            }

            ParameterLookup[p] = pp;
        }
    }

    private static void Visit(object? value, Action<object> visitor) =>
        Visit(value, visitor, []);

    private static void Visit(object? value, Action<object> visitor, HashSet<object> visited)
    {
        if (value is null || !visited.Add(value))
        {
            return;
        }

        visitor(value);

        if (value is IValueWithReferences vwr)
        {
            foreach (var reference in vwr.References)
            {
                Visit(reference, visitor, visited);
            }
        }
    }

    private static Task VisitAsync(object? value, Func<object, CancellationToken, Task> visitor, CancellationToken cancellationToken = default) =>
        VisitAsync(value, visitor, [], cancellationToken);

    private static async Task VisitAsync(object? value, Func<object, CancellationToken, Task> visitor, HashSet<object> visited, CancellationToken cancellationToken = default)
    {
        if (value is null || !visited.Add(value))
        {
            return;
        }

        await visitor(value, cancellationToken).ConfigureAwait(false);

        if (value is IValueWithReferences vwr)
        {
            foreach (var reference in vwr.References)
            {
                await VisitAsync(reference, visitor, visited, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Saves the compiled Bicep template to disk.
    /// </summary>
    /// <param name="outputDirectoryPath">The path to the output directory where the Bicep template will be saved.</param>
    /// <returns>A task that represents the asynchronous save operation.</returns>
    private async Task SaveToDiskAsync(string outputDirectoryPath)
    {
        var plan = MainInfrastructure.Build(provisioningOptions.ProvisioningBuildOptions);
        var compiledBicep = plan.Compile().First();

        logger.LogDebug("Writing Bicep module {BicepName}.bicep to {TargetPath}", MainInfrastructure.BicepName, outputDirectoryPath);

        var bicepPath = Path.Combine(outputDirectoryPath, $"{MainInfrastructure.BicepName}.bicep");
        await File.WriteAllTextAsync(bicepPath, compiledBicep.Value).ConfigureAwait(false);
    }
}
