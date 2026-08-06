// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Azure Virtual Machine Scale Set compute environments.
/// </summary>
[Experimental("ASPIREAZURE004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class AzureVirtualMachineScaleSetEnvironmentExtensions
{
    private static readonly string[] s_azureBlobEndpointSuffixes =
    [
        "blob.core.windows.net",
        "blob.core.usgovcloudapi.net",
        "blob.core.chinacloudapi.cn",
        "blob.core.cloudapi.de"
    ];

    /// <summary>
    /// Adds an Azure Virtual Machine Scale Set compute environment to the application model.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns>The Azure Virtual Machine Scale Set compute environment resource builder.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is empty or contains only whitespace.</exception>
    [AspireExport]
    public static IResourceBuilder<AzureVirtualMachineScaleSetEnvironmentResource> AddAzureVirtualMachineScaleSetEnvironment(
        this IDistributedApplicationBuilder builder,
        string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        builder.AddAzureProvisioning();
        builder.Services.Configure<AzureProvisioningOptions>(options => options.SupportsTargetedRoleAssignments = true);

        var workloadIdentity = builder.AddAzureUserAssignedIdentity(CreateChildResourceName(name, "identity"));
        var foundation = new AzureVirtualMachineScaleSetFoundationResource(
            CreateChildResourceName(name, "foundation"),
            name);
        if (builder.ExecutionContext.IsPublishMode)
        {
            builder.AddResource(foundation);
        }

        var resource = new AzureVirtualMachineScaleSetEnvironmentResource(name, foundation)
        {
            WorkloadIdentity = workloadIdentity.Resource
        };

        var resourceBuilder = builder.ExecutionContext.IsPublishMode
            ? builder.AddResource(resource)
            : builder.CreateResourceBuilder(resource);

        return resourceBuilder.WithAnnotation(new AzureComputeEnvironmentIdentityAnnotation(workloadIdentity.Resource));
    }

    /// <summary>
    /// Configures the Linux platform image used by the virtual machine instances.
    /// </summary>
    /// <param name="builder">The Azure Virtual Machine Scale Set compute environment resource builder.</param>
    /// <param name="publisher">The image publisher.</param>
    /// <param name="offer">The image offer.</param>
    /// <param name="sku">The image SKU.</param>
    /// <param name="version">The image version.</param>
    /// <returns>The Azure Virtual Machine Scale Set compute environment resource builder.</returns>
    /// <remarks>
    /// This experimental integration publishes the application for <c>linux-x64</c>. The selected
    /// platform image and virtual machine SKU must therefore provide a compatible Linux x64 environment.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<AzureVirtualMachineScaleSetEnvironmentResource> WithPlatformImage(
        this IResourceBuilder<AzureVirtualMachineScaleSetEnvironmentResource> builder,
        string publisher,
        string offer,
        string sku,
        string version)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(publisher);
        ArgumentException.ThrowIfNullOrWhiteSpace(offer);
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        builder.Resource.ImagePublisher = publisher;
        builder.Resource.ImageOffer = offer;
        builder.Resource.ImageSku = sku;
        builder.Resource.ImageVersion = version;

        return builder;
    }

    /// <summary>
    /// Configures the Azure virtual machine SKU used by the scale set.
    /// </summary>
    /// <param name="builder">The Azure Virtual Machine Scale Set compute environment resource builder.</param>
    /// <param name="sku">The Azure virtual machine SKU.</param>
    /// <returns>The Azure Virtual Machine Scale Set compute environment resource builder.</returns>
    /// <remarks>
    /// This experimental integration publishes the application for <c>linux-x64</c>. Arm-based
    /// virtual machine SKUs are not supported.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<AzureVirtualMachineScaleSetEnvironmentResource> WithVmSku(
        this IResourceBuilder<AzureVirtualMachineScaleSetEnvironmentResource> builder,
        string sku)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);

        builder.Resource.VmSku = sku;

        return builder;
    }

    /// <summary>
    /// Configures the fixed number of virtual machine instances in the scale set.
    /// </summary>
    /// <param name="builder">The Azure Virtual Machine Scale Set compute environment resource builder.</param>
    /// <param name="capacity">The number of virtual machine instances.</param>
    /// <returns>The Azure Virtual Machine Scale Set compute environment resource builder.</returns>
    [AspireExport]
    public static IResourceBuilder<AzureVirtualMachineScaleSetEnvironmentResource> WithCapacity(
        this IResourceBuilder<AzureVirtualMachineScaleSetEnvironmentResource> builder,
        int capacity)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        builder.Resource.Capacity = capacity;

        return builder;
    }

    /// <summary>
    /// Attaches the virtual machine scale set network interfaces to an Azure subnet.
    /// </summary>
    /// <param name="builder">The Azure Virtual Machine Scale Set compute environment resource builder.</param>
    /// <param name="subnet">The Azure subnet resource builder.</param>
    /// <returns>The Azure Virtual Machine Scale Set compute environment resource builder.</returns>
    [AspireExport]
    public static IResourceBuilder<AzureVirtualMachineScaleSetEnvironmentResource> WithSubnet(
        this IResourceBuilder<AzureVirtualMachineScaleSetEnvironmentResource> builder,
        IResourceBuilder<AzureSubnetResource> subnet)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(subnet);

        builder.Resource.Subnet = subnet.Resource;

        return builder;
    }

    /// <summary>
    /// Configures the Azure VM Application package used by the scale set.
    /// </summary>
    /// <param name="builder">The Azure Virtual Machine Scale Set compute environment resource builder.</param>
    /// <param name="packageUri">The HTTPS URI of the VM Application package in Azure Blob Storage.</param>
    /// <param name="version">The immutable three-part numeric VM Application version.</param>
    /// <returns>The Azure Virtual Machine Scale Set compute environment resource builder.</returns>
    /// <remarks>
    /// The package must be a gzip-compressed tar archive containing <c>install.sh</c> and <c>update.sh</c>
    /// at its root. A root-level <c>remove.sh</c> script is optional. The package must be accessible to Azure
    /// Compute Gallery without credentials. Query strings are rejected so credentials cannot be written into
    /// the generated deployment template.
    /// </remarks>
    [AspireExport("withVmApplicationPackageUri", MethodName = "withVmApplicationPackageUri")]
    public static IResourceBuilder<AzureVirtualMachineScaleSetEnvironmentResource> WithVmApplicationPackage(
        this IResourceBuilder<AzureVirtualMachineScaleSetEnvironmentResource> builder,
        Uri packageUri,
        string version)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(packageUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (!packageUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The VM Application package URI must be absolute.", nameof(packageUri));
        }

        if (packageUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The VM Application package URI must use HTTPS.", nameof(packageUri));
        }

        if (!string.IsNullOrEmpty(packageUri.UserInfo))
        {
            throw new ArgumentException("The VM Application package URI cannot contain user information.", nameof(packageUri));
        }

        if (!string.IsNullOrEmpty(packageUri.Query))
        {
            throw new ArgumentException("The VM Application package URI cannot contain a query string.", nameof(packageUri));
        }

        if (!string.IsNullOrEmpty(packageUri.Fragment))
        {
            throw new ArgumentException("The VM Application package URI cannot contain a fragment.", nameof(packageUri));
        }

        if (!IsAzureBlobUri(packageUri))
        {
            throw new ArgumentException("The VM Application package URI must identify a blob in Azure Storage.", nameof(packageUri));
        }

        if (!IsValidGalleryApplicationVersion(version))
        {
            throw new ArgumentException("The VM Application version must contain exactly three non-negative numeric parts.", nameof(version));
        }

        builder.Resource.ApplicationPackageUri = packageUri.AbsoluteUri;
        builder.Resource.ApplicationVersion = version;
        builder.Resource.UsesGeneratedApplicationPackage = false;
        builder.Resource.Foundation.ProvisionPackageStorage = false;

        return builder;
    }

    /// <summary>
    /// Configures the Azure VM Application package using a secret URI parameter.
    /// </summary>
    /// <param name="builder">The Azure Virtual Machine Scale Set compute environment resource builder.</param>
    /// <param name="packageUri">A secret parameter containing the HTTPS Azure Blob Storage URI, including a SAS query string when required.</param>
    /// <param name="version">The immutable three-part numeric VM Application version.</param>
    /// <returns>The Azure Virtual Machine Scale Set compute environment resource builder.</returns>
    /// <remarks>
    /// The package must be a gzip-compressed tar archive containing <c>install.sh</c> and <c>update.sh</c>
    /// at its root. A root-level <c>remove.sh</c> script is optional. Use this overload for private blobs accessed
    /// through a SAS URI. The parameter remains secret in the Aspire manifest and generated Bicep.
    /// </remarks>
    [AspireExport("withVmApplicationPackageParameter", MethodName = "withVmApplicationPackageParameter")]
    public static IResourceBuilder<AzureVirtualMachineScaleSetEnvironmentResource> WithVmApplicationPackage(
        this IResourceBuilder<AzureVirtualMachineScaleSetEnvironmentResource> builder,
        IResourceBuilder<ParameterResource> packageUri,
        string version)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(packageUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (!packageUri.Resource.Secret)
        {
            throw new ArgumentException("The VM Application package URI parameter must be secret.", nameof(packageUri));
        }

        if (!IsValidGalleryApplicationVersion(version))
        {
            throw new ArgumentException("The VM Application version must contain exactly three non-negative numeric parts.", nameof(version));
        }

        builder.WithParameter("vmApplicationPackageUri", packageUri);
        builder.Resource.ApplicationPackageUri = packageUri.Resource;
        builder.Resource.ApplicationVersion = version;
        builder.Resource.UsesGeneratedApplicationPackage = false;
        builder.Resource.Foundation.ProvisionPackageStorage = false;

        return builder;
    }

    /// <summary>
    /// Configures the SSH public key used to provision the Linux administrator account.
    /// </summary>
    /// <param name="builder">The Azure Virtual Machine Scale Set compute environment resource builder.</param>
    /// <param name="publicKey">The SSH public key.</param>
    /// <returns>The Azure Virtual Machine Scale Set compute environment resource builder.</returns>
    /// <remarks>No inbound SSH access is provisioned by this integration.</remarks>
    [AspireExport]
    public static IResourceBuilder<AzureVirtualMachineScaleSetEnvironmentResource> WithAdminSshPublicKey(
        this IResourceBuilder<AzureVirtualMachineScaleSetEnvironmentResource> builder,
        string publicKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKey);

        builder.Resource.AdminSshPublicKey = publicKey;

        return builder;
    }

    private static bool IsValidGalleryApplicationVersion(string version)
    {
        var parts = version.Split('.');

        return parts.Length == 3 && parts.All(static part => uint.TryParse(part, out _));
    }

    private static bool IsAzureBlobUri(Uri uri)
    {
        var host = uri.DnsSafeHost;
        var hasBlobEndpoint = s_azureBlobEndpointSuffixes.Any(suffix =>
        {
            if (!host.EndsWith($".{suffix}", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var accountName = host[..^(suffix.Length + 1)];
            return accountName.Length is >= 3 and <= 24 &&
                accountName.All(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9');
        });
        var pathParts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        return hasBlobEndpoint && pathParts.Length >= 2;
    }

    private static string CreateChildResourceName(string parentName, string suffix)
    {
        var maximumParentLength = 64 - suffix.Length - 1;
        var prefix = parentName[..Math.Min(parentName.Length, maximumParentLength)].TrimEnd('-');
        return $"{prefix}-{suffix}";
    }
}
