// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003, ASPIREAZURE004
#pragma warning disable ASPIREDOTNETPROJECT001
#pragma warning disable ASPIREFILESYSTEM001
#pragma warning disable ASPIREPIPELINES001

using System.Formats.Tar;
using System.IO.Compression;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Azure.Tests;

public class AzureVirtualMachineScaleSetEnvironmentTests
{
    [Fact]
    public async Task GeneratesVmssGalleryApplicationInfrastructure()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var virtualNetwork = builder.AddAzureVirtualNetwork("network");
        var subnet = virtualNetwork.AddSubnet("compute-subnet", "10.0.0.0/24");
        var environment = builder.AddAzureVirtualMachineScaleSetEnvironment("compute")
            .WithPlatformImage("Canonical", "ubuntu-24_04-lts", "server", "latest")
            .WithVmSku("Standard_D2s_v5")
            .WithCapacity(2)
            .WithSubnet(subnet)
            .WithAdminSshPublicKey("ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQCapabilityProof")
            .WithVmApplicationPackage(new Uri("https://account.blob.core.windows.net/applications/app-001.tar.gz"), "1.2.3");

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(environment.Resource, skipPreparer: true);

        Assert.IsType<string>(environment.Resource.Parameters["capacity"]);
        Assert.Equal("2", environment.Resource.Parameters["capacity"]);
        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task GeneratesPrivatePackageStorageAndGalleryFoundation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureVirtualMachineScaleSetEnvironment("compute");

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(
            environment.Resource.Foundation,
            skipPreparer: true);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1.0.latest")]
    [InlineData("-1.0.0")]
    public void RejectsInvalidVmApplicationVersion(string version)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var environment = builder.AddAzureVirtualMachineScaleSetEnvironment("compute");

        var exception = Assert.Throws<ArgumentException>(() =>
            environment.WithVmApplicationPackage(new Uri("https://account.blob.core.windows.net/applications/app.tar.gz"), version));

