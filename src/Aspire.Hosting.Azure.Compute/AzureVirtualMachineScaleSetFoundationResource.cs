// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

internal sealed class AzureVirtualMachineScaleSetFoundationResource : AzureBicepResource
{
    public AzureVirtualMachineScaleSetFoundationResource(string name, string environmentName)
        : base(name, templateResourceName: "Aspire.Hosting.Azure.Compute.AzureVirtualMachineScaleSetFoundation.bicep")
    {
        Parameters["resourceName"] = environmentName;
        Parameters["provisionPackageStorage"] = true;
        Parameters[KnownParameters.UserPrincipalId] = null;

        PackageUri = new BicepOutputReference("packageUri", this);
        GalleryName = new BicepOutputReference("galleryName", this);
        GalleryApplicationName = new BicepOutputReference("galleryApplicationName", this);
    }

    internal BicepOutputReference PackageUri { get; }

    internal BicepOutputReference GalleryName { get; }

    internal BicepOutputReference GalleryApplicationName { get; }

    internal bool ProvisionPackageStorage
    {
        set => Parameters["provisionPackageStorage"] = value;
    }
}
