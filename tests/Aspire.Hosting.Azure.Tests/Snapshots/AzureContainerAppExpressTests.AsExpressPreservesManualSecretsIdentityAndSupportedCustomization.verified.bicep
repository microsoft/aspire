@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param env_outputs_azure_container_registry_endpoint string

param env_outputs_azure_container_registry_managed_identity_id string

param api_containerimage string

param api_identity_outputs_id string

@secure()
param api_key_value string

param storage_outputs_blobendpoint string

param api_identity_outputs_clientid string

resource api 'Microsoft.App/containerApps@2026-03-02-preview' = {
  name: 'api'
  location: location
  properties: {
    configuration: {
      secrets: [
        {
          name: 'api-key'
          value: api_key_value
        }
      ]
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
      }
      registries: [
        {
          server: env_outputs_azure_container_registry_endpoint
          identity: env_outputs_azure_container_registry_managed_identity_id
        }
      ]
    }
    environmentId: env_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          probes: [
            {
              tcpSocket: {
                port: 8080
              }
              type: 'Liveness'
            }
          ]
          image: api_containerimage
          name: 'api'
          env: [
            {
              name: 'OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY'
              value: 'in_memory'
            }
            {
              name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
              value: 'true'
            }
            {
              name: 'HTTP_PORTS'
              value: '8080'
            }
            {
              name: 'API_KEY'
              secretRef: 'api-key'
            }
            {
              name: 'ConnectionStrings__blobs'
              value: storage_outputs_blobendpoint
            }
            {
              name: 'BLOBS_URI'
              value: storage_outputs_blobendpoint
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
          volumeMounts: [
            {
              volumeName: 'scratch'
              mountPath: '/tmp/scratch'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        rules: [
          {
            name: 'http'
            http: {
              metadata: {
                concurrentRequests: '20'
              }
            }
          }
        ]
      }
      volumes: [
        {
          name: 'scratch'
          storageType: 'EmptyDir'
        }
      ]
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