        Assert.Equal("version", exception.ParamName);
    }

    [Fact]
    public void RejectsPackageUriWithQueryString()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var environment = builder.AddAzureVirtualMachineScaleSetEnvironment("compute");

        var exception = Assert.Throws<ArgumentException>(() =>
            environment.WithVmApplicationPackage(new Uri("https://account.blob.core.windows.net/applications/app.tar.gz?sig=secret"), "1.2.3"));

        Assert.Equal("packageUri", exception.ParamName);
        Assert.StartsWith("The VM Application package URI cannot contain a query string.", exception.Message);
    }

    [Theory]
    [InlineData(
        "http://account.blob.core.windows.net/applications/app.tar.gz",
        "The VM Application package URI must use HTTPS.")]
    [InlineData(
        "https://user:password@account.blob.core.windows.net/applications/app.tar.gz",
        "The VM Application package URI cannot contain user information.")]
    [InlineData(
        "https://account.blob.core.windows.net/applications/app.tar.gz#fragment",
        "The VM Application package URI cannot contain a fragment.")]
    [InlineData(
        "https://storage.example.test/applications/app.tar.gz",
        "The VM Application package URI must identify a blob in Azure Storage.")]
    [InlineData(
        "https://invalid.account.blob.core.windows.net/applications/app.tar.gz",
        "The VM Application package URI must identify a blob in Azure Storage.")]
    [InlineData(
        "https://account.blob.core.windows.net/applications",
        "The VM Application package URI must identify a blob in Azure Storage.")]
    public void RejectsInvalidPackageUri(string packageUri, string expectedMessage)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var environment = builder.AddAzureVirtualMachineScaleSetEnvironment("compute");

        var exception = Assert.Throws<ArgumentException>(() =>
            environment.WithVmApplicationPackage(new Uri(packageUri), "1.2.3"));

        Assert.Equal("packageUri", exception.ParamName);
        Assert.StartsWith(expectedMessage, exception.Message);
    }

    [Theory]
    [InlineData("https://account.blob.core.windows.net/applications/app.tar.gz")]
    [InlineData("https://account.blob.core.usgovcloudapi.net/applications/app.tar.gz")]
    [InlineData("https://account.blob.core.chinacloudapi.cn/applications/app.tar.gz")]
    [InlineData("https://account.blob.core.cloudapi.de/applications/app.tar.gz")]
    public void AcceptsAzureBlobPackageUris(string packageUri)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var environment = builder.AddAzureVirtualMachineScaleSetEnvironment("compute");

        environment.WithVmApplicationPackage(new Uri(packageUri), "1.2.3");

        Assert.Equal(packageUri, environment.Resource.ApplicationPackageUri);
        Assert.Equal(false, environment.Resource.Foundation.Parameters["provisionPackageStorage"]);
    }

    [Fact]
    public void AcceptsSecretPackageUriParameter()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var packageUri = builder.AddParameter("package-uri", secret: true);
        var environment = builder.AddAzureVirtualMachineScaleSetEnvironment("compute");

        environment.WithVmApplicationPackage(packageUri, "1.2.3");

        Assert.Same(packageUri.Resource, environment.Resource.ApplicationPackageUri);
        Assert.Same(packageUri.Resource, environment.Resource.Parameters["vmApplicationPackageUri"]);
        Assert.Equal(false, environment.Resource.Foundation.Parameters["provisionPackageStorage"]);
    }

    [Fact]
    public void RejectsNonSecretPackageUriParameter()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var packageUri = builder.AddParameter("package-uri");
        var environment = builder.AddAzureVirtualMachineScaleSetEnvironment("compute");

        var exception = Assert.Throws<ArgumentException>(() =>
            environment.WithVmApplicationPackage(packageUri, "1.2.3"));

        Assert.Equal("packageUri", exception.ParamName);
        Assert.StartsWith("The VM Application package URI parameter must be secret.", exception.Message);
    }

    [Fact]
    public void RequiresSubnet()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var environment = builder.AddAzureVirtualMachineScaleSetEnvironment("compute");

        var exception = Assert.Throws<InvalidOperationException>(() => environment.Resource.GetBicepTemplateFile());

        Assert.Equal("The Azure Virtual Machine Scale Set compute environment 'compute' requires an Azure subnet.", exception.Message);
    }

    [Fact]
    public void UsesGeneratedVmApplicationPackageByDefault()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var subnet = builder.AddAzureVirtualNetwork("network").AddSubnet("compute-subnet", "10.0.0.0/24");
        var environment = builder.AddAzureVirtualMachineScaleSetEnvironment("compute")
            .WithSubnet(subnet)
            .WithAdminSshPublicKey("ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQCapabilityProof");

        _ = environment.Resource.GetBicepTemplateString();

        Assert.True(environment.Resource.UsesGeneratedApplicationPackage);
        Assert.Equal("Standard_D2s_v6", environment.Resource.VmSku);
        Assert.Same(environment.Resource.Foundation.PackageUri, environment.Resource.ApplicationPackageUri);
        Assert.Same(environment.Resource.Foundation.PackageUri, environment.Resource.Parameters["vmApplicationPackageUri"]);
    }

    [Fact]
    public void RequiresAdminSshPublicKey()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var subnet = builder.AddAzureVirtualNetwork("network").AddSubnet("compute-subnet", "10.0.0.0/24");
        var environment = builder.AddAzureVirtualMachineScaleSetEnvironment("compute")
            .WithSubnet(subnet)
            .WithVmApplicationPackage(new Uri("https://account.blob.core.windows.net/applications/app.tar.gz"), "1.2.3");

        var exception = Assert.Throws<InvalidOperationException>(() => environment.Resource.GetBicepTemplateFile());

        Assert.Equal("The Azure Virtual Machine Scale Set compute environment 'compute' requires an administrator SSH public key.", exception.Message);
    }

    [Fact]
    public async Task AcceptsOneExplicitlyBoundDotnetProject()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = AddConfiguredEnvironment(builder);
        var blobs = builder.AddAzureStorage("storage").AddBlobs("blobs");
        var app = builder.AddDotnetProject("app", "app.csproj", options => options.ExcludeLaunchProfile = true)
            .WithReference(blobs)
            .WithComputeEnvironment(environment);

        using var application = builder.Build();

        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(application, default);

        Assert.Same(environment.Resource, app.Resource.GetComputeEnvironment());
        Assert.True(app.Resource.TryGetLastAnnotation<AppIdentityAnnotation>(out var appIdentity));
        var identity = Assert.IsType<AzureUserAssignedIdentityResource>(appIdentity.IdentityResource);
        Assert.Equal("compute-identity", identity.Name);

        var model = application.Services.GetRequiredService<DistributedApplicationModel>();
        var roleAssignments = Assert.Single(
            model.Resources.OfType<AzureRoleAssignmentResource>(),
            resource => ReferenceEquals(resource.OwnerResource, app.Resource));
        Assert.Same(identity, roleAssignments.IdentityResource);
    }

    [Fact]
    public void BoundDotnetProjectDoesNotRequireAContainerImage()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureVirtualMachineScaleSetEnvironment("compute");
        var app = builder.AddDotnetProject("app", "app.csproj", options => options.ExcludeLaunchProfile = true)
            .WithComputeEnvironment(environment);

        Assert.False(app.Resource.RequiresImageBuild());
        Assert.False(app.Resource.RequiresImageBuildAndPush());
    }

    [Fact]
    public async Task GeneratedPackagePipelineOrdersBuildFoundationUploadAndRollout()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = AddGeneratedPackageEnvironment(builder);
        var prerequisite = builder.AddAzureStorage("storage");
        builder.AddDotnetProject("app", "app.csproj", options => options.ExcludeLaunchProfile = true)
            .WithAnnotation(new DeploymentPrerequisitesAnnotation(new HashSet<AzureBicepResource> { prerequisite.Resource }))
            .WithComputeEnvironment(environment);

        IReadOnlyList<PipelineStep>? resolvedSteps = null;
        environment.WithAnnotation(new PipelineConfigurationAnnotation(context =>
        {
            resolvedSteps = context.Steps;
        }));
        builder.Services.Configure<PipelineOptions>(options => options.Step = WellKnownPipelineSteps.ValidateComputeEnvironments);

        using var application = builder.Build();
        await ExecutePipelineAsync(application);

        Assert.NotNull(resolvedSteps);
        var buildPackage = Assert.Single(
            resolvedSteps,
            step => step.Name == "build-compute-vm-application-package");
        var uploadPackage = Assert.Single(
            resolvedSteps,
            step => step.Name == "upload-compute-vm-application-package");
        var foundationProvision = Assert.Single(
            resolvedSteps,
            step => step.Name == "provision-compute-foundation");
        var prerequisiteProvision = Assert.Single(
            resolvedSteps,
            step => step.Name == "provision-storage");
        var rolloutProvision = Assert.Single(
            resolvedSteps,
            step => step.Name == "provision-compute");

        Assert.Contains(WellKnownPipelineSteps.DeployPrereq, buildPackage.DependsOnSteps);
        Assert.Contains(prerequisiteProvision.Name, buildPackage.DependsOnSteps);
        Assert.Contains(buildPackage.Name, uploadPackage.DependsOnSteps);
        Assert.Contains(foundationProvision.Name, uploadPackage.DependsOnSteps);
        Assert.Contains(uploadPackage.Name, rolloutProvision.DependsOnSteps);
        Assert.Contains(prerequisiteProvision.Name, rolloutProvision.DependsOnSteps);
    }

    [Fact]
    public async Task GeneratedVmApplicationArchiveIsDeterministic()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        using var application = builder.Build();
        var tempFileSystem = application.Services.GetRequiredService<IFileSystemService>().TempDirectory;
        using var stagingDirectory = tempFileSystem.CreateTempSubdirectory("vmss-package");
        using var firstArchive = tempFileSystem.CreateTempFile("first.tar.gz");
        using var secondArchive = tempFileSystem.CreateTempFile("second.tar.gz");
        var appDirectory = Directory.CreateDirectory(Path.Combine(stagingDirectory.Path, "app"));
        await File.WriteAllTextAsync(Path.Combine(appDirectory.FullName, "testapp"), "fake executable");
        await File.WriteAllTextAsync(Path.Combine(appDirectory.FullName, "testapp.runtimeconfig.json"), "{}");
        await VirtualMachineScaleSetApplicationPackageBuilder.WriteManagementFilesAsync(
            stagingDirectory.Path,
            "compute",
            "app",
            "testapp",
            new Dictionary<string, string>
            {
                ["AZURE_CLIENT_ID"] = "00000000-0000-0000-0000-000000000001",
                ["AZURE_TOKEN_CREDENTIALS"] = "ManagedIdentityCredential",
                ["DOTNET_ENVIRONMENT"] = "Production",
                ["GREETING"] = "hello \"vm\"\nsecond line"
            },
            ["--mode", "value with spaces", "100%"],
            CancellationToken.None);
        await File.WriteAllBytesAsync(
            Path.Combine(appDirectory.FullName, "helper"),
            [0x7f, (byte)'E', (byte)'L', (byte)'F']);

        await VirtualMachineScaleSetApplicationPackageBuilder.CreateArchiveAsync(
            stagingDirectory.Path,
            firstArchive.Path,
            "testapp",
            CancellationToken.None);
        await VirtualMachineScaleSetApplicationPackageBuilder.CreateArchiveAsync(
            stagingDirectory.Path,
            secondArchive.Path,
            "testapp",
            CancellationToken.None);

        Assert.Equal(await File.ReadAllBytesAsync(firstArchive.Path), await File.ReadAllBytesAsync(secondArchive.Path));

        using var archiveStream = File.OpenRead(firstArchive.Path);
        using var gzipStream = new GZipStream(archiveStream, CompressionMode.Decompress);
        using var tarReader = new TarReader(gzipStream);
        var entries = new List<(string Name, UnixFileMode Mode, string? Content)>();
        while (await tarReader.GetNextEntryAsync() is { } entry)
        {
            string? content = null;
            if (entry.DataStream is not null)
            {
                using var reader = new StreamReader(
                    entry.DataStream,
                    System.Text.Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 1024,
                    leaveOpen: true);
                content = await reader.ReadToEndAsync();
            }

            entries.Add((entry.Name, entry.Mode, content));
        }

        Assert.Collection(
            entries,
            entry => Assert.Equal(("app/", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute), (entry.Name, entry.Mode)),
            entry => Assert.Equal("app/helper", entry.Name),
            entry => Assert.Equal("app/testapp", entry.Name),
            entry => Assert.Equal("app/testapp.runtimeconfig.json", entry.Name),
            entry => Assert.Equal("aspire-compute.env", entry.Name),
            entry => Assert.Equal("aspire-compute.service", entry.Name),
            entry => Assert.Equal("install.sh", entry.Name),
            entry => Assert.Equal("remove.sh", entry.Name),
            entry => Assert.Equal("update.sh", entry.Name));
        var serviceUnit = entries.Single(entry => entry.Name == "aspire-compute.service").Content;
        Assert.Contains(
            "ExecStart=\"/opt/aspire/compute/app/testapp\" \"--mode\" \"value with spaces\" \"100%%\"",
            serviceUnit);
        Assert.Contains("User=aspire-app", serviceUnit);
        Assert.Contains("EnvironmentFile=/etc/aspire/compute.env", serviceUnit);
        Assert.Equal(
            """
            AZURE_CLIENT_ID="00000000-0000-0000-0000-000000000001"
            AZURE_TOKEN_CREDENTIALS="ManagedIdentityCredential"
            DOTNET_ENVIRONMENT="Production"
            GREETING="hello \"vm\"\nsecond line"

            """,
            entries.Single(entry => entry.Name == "aspire-compute.env").Content);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
            entries.Single(entry => entry.Name == "install.sh").Mode);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
            entries.Single(entry => entry.Name == "app/helper").Mode);
    }

    [Fact]
    public async Task ResolvesProjectEnvironmentArgumentsAndWorkloadIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddDotnetProject("app", "app.csproj", options => options.ExcludeLaunchProfile = true)
            .WithEnvironment("GREETING", "hello")
            .WithArgs("--mode", "worker");
        var identityClientId = new ParameterResource(
            "identity-client-id",
            _ => "00000000-0000-0000-0000-000000000001");

        var configuration = await VirtualMachineScaleSetApplicationPackageBuilder.ResolveConfigurationAsync(
            app.Resource,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
            identityClientId,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY"] = "in_memory",
                ["GREETING"] = "hello",
                ["DOTNET_ENVIRONMENT"] = "Production",
                ["AZURE_CLIENT_ID"] = "00000000-0000-0000-0000-000000000001",
                ["AZURE_TOKEN_CREDENTIALS"] = "ManagedIdentityCredential"
            },
            configuration.EnvironmentVariables);
        Assert.Equal(["--mode", "worker"], configuration.Arguments);
    }

    [Fact]
    public async Task RejectsSecretsInGeneratedProjectConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var secret = builder.AddParameter("secret", secret: true);
        var app = builder.AddDotnetProject("app", "app.csproj", options => options.ExcludeLaunchProfile = true)
            .WithEnvironment("APP_SECRET", secret);
        var identityClientId = new ParameterResource(
            "identity-client-id",
            _ => "00000000-0000-0000-0000-000000000001");

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            VirtualMachineScaleSetApplicationPackageBuilder.ResolveConfigurationAsync(
                app.Resource,
                new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
                identityClientId,
                NullLogger.Instance,
                CancellationToken.None));

        Assert.Equal(
            "The environment variable 'APP_SECRET' on project 'app' contains a secret. " +
            "Azure Virtual Machine Scale Set compute environments do not support materializing secrets into VM Application packages.",
            exception.Message);
    }

    [Fact]
    public void RejectsVmApplicationPackagesLargerThanAzureLimit()
    {
        const long maximumPackageSize = 2L * 1024 * 1024 * 1024;

        VirtualMachineScaleSetApplicationPackageBuilder.ValidatePackageSize(maximumPackageSize, "app");
        var exception = Assert.Throws<DistributedApplicationException>(() =>
            VirtualMachineScaleSetApplicationPackageBuilder.ValidatePackageSize(maximumPackageSize + 1, "app"));

        Assert.Equal(
            "The VM Application package for project 'app' is 2147483649 bytes, " +
            "which exceeds the Azure VM Applications limit of 2147483648 bytes.",
            exception.Message);
    }

    [Fact]
    public void PackageUploadOptionsContainPackageMetadata()
    {
        var options = AzureVirtualMachineScaleSetEnvironmentResource.CreatePackageUploadOptions(
            "0123456789abcdef",
            "1.2.3");

        Assert.Equal("application/gzip", options.HttpHeaders.ContentType);
        Assert.Equal("0123456789abcdef", options.Metadata["aspire_fingerprint"]);
        Assert.Equal("1.2.3", options.Metadata["aspire_version"]);
        Assert.All(options.Metadata.Keys, key => Assert.Matches("^[A-Za-z_][A-Za-z0-9_]*$", key));
    }

    [Fact]
    public async Task RejectsWorkloadIdentityThatDiffersFromEnvironmentIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = AddConfiguredEnvironment(builder);
        var otherIdentity = builder.AddAzureUserAssignedIdentity("other-identity");
        builder.AddDotnetProject("app", "app.csproj", options => options.ExcludeLaunchProfile = true)
            .WithAzureUserAssignedIdentity(otherIdentity)
            .WithComputeEnvironment(environment);

        using var application = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => AzureManifestUtils.ExecuteBeforeStartHooksAsync(application, default));

        Assert.Equal(
            "Compute resource 'app' uses identity 'other-identity', but compute environment 'compute' requires identity 'compute-identity'.",
            exception.Message);
    }

    [Fact]
    public async Task RunModeDoesNotMaterializeOrValidateVmssInfrastructure()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var environment = builder.AddAzureVirtualMachineScaleSetEnvironment("compute");
        var project = builder.AddDotnetProject("app", "app.csproj", options => options.ExcludeLaunchProfile = true)
            .WithComputeEnvironment(environment);

        using var application = builder.Build();

        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(application, default);

        var model = application.Services.GetRequiredService<DistributedApplicationModel>();
        Assert.False(model.Resources.Contains(environment.Resource));
        Assert.Same(environment.Resource, project.Resource.GetComputeEnvironment());
        Assert.False(project.Resource.TryGetLastAnnotation<AppIdentityAnnotation>(out _));
    }

    [Fact]
    public async Task RejectsBoundContainerResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = AddConfiguredEnvironment(builder);
        builder.AddContainer("app", "example/app")
            .WithComputeEnvironment(environment);

        using var application = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => AzureManifestUtils.ExecuteBeforeStartHooksAsync(application, default));

        Assert.Equal(
            "Compute environment 'compute' does not support compute resource(s) 'app (ContainerResource)'.",
            exception.Message);
    }

    private static IResourceBuilder<AzureVirtualMachineScaleSetEnvironmentResource> AddConfiguredEnvironment(
        IDistributedApplicationBuilder builder)
    {
        var subnet = builder.AddAzureVirtualNetwork("network").AddSubnet("compute-subnet", "10.0.0.0/24");

        return builder.AddAzureVirtualMachineScaleSetEnvironment("compute")
            .WithSubnet(subnet)
            .WithAdminSshPublicKey("ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQCapabilityProof")
            .WithVmApplicationPackage(new Uri("https://account.blob.core.windows.net/applications/app.tar.gz"), "1.2.3");
    }

    private static IResourceBuilder<AzureVirtualMachineScaleSetEnvironmentResource> AddGeneratedPackageEnvironment(
        IDistributedApplicationBuilder builder)
    {
        var subnet = builder.AddAzureVirtualNetwork("network").AddSubnet("compute-subnet", "10.0.0.0/24");

        return builder.AddAzureVirtualMachineScaleSetEnvironment("compute")
            .WithSubnet(subnet)
            .WithAdminSshPublicKey("ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQCapabilityProof");
    }

    private static Task ExecutePipelineAsync(DistributedApplication application)
    {
        var context = new PipelineContext(
            application.Services.GetRequiredService<DistributedApplicationModel>(),
            application.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            application.Services,
            application.Services.GetRequiredService<ILogger<AzureVirtualMachineScaleSetEnvironmentTests>>(),
            CancellationToken.None);

        return application.Services.GetRequiredService<IDistributedApplicationPipeline>().ExecuteAsync(context);
    }
}
