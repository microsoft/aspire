@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param appservice_outputs_azure_container_registry_endpoint string

param appservice_outputs_planid string

param appservice_outputs_azure_container_registry_managed_identity_id string

param appservice_outputs_azure_container_registry_managed_identity_client_id string

param myapp_containerimage string

param myapp_containerport string

param myidentity_outputs_id string

param myidentity_outputs_clientid string

param appservice_outputs_azure_app_service_dashboard_uri string

param appservice_outputs_azure_website_contributor_managed_identity_id string

param appservice_outputs_azure_website_contributor_managed_identity_principal_id string

resource mainContainer 'Microsoft.Web/sites/sitecontainers@2025-03-01' = {
  name: 'main'
  properties: {
    authType: 'UserAssigned'
    image: myapp_containerimage
    isMain: true
    targetPort: myapp_containerport
    userManagedIdentityClientId: appservice_outputs_azure_container_registry_managed_identity_client_id
  }
  parent: webapp
}

resource webapp 'Microsoft.Web/sites@2025-03-01' = {
  name: take('${toLower('myapp')}-${uniqueString(resourceGroup().id)}', 60)
  location: location
  properties: {
    serverFarmId: appservice_outputs_planid
    keyVaultReferenceIdentity: myidentity_outputs_id
    siteConfig: {
      numberOfWorkers: 30
      linuxFxVersion: 'SITECONTAINERS'
      acrUseManagedIdentityCreds: true
      acrUserManagedIdentityID: appservice_outputs_azure_container_registry_managed_identity_client_id
      appSettings: [
        {
          name: 'WEBSITES_PORT'
          value: myapp_containerport
        }
        {
          name: 'OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY'
          value: 'in_memory'
        }
        {
          name: 'AZURE_CLIENT_ID'
          value: myidentity_outputs_clientid
        }
        {
          name: 'AZURE_TOKEN_CREDENTIALS'
          value: 'ManagedIdentityCredential'
        }
        {
          name: 'ASPIRE_ENVIRONMENT_NAME'
          value: 'appservice'
        }
        {
          name: 'OTEL_SERVICE_NAME'
          value: 'myapp'
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
          value: appservice_outputs_azure_app_service_dashboard_uri
        }
        {
          name: 'OTEL_CLIENT_ID'
          value: appservice_outputs_azure_container_registry_managed_identity_client_id
        }
      ]
    }
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${appservice_outputs_azure_container_registry_managed_identity_id}': { }
      '${myidentity_outputs_id}': { }
    }
  }
}

resource myapp_website_ra 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(webapp.id, appservice_outputs_azure_website_contributor_managed_identity_id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'de139f84-1756-47ae-9be6-808fbbe84772'))
  properties: {
    principalId: appservice_outputs_azure_website_contributor_managed_identity_principal_id
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'de139f84-1756-47ae-9be6-808fbbe84772')
    principalType: 'ServicePrincipal'
  }
  scope: webapp
}

resource slotConfigNames 'Microsoft.Web/sites/config@2025-03-01' = {
  name: 'slotConfigNames'
  properties: {
    appSettingNames: [
      'OTEL_SERVICE_NAME'
    ]
  }
  parent: webapp
}