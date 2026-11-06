@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param infra_outputs_azure_container_apps_environment_default_domain string

param infra_outputs_azure_container_apps_environment_id string

param pythonapp_containerimage string

param infra_outputs_azure_container_registry_endpoint string

param infra_outputs_azure_container_registry_managed_identity_id string

resource pythonapp 'Microsoft.App/containerApps@2025-07-01' = {
  name: 'pythonapp'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [
        {
          server: infra_outputs_azure_container_registry_endpoint
          identity: infra_outputs_azure_container_registry_managed_identity_id
        }
      ]
    }
    environmentId: infra_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: pythonapp_containerimage
          name: 'pythonapp'
        }
      ]
      scale: {
        minReplicas: 1
      }
    }
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${infra_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}