@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_registry_endpoint string

param env_outputs_planid string

param env_outputs_azure_container_registry_managed_identity_id string

param env_outputs_azure_container_registry_managed_identity_client_id string

param other_containerimage string

resource mainContainer 'Microsoft.Web/sites/sitecontainers@2025-03-01' = {
  name: 'main'
  properties: {
    authType: 'UserAssigned'
    image: other_containerimage
    isMain: true
    targetPort: '8080'
    userManagedIdentityClientId: env_outputs_azure_container_registry_managed_identity_client_id
  }
  parent: webapp
}

resource webapp 'Microsoft.Web/sites@2025-03-01' = {
  name: take('${toLower('other')}-${uniqueString(resourceGroup().id)}', 60)
  location: location
  properties: {
    serverFarmId: env_outputs_planid
    publicNetworkAccess: 'Disabled'
    siteConfig: {
      numberOfWorkers: 30
      linuxFxVersion: 'SITECONTAINERS'
      acrUseManagedIdentityCreds: true
      acrUserManagedIdentityID: env_outputs_azure_container_registry_managed_identity_client_id
      appSettings: [
        {
          name: 'WEBSITES_PORT'
          value: '8080'
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
          value: '8080'
        }
        {
          name: 'ASPIRE_ENVIRONMENT_NAME'
          value: 'env'
        }
      ]
    }
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${env_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}

output privateLinkResourceId string = webapp.id

output privateLinkLocation string = toLower(replace(webapp.location, ' ', ''))