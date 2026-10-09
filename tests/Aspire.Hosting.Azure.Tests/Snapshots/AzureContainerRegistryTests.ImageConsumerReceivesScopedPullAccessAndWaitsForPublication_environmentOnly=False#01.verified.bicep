@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param api_containerimage string

param api_identity_outputs_id string

param published_image string

param published_tag string

param published_sha256 string

param published_registryendpoint string

param published_repository string

param second_image string

param second_tag string

param second_sha256 string

param second_registryendpoint string

param second_repository string

param api_identity_outputs_clientid string

param env_outputs_azure_container_registry_endpoint string

param env_outputs_azure_container_registry_managed_identity_id string

resource api 'Microsoft.App/containerApps@2025-10-02-preview' = {
  name: 'api'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [
        {
          server: env_outputs_azure_container_registry_endpoint
          identity: env_outputs_azure_container_registry_managed_identity_id
        }
      ]
      runtime: {
        dotnet: {
          autoConfigureDataProtection: true
        }
      }
    }
    environmentId: env_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: api_containerimage
          name: 'api'
          env: [
            {
              name: 'OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY'
              value: 'in_memory'
            }
            {
              name: 'PUBLISHED_IMAGE'
              value: published_image
            }
            {
              name: 'PUBLISHED_TAG'
              value: published_tag
            }
            {
              name: 'PUBLISHED_SHA256'
              value: published_sha256
            }
            {
              name: 'PUBLISHED_REGISTRY'
              value: published_registryendpoint
            }
            {
              name: 'PUBLISHED_REPOSITORY'
              value: published_repository
            }
            {
              name: 'SECOND_IMAGE'
              value: second_image
            }
            {
              name: 'SECOND_TAG'
              value: second_tag
            }
            {
              name: 'SECOND_SHA256'
              value: second_sha256
            }
            {
              name: 'SECOND_REGISTRY'
              value: second_registryendpoint
            }
            {
              name: 'SECOND_REPOSITORY'
              value: second_repository
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: api_identity_outputs_clientid
            }
            {
              name: 'AZURE_TOKEN_CREDENTIALS'
              value: 'ManagedIdentityCredential'
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
      '${api_identity_outputs_id}': { }
      '${env_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}