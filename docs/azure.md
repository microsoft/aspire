# Notes on how the Azure integration works

## Provisioning

Many of the resources in the Azure integrations use the `Azure.Provisioning` SDKs. For many of them, this is how it works:

- Each AzureResource (or alternatively, a BicepResource) will, at publish time, turn into its own Bicep file
    - The `AzureProvisioningInfrastructure` is a collection of the resources that will show up in that file
    - Some resources will correspond to the `resource "..." = existing {}` in Bicep, which are those resources added
      to an instance of `AzureProvisioningInfrastructure` with `resource.AddAsExisting(infra)`.
    - Some resources will be "new".
- `BicepOutput` represents a pending value that will be computed when the Bicep is run.
