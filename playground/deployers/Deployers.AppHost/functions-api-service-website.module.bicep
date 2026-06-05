@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param aas_env_outputs_azure_container_registry_endpoint string

param aas_env_outputs_planid string

param aas_env_outputs_azure_container_registry_managed_identity_id string

param aas_env_outputs_azure_container_registry_managed_identity_client_id string

param functions_api_service_containerimage string

param functions_api_service_containerport string

param eventhubs_outputs_eventhubsendpoint string

param eventhubs_outputs_eventhubshostname string

param messaging_outputs_servicebusendpoint string

param messaging_outputs_servicebushostname string

param cosmosdb_outputs_connectionstring string

param storage_outputs_queueendpoint string

param storage_outputs_blobendpoint string

param functions_api_service_identity_outputs_id string

param functions_api_service_identity_outputs_clientid string

param aas_env_outputs_azure_app_service_dashboard_uri string

param aas_env_outputs_azure_website_contributor_managed_identity_id string

param aas_env_outputs_azure_website_contributor_managed_identity_principal_id string

resource mainContainer 'Microsoft.Web/sites/sitecontainers@2025-03-01' = {
  name: 'main'
  properties: {
    authType: 'UserAssigned'
    image: functions_api_service_containerimage
    isMain: true
    targetPort: functions_api_service_containerport
    userManagedIdentityClientId: aas_env_outputs_azure_container_registry_managed_identity_client_id
  }
  parent: webapp
}

resource webapp 'Microsoft.Web/sites@2025-03-01' = {
  name: take('${toLower('functions-api-service')}-${uniqueString(resourceGroup().id)}', 60)
  location: location
  properties: {
    serverFarmId: aas_env_outputs_planid
    keyVaultReferenceIdentity: functions_api_service_identity_outputs_id
    siteConfig: {
      numberOfWorkers: 30
      linuxFxVersion: 'SITECONTAINERS'
      acrUseManagedIdentityCreds: true
      acrUserManagedIdentityID: aas_env_outputs_azure_container_registry_managed_identity_client_id
      appSettings: [
        {
          name: 'WEBSITES_PORT'
          value: functions_api_service_containerport
        }
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
          value: functions_api_service_containerport
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
          name: 'ConnectionStrings__foobarbaz'
          value: storage_outputs_blobendpoint
        }
        {
          name: 'FOOBARBAZ_URI'
          value: storage_outputs_blobendpoint
        }
        {
          name: 'AZURE_CLIENT_ID'
          value: functions_api_service_identity_outputs_clientid
        }
        {
          name: 'AZURE_TOKEN_CREDENTIALS'
          value: 'ManagedIdentityCredential'
        }
        {
          name: 'ASPIRE_ENVIRONMENT_NAME'
          value: 'aas-env'
        }
        {
          name: 'OTEL_SERVICE_NAME'
          value: 'functions-api-service'
        }
        {
          name: 'OTEL_EXPORTER_OTLP_PROTOCOL'
          value: 'grpc'
        }
        {
          name: 'OTEL_EXPORTER_OTLP_ENDPOINT'
          value: 'http://localhost:6001'
        }
        {
          name: 'WEBSITE_ENABLE_ASPIRE_OTEL_SIDECAR'
          value: 'true'
        }
        {
          name: 'OTEL_COLLECTOR_URL'
          value: aas_env_outputs_azure_app_service_dashboard_uri
        }
        {
          name: 'OTEL_CLIENT_ID'
          value: aas_env_outputs_azure_container_registry_managed_identity_client_id
        }
      ]
    }
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${aas_env_outputs_azure_container_registry_managed_identity_id}': { }
      '${functions_api_service_identity_outputs_id}': { }
    }
  }
}

resource functions_api_service_website_ra 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(webapp.id, aas_env_outputs_azure_website_contributor_managed_identity_id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'de139f84-1756-47ae-9be6-808fbbe84772'))
  properties: {
    principalId: aas_env_outputs_azure_website_contributor_managed_identity_principal_id
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'de139f84-1756-47ae-9be6-808fbbe84772')
    principalType: 'ServicePrincipal'
  }
  scope: webapp
}