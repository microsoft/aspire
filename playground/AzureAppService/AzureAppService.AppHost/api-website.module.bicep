@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param infra_outputs_azure_container_registry_endpoint string

param infra_outputs_planid string

param infra_outputs_azure_container_registry_managed_identity_id string

param infra_outputs_azure_container_registry_managed_identity_client_id string

param api_containerimage string

param api_containerport string

param storage_outputs_blobendpoint string

param account_outputs_connectionstring string

@secure()
param secretparam_value string

param api_identity_outputs_principalname string

param api_identity_outputs_id string

param api_identity_outputs_clientid string

param infra_outputs_azure_app_service_dashboard_uri string

param infra_outputs_azure_website_contributor_managed_identity_id string

param infra_outputs_azure_website_contributor_managed_identity_principal_id string

resource mainContainer 'Microsoft.Web/sites/sitecontainers@2025-03-01' = {
  name: 'main'
  properties: {
    authType: 'UserAssigned'
    image: api_containerimage
    isMain: true
    targetPort: api_containerport
    userManagedIdentityClientId: infra_outputs_azure_container_registry_managed_identity_client_id
  }
  parent: webapp
}

resource webapp 'Microsoft.Web/sites@2025-03-01' = {
  name: take('${toLower('api')}-${uniqueString(resourceGroup().id)}', 60)
  location: location
  properties: {
    serverFarmId: infra_outputs_planid
    keyVaultReferenceIdentity: api_identity_outputs_id
    siteConfig: {
      numberOfWorkers: 30
      linuxFxVersion: 'SITECONTAINERS'
      acrUseManagedIdentityCreds: true
      acrUserManagedIdentityID: infra_outputs_azure_container_registry_managed_identity_client_id
      appSettings: [
        {
          name: 'WEBSITES_PORT'
          value: api_containerport
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
          value: api_containerport
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
          name: 'ConnectionStrings__account'
          value: account_outputs_connectionstring
        }
        {
          name: 'ACCOUNT_URI'
          value: account_outputs_connectionstring
        }
        {
          name: 'VALUE'
          value: secretparam_value
        }
        {
          name: 'AZURE_PRINCIPAL_NAME'
          value: api_identity_outputs_principalname
        }
        {
          name: 'AZURE_CLIENT_ID'
          value: api_identity_outputs_clientid
        }
        {
          name: 'AZURE_TOKEN_CREDENTIALS'
          value: 'ManagedIdentityCredential'
        }
        {
          name: 'ASPIRE_ENVIRONMENT_NAME'
          value: 'infra'
        }
        {
          name: 'OTEL_SERVICE_NAME'
          value: 'api'
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
          value: infra_outputs_azure_app_service_dashboard_uri
        }
        {
          name: 'OTEL_CLIENT_ID'
          value: infra_outputs_azure_container_registry_managed_identity_client_id
        }
      ]
    }
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${infra_outputs_azure_container_registry_managed_identity_id}': { }
      '${api_identity_outputs_id}': { }
    }
  }
}

resource api_website_ra 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(webapp.id, infra_outputs_azure_website_contributor_managed_identity_id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'de139f84-1756-47ae-9be6-808fbbe84772'))
  properties: {
    principalId: infra_outputs_azure_website_contributor_managed_identity_principal_id
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'de139f84-1756-47ae-9be6-808fbbe84772')
    principalType: 'ServicePrincipal'
  }
  scope: webapp
}