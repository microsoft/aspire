@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param aas_env_outputs_azure_container_registry_endpoint string

param aas_env_outputs_planid string

param aas_env_outputs_azure_container_registry_managed_identity_id string

param aas_env_outputs_azure_container_registry_managed_identity_client_id string

param api_service_containerimage string

param api_service_containerport string

param aas_env_outputs_azure_app_service_dashboard_uri string

param aas_env_outputs_azure_website_contributor_managed_identity_id string

param aas_env_outputs_azure_website_contributor_managed_identity_principal_id string

resource mainContainer 'Microsoft.Web/sites/sitecontainers@2025-03-01' = {
  name: 'main'
  properties: {
    authType: 'UserAssigned'
    image: api_service_containerimage
    isMain: true
    targetPort: api_service_containerport
    userManagedIdentityClientId: aas_env_outputs_azure_container_registry_managed_identity_client_id
  }
  parent: webapp
}

resource webapp 'Microsoft.Web/sites@2025-03-01' = {
  name: take('${toLower('api-service')}-${uniqueString(resourceGroup().id)}', 60)
  location: location
  properties: {
    serverFarmId: aas_env_outputs_planid
    siteConfig: {
      numberOfWorkers: 30
      linuxFxVersion: 'SITECONTAINERS'
      acrUseManagedIdentityCreds: true
      acrUserManagedIdentityID: aas_env_outputs_azure_container_registry_managed_identity_client_id
      appSettings: [
        {
          name: 'WEBSITES_PORT'
          value: api_service_containerport
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
          value: api_service_containerport
        }
        {
          name: 'ASPIRE_ENVIRONMENT_NAME'
          value: 'aas-env'
        }
        {
          name: 'OTEL_SERVICE_NAME'
          value: 'api-service'
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
    }
  }
}

resource api_service_website_ra 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(webapp.id, aas_env_outputs_azure_website_contributor_managed_identity_id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'de139f84-1756-47ae-9be6-808fbbe84772'))
  properties: {
    principalId: aas_env_outputs_azure_website_contributor_managed_identity_principal_id
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'de139f84-1756-47ae-9be6-808fbbe84772')
    principalType: 'ServicePrincipal'
  }
  scope: webapp
}