@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param apiservice_containerimage string

param apiservice_identity_outputs_id string

param apiservice_containerport string

param eventhubs_outputs_eventhubsendpoint string

param eventhubs_outputs_eventhubshostname string

param messaging_outputs_servicebusendpoint string

param messaging_outputs_servicebushostname string

param cosmosdb_outputs_connectionstring string

param storage_outputs_queueendpoint string

param storage_outputs_blobendpoint string

param apiservice_identity_outputs_clientid string

param env_outputs_azure_container_registry_endpoint string

param env_outputs_azure_container_registry_managed_identity_id string

resource apiservice 'Microsoft.App/containerApps@2025-02-02-preview' = {
  name: 'apiservice'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: int(apiservice_containerport)
        transport: 'http'
      }
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
          image: apiservice_containerimage
          name: 'apiservice'
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
              value: apiservice_containerport
            }
            {
              name: 'ConnectionStrings__myhub'
              value: 'Endpoint=${eventhubs_outputs_eventhubsendpoint};EntityPath=myhub'
            }
            {
              name: 'MYHUB_HOST'
              value: eventhubs_outputs_eventhubshostname
            }
            {
              name: 'MYHUB_URI'
              value: eventhubs_outputs_eventhubsendpoint
            }
            {
              name: 'MYHUB_EVENTHUBNAME'
              value: 'myhub'
            }
            {
              name: 'ConnectionStrings__messaging'
              value: messaging_outputs_servicebusendpoint
            }
            {
              name: 'MESSAGING_HOST'
              value: messaging_outputs_servicebushostname
            }
            {
              name: 'MESSAGING_URI'
              value: messaging_outputs_servicebusendpoint
            }
            {
              name: 'ConnectionStrings__cosmosdb'
              value: cosmosdb_outputs_connectionstring
            }
            {
              name: 'COSMOSDB_URI'
              value: cosmosdb_outputs_connectionstring
            }
            {
              name: 'ConnectionStrings__queue'
              value: storage_outputs_queueendpoint
            }
            {
              name: 'QUEUE_URI'
              value: storage_outputs_queueendpoint
            }
            {
              name: 'ConnectionStrings__blob'
              value: storage_outputs_blobendpoint
            }
            {
              name: 'BLOB_URI'
              value: storage_outputs_blobendpoint
            }
            {
              name: 'FUNCAPP_HTTP'
              value: 'http://funcapp.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'services__funcapp__http__0'
              value: 'http://funcapp.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'FUNCAPP_HTTPS'
              value: 'https://funcapp.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'services__funcapp__https__0'
              value: 'https://funcapp.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: apiservice_identity_outputs_clientid
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
      '${apiservice_identity_outputs_id}': { }
      '${env_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}