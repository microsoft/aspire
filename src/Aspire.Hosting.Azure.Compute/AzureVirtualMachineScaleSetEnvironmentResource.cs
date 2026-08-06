// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;
using Aspire.Hosting.Pipelines;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Virtual Machine Scale Set compute environment.
/// </summary>
/// <remarks>
/// This resource is an experimental capability proof that publishes one .NET project as a
/// self-contained <c>linux-x64</c> application and deploys it through Azure VM Applications.
/// </remarks>
[Experimental("ASPIREAZURE004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class AzureVirtualMachineScaleSetEnvironmentResource : AzureBicepResource, IAzureComputeEnvironmentResource
{
    private const string BuildPackageStepTag = "build-vm-application-package";
    private const string UploadPackageStepTag = "upload-vm-application-package";

#pragma warning disable ASPIRECOMPUTE002
    bool IComputeEnvironmentResource.AllowsImplicitBinding => false;

    int IComputeEnvironmentResource.MinimumResourceCount => 1;

    int? IComputeEnvironmentResource.MaximumResourceCount => 1;

    bool IComputeEnvironmentResource.SupportsResource(IComputeResource resource) => resource is DotnetProjectResource;

    bool IComputeEnvironmentResource.UsesContainerImages(IComputeResource resource) => false;
#pragma warning restore ASPIRECOMPUTE002

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureVirtualMachineScaleSetEnvironmentResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="foundation">The Azure resources used to stage and distribute the VM Application package.</param>
    internal AzureVirtualMachineScaleSetEnvironmentResource(
        string name,
        AzureVirtualMachineScaleSetFoundationResource foundation)
        : base(name, templateResourceName: "Aspire.Hosting.Azure.Compute.AzureVirtualMachineScaleSetEnvironment.bicep")
    {
        Foundation = foundation;
        ApplicationPackageUri = foundation.PackageUri;

        Annotations.Add(new PipelineStepAnnotation(_ =>
        {
            if (!UsesGeneratedApplicationPackage)
            {
                return [];
            }

            var buildPackageStep = new PipelineStep
            {
                Name = $"build-{name}-vm-application-package",
                Description = $"Publishes and packages the .NET project bound to {name}.",
                Action = BuildApplicationPackageAsync,
                Tags = [WellKnownPipelineTags.BuildCompute, BuildPackageStepTag],
                DependsOnSteps = [WellKnownPipelineSteps.DeployPrereq],
                Resource = this
            };
            var uploadPackageStep = new PipelineStep
            {
                Name = $"upload-{name}-vm-application-package",
                Description = $"Uploads the VM Application package for {name}.",
                Action = UploadApplicationPackageAsync,
                Tags = [UploadPackageStepTag],
                Resource = this
            };

            return [buildPackageStep, uploadPackageStep];
        }));

        Annotations.Add(new PipelineConfigurationAnnotation(context =>
        {
            var project = context.Model.Resources
                .OfType<DotnetProjectResource>()
                .SingleOrDefault(resource => ReferenceEquals(resource.GetComputeEnvironment(), this));
            if (project is null)
            {
                return;
            }

            var rolloutProvisionSteps = context.GetSteps(this, WellKnownPipelineTags.ProvisionInfrastructure);
            var prerequisiteResources = project.Annotations
                .OfType<DeploymentPrerequisitesAnnotation>()
                .SelectMany(annotation => annotation.Resources)
                .Distinct()
                .ToArray();
            foreach (var prerequisite in prerequisiteResources)
            {
                References.Add(prerequisite);
                rolloutProvisionSteps.DependsOn(
                    context.GetSteps(prerequisite, WellKnownPipelineTags.ProvisionInfrastructure));
            }

            if (!UsesGeneratedApplicationPackage)
            {
                return;
            }

            var buildPackageSteps = context.GetSteps(this, BuildPackageStepTag);
            var uploadPackageSteps = context.GetSteps(this, UploadPackageStepTag);
            var foundationProvisionSteps = context.GetSteps(Foundation, WellKnownPipelineTags.ProvisionInfrastructure);
            var workloadIdentity = WorkloadIdentity ??
                throw new InvalidOperationException(
                    $"The Azure Virtual Machine Scale Set compute environment '{Name}' requires a workload identity.");
            var identityProvisionSteps = context.GetSteps(workloadIdentity, WellKnownPipelineTags.ProvisionInfrastructure);

            buildPackageSteps.DependsOn(identityProvisionSteps);
            foreach (var prerequisite in prerequisiteResources)
            {
                buildPackageSteps.DependsOn(
                    context.GetSteps(prerequisite, WellKnownPipelineTags.ProvisionInfrastructure));
            }
            uploadPackageSteps.DependsOn(buildPackageSteps);
            uploadPackageSteps.DependsOn(foundationProvisionSteps);
            rolloutProvisionSteps.DependsOn(uploadPackageSteps);
        }));
    }

    internal AzureVirtualMachineScaleSetFoundationResource Foundation { get; }

    internal string VmSku { get; set; } = "Standard_D2s_v6";

    internal int Capacity { get; set; } = 2;

    internal string ImagePublisher { get; set; } = "Canonical";

    internal string ImageOffer { get; set; } = "ubuntu-24_04-lts";

    internal string ImageSku { get; set; } = "server";

    internal string ImageVersion { get; set; } = "latest";

    internal AzureSubnetResource? Subnet { get; set; }

    internal object? ApplicationPackageUri { get; set; }

    internal bool UsesGeneratedApplicationPackage { get; set; } = true;

    internal string ApplicationVersion { get; set; } = "1.0.0";

    internal string AdminSshPublicKey { get; set; } = string.Empty;

    internal AzureUserAssignedIdentityResource? WorkloadIdentity { get; set; }

    internal string? GeneratedApplicationPackagePath { get; private set; }

    internal string? GeneratedApplicationPackageFingerprint { get; private set; }

    /// <inheritdoc/>
    public override BicepTemplateFile GetBicepTemplateFile(string? directory = null, bool deleteTemporaryFileOnDispose = true)
    {
        EnsureTemplateParameters();

        return base.GetBicepTemplateFile(directory, deleteTemporaryFileOnDispose);
    }

    /// <inheritdoc/>
    public override string GetBicepTemplateString()
    {
        EnsureTemplateParameters();

        return base.GetBicepTemplateString();
    }

    /// <inheritdoc/>
    public ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference)
        => throw new NotSupportedException("Ingress for Azure Virtual Machine Scale Set compute environments is not implemented yet.");

    private void EnsureTemplateParameters()
    {
        if (Subnet is null)
        {
            throw new InvalidOperationException($"The Azure Virtual Machine Scale Set compute environment '{Name}' requires an Azure subnet.");
        }

        if (ApplicationPackageUri is null)
        {
            throw new InvalidOperationException($"The Azure Virtual Machine Scale Set compute environment '{Name}' requires a VM Application package.");
        }

        if (string.IsNullOrWhiteSpace(AdminSshPublicKey))
        {
            throw new InvalidOperationException($"The Azure Virtual Machine Scale Set compute environment '{Name}' requires an administrator SSH public key.");
        }

        if (WorkloadIdentity is null)
        {
            throw new InvalidOperationException($"The Azure Virtual Machine Scale Set compute environment '{Name}' requires a workload identity.");
        }

        Parameters["resourceName"] = Name;
        Parameters["vmSku"] = VmSku;
        Parameters["capacity"] = Capacity.ToString(CultureInfo.InvariantCulture);
        Parameters["imagePublisher"] = ImagePublisher;
        Parameters["imageOffer"] = ImageOffer;
        Parameters["imageSku"] = ImageSku;
        Parameters["imageVersion"] = ImageVersion;
        Parameters["subnetId"] = Subnet.Id;
        Parameters["vmApplicationPackageUri"] = ApplicationPackageUri;
        Parameters["vmApplicationVersion"] = ApplicationVersion;
        Parameters["adminSshPublicKey"] = AdminSshPublicKey;
        Parameters["workloadIdentityName"] = WorkloadIdentity.NameOutputReference;
        Parameters["galleryName"] = Foundation.GalleryName;
        Parameters["galleryApplicationName"] = Foundation.GalleryApplicationName;
    }

    private async Task BuildApplicationPackageAsync(PipelineStepContext context)
    {
        var project = context.Model.Resources
            .OfType<DotnetProjectResource>()
            .SingleOrDefault(resource => ReferenceEquals(resource.GetComputeEnvironment(), this))
            ?? throw new InvalidOperationException(
                $"The Azure Virtual Machine Scale Set compute environment '{Name}' requires exactly one bound .NET project.");

        var workloadIdentity = WorkloadIdentity ??
            throw new InvalidOperationException(
                $"The Azure Virtual Machine Scale Set compute environment '{Name}' requires a workload identity.");
        var package = await VirtualMachineScaleSetApplicationPackageBuilder.BuildAsync(
            project,
            this,
            context.ExecutionContext,
            workloadIdentity.ClientId,
            context.Services.GetRequiredService<IFileSystemService>(),
            context.Services.GetRequiredService<IAspireStore>(),
            context.Logger,
            context.CancellationToken).ConfigureAwait(false);

        GeneratedApplicationPackagePath = package.Path;
        GeneratedApplicationPackageFingerprint = package.Fingerprint;
        ApplicationVersion = package.Version;
        Parameters["vmApplicationVersion"] = package.Version;

        context.Summary.Add($"{Name} package", $"{Path.GetFileName(package.Path)} ({package.Fingerprint[..12]})");
    }

    private async Task UploadApplicationPackageAsync(PipelineStepContext context)
    {
        if (GeneratedApplicationPackagePath is not { } packagePath ||
            GeneratedApplicationPackageFingerprint is not { } fingerprint)
        {
            throw new InvalidOperationException(
                $"The VM Application package for compute environment '{Name}' has not been built.");
        }

        var packageUriValue = await Foundation.PackageUri.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
        if (!Uri.TryCreate(packageUriValue, UriKind.Absolute, out var packageUri))
        {
            throw new InvalidOperationException(
                $"The package Storage foundation for compute environment '{Name}' did not produce a valid package URI.");
        }

        var credential = context.Services.GetRequiredService<ITokenCredentialProvider>().TokenCredential;
        var timeProvider = context.Services.GetRequiredService<TimeProvider>();
        var blobClient = new BlobClient(packageUri, credential);
        var uploadOptions = CreatePackageUploadOptions(fingerprint, ApplicationVersion);

        const int maximumAttempts = 10;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var packageStream = File.OpenRead(packagePath);
                await blobClient.UploadAsync(
                    packageStream,
                    uploadOptions,
                    context.CancellationToken).ConfigureAwait(false);
                break;
            }
            catch (RequestFailedException exception) when (
                exception.Status is 403 or 404 &&
                attempt < maximumAttempts &&
                !context.CancellationToken.IsCancellationRequested)
            {
                // Azure Storage data-plane RBAC and container visibility can lag ARM completion.
                var backoffSeconds = Math.Min(2 << (attempt - 1), 30);
                var jitterMilliseconds = Random.Shared.Next(0, 2_001);
                var delay = TimeSpan.FromSeconds(backoffSeconds) + TimeSpan.FromMilliseconds(jitterMilliseconds);
                context.Logger.LogDebug(
                    "Waiting {Delay} for package Storage access for compute environment {ComputeEnvironment} (attempt {Attempt}/{MaximumAttempts}).",
                    delay,
                    Name,
                    attempt,
                    maximumAttempts);
                await Task.Delay(delay, timeProvider, context.CancellationToken).ConfigureAwait(false);
            }
        }

        context.Summary.Add($"{Name} package upload", packageUri.GetLeftPart(UriPartial.Path));
    }

    internal static BlobUploadOptions CreatePackageUploadOptions(string fingerprint, string applicationVersion)
    {
        return new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders
            {
                ContentType = "application/gzip"
            },
            Metadata = new Dictionary<string, string>
            {
                ["aspire-fingerprint"] = fingerprint,
                ["aspire-version"] = applicationVersion
            }
        };
    }
}
