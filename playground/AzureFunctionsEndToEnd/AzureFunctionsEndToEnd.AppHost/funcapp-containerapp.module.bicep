@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param funcapp_containerimage string

param funcapp_identity_outputs_id string

param funcstorage67c6c_outputs_blobendpoint string

param funcstorage67c6c_outputs_queueendpoint string

param funcstorage67c6c_outputs_tableendpoint string

param eventhubs_outputs_eventhubsendpoint string

param messaging_outputs_servicebusendpoint string

param cosmosdb_outputs_connectionstring string

param storage_outputs_blobendpoint string

param storage_outputs_queueendpoint string

param funcapp_identity_outputs_clientid string

param env_outputs_azure_container_registry_endpoint string

param env_outputs_azure_container_registry_managed_identity_id string

resource funcapp 'Microsoft.App/containerApps@2025-02-02-preview' = {
  name: 'funcapp'
  location: location
  properties: {
    configuration: {
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
          image: funcapp_containerimage
          name: 'funcapp'
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
              name: 'FUNCTIONS_WORKER_RUNTIME'
              value: 'dotnet-isolated'
            }
            {
              name: 'AzureFunctionsJobHost__telemetryMode'
              value: 'OpenTelemetry'
            }
            {
              name: 'ASPNETCORE_URLS'
              value: 'http://+:8080'
            }
            {
              name: 'AzureWebJobsStorage__blobServiceUri'
              value: funcstorage67c6c_outputs_blobendpoint
            }
            {
              name: 'AzureWebJobsStorage__queueServiceUri'
              value: funcstorage67c6c_outputs_queueendpoint
            }
            {
              name: 'AzureWebJobsStorage__tableServiceUri'
              value: funcstorage67c6c_outputs_tableendpoint
            }
            {
              name: 'Aspire__Azure__Storage__Blobs__AzureWebJobsStorage__ServiceUri'
              value: funcstorage67c6c_outputs_blobendpoint
            }
            {
              name: 'Aspire__Azure__Storage__Queues__AzureWebJobsStorage__ServiceUri'
              value: funcstorage67c6c_outputs_queueendpoint
            }
            {
              name: 'Aspire__Azure__Data__Tables__AzureWebJobsStorage__ServiceUri'
              value: funcstorage67c6c_outputs_tableendpoint
            }
            {
              name: 'myhub__fullyQualifiedNamespace'
              value: eventhubs_outputs_eventhubsendpoint
            }
            {
              name: 'Aspire__Azure__Messaging__EventHubs__EventHubProducerClient__myhub__FullyQualifiedNamespace'
              value: eventhubs_outputs_eventhubsendpoint
            }
            {
              name: 'Aspire__Azure__Messaging__EventHubs__EventHubConsumerClient__myhub__FullyQualifiedNamespace'
              value: eventhubs_outputs_eventhubsendpoint
            }
            {
              name: 'Aspire__Azure__Messaging__EventHubs__EventProcessorClient__myhub__FullyQualifiedNamespace'
              value: eventhubs_outputs_eventhubsendpoint
            }
            {
              name: 'Aspire__Azure__Messaging__EventHubs__PartitionReceiver__myhub__FullyQualifiedNamespace'
              value: eventhubs_outputs_eventhubsendpoint
            }
            {
              name: 'Aspire__Azure__Messaging__EventHubs__EventHubBufferedProducerClient__myhub__FullyQualifiedNamespace'
              value: eventhubs_outputs_eventhubsendpoint
            }
            {
              name: 'Aspire__Azure__Messaging__EventHubs__EventHubProducerClient__myhub__EventHubName'
              value: 'myhub'
            }
            {
              name: 'Aspire__Azure__Messaging__EventHubs__EventHubConsumerClient__myhub__EventHubName'
              value: 'myhub'
            }
            {
              name: 'Aspire__Azure__Messaging__EventHubs__EventProcessorClient__myhub__EventHubName'
              value: 'myhub'
            }
            {
              name: 'Aspire__Azure__Messaging__EventHubs__PartitionReceiver__myhub__EventHubName'
              value: 'myhub'
            }
            {
              name: 'Aspire__Azure__Messaging__EventHubs__EventHubBufferedProducerClient__myhub__EventHubName'
              value: 'myhub'
            }
            {
              name: 'messaging__fullyQualifiedNamespace'
              value: messaging_outputs_servicebusendpoint
            }
            {
              name: 'Aspire__Azure__Messaging__ServiceBus__messaging__FullyQualifiedNamespace'
              value: messaging_outputs_servicebusendpoint
            }
            {
              name: 'cosmosdb__accountEndpoint'
              value: cosmosdb_outputs_connectionstring
            }
            {
              name: 'Aspire__Microsoft__EntityFrameworkCore__Cosmos__cosmosdb__AccountEndpoint'
              value: cosmosdb_outputs_connectionstring
            }
            {
              name: 'Aspire__Microsoft__Azure__Cosmos__cosmosdb__AccountEndpoint'
              value: cosmosdb_outputs_connectionstring
            }
            {
              name: 'ConnectionStrings__myblobcontainer'
              value: 'Endpoint=${storage_outputs_blobendpoint};ContainerName=myblobcontainer'
            }
            {
              name: 'MYBLOBCONTAINER_URI'
              value: storage_outputs_blobendpoint
            }
            {
              name: 'MYBLOBCONTAINER_BLOBCONTAINERNAME'
              value: 'myblobcontainer'
            }
            {
              name: 'blob__blobServiceUri'
              value: storage_outputs_blobendpoint
            }
            {
              name: 'blob__queueServiceUri'
              value: storage_outputs_queueendpoint
            }
            {
              name: 'Aspire__Azure__Storage__Blobs__blob__ServiceUri'
              value: storage_outputs_blobendpoint
            }
            {
              name: 'queue__queueServiceUri'
              value: storage_outputs_queueendpoint
            }
            {
              name: 'Aspire__Azure__Storage__Queues__queue__ServiceUri'
              value: storage_outputs_queueendpoint
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: funcapp_identity_outputs_clientid
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
      '${funcapp_identity_outputs_id}': { }
      '${env_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
  kind: 'functionapp'
}