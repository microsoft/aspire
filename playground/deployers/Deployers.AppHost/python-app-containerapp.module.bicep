@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param aca_env_outputs_azure_container_apps_environment_default_domain string

param aca_env_outputs_azure_container_apps_environment_id string

param python_app_containerimage string

param computeparam_value string

@secure()
param secretparam_value string

param parameterwithdefault_value string

param aca_env_outputs_azure_container_registry_endpoint string

param aca_env_outputs_azure_container_registry_managed_identity_id string

resource python_app 'Microsoft.App/containerApps@2025-01-01' = {
  name: 'python-app'
  location: location
  properties: {
    configuration: {
      secrets: [
        {
          name: 'p1'
          value: secretparam_value
        }
      ]
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 80
        transport: 'http'
      }
      registries: [
        {
          server: aca_env_outputs_azure_container_registry_endpoint
          identity: aca_env_outputs_azure_container_registry_managed_identity_id
        }
      ]
    }
    environmentId: aca_env_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: python_app_containerimage
          name: 'python-app'
          env: [
            {
              name: 'P0'
              value: computeparam_value
            }
            {
              name: 'P1'
              secretRef: 'p1'
            }
            {
              name: 'P3'
              value: parameterwithdefault_value
            }
            {
              name: 'TEST_SCENARIO'
              value: 'build-args-and-secrets'
            }
          ]
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
      '${aca_env_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}