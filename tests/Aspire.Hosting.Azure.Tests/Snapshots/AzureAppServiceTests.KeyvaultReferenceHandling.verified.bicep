@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_registry_endpoint string

param env_outputs_planid string

param env_outputs_azure_container_registry_managed_identity_id string

param env_outputs_azure_container_registry_managed_identity_client_id string

param api_containerimage string

param api_containerport string

param mydb_kv_outputs_name string

param mydb_outputs_connectionstring string

param kvName string

param sharedRg string

param api_identity_outputs_id string

param api_identity_outputs_clientid string

param env_outputs_azure_app_service_dashboard_uri string

param env_outputs_azure_website_contributor_managed_identity_id string

param env_outputs_azure_website_contributor_managed_identity_principal_id string

resource mydb_kv 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: mydb_kv_outputs_name
}

resource mydb_kv_connectionstrings__mydb 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = {
  name: 'connectionstrings--mydb'
  parent: mydb_kv
}

resource mydb_kv_primaryaccesskey__mydb 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = {
  name: 'primaryaccesskey--mydb'
  parent: mydb_kv
}

resource existingKv 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: kvName
  scope: resourceGroup(sharedRg)
}

resource existingKv_secret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = {
  name: 'secret'
  parent: existingKv
}

resource mainContainer 'Microsoft.Web/sites/sitecontainers@2025-03-01' = {
  name: 'main'
  properties: {
    authType: 'UserAssigned'
    image: api_containerimage
    isMain: true
    targetPort: api_containerport
    userManagedIdentityClientId: env_outputs_azure_container_registry_managed_identity_client_id
  }
  parent: webapp
}

resource webapp 'Microsoft.Web/sites@2025-03-01' = {
  name: take('${toLower('api')}-${uniqueString(resourceGroup().id)}', 60)
  location: location
  properties: {
    serverFarmId: env_outputs_planid
    keyVaultReferenceIdentity: api_identity_outputs_id
    siteConfig: {
      numberOfWorkers: 30
      linuxFxVersion: 'SITECONTAINERS'
      acrUseManagedIdentityCreds: true
      acrUserManagedIdentityID: env_outputs_azure_container_registry_managed_identity_client_id
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
          name: 'ConnectionStrings__mydb'
          value: '@Microsoft.KeyVault(SecretUri=${mydb_kv_connectionstrings__mydb.properties.secretUri})'
        }
        {
          name: 'MYDB_URI'
          value: mydb_outputs_connectionstring
        }
        {
          name: 'MYDB_ACCOUNTKEY'
          value: '@Microsoft.KeyVault(SecretUri=${mydb_kv_primaryaccesskey__mydb.properties.secretUri})'
        }
        {
          name: 'MYDB_CONNECTIONSTRING'
          value: '@Microsoft.KeyVault(SecretUri=${mydb_kv_connectionstrings__mydb.properties.secretUri})'
        }
        {
          name: 'SECRET_VALUE'
          value: '@Microsoft.KeyVault(SecretUri=${existingKv_secret.properties.secretUri})'
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
          value: 'env'
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
          value: env_outputs_azure_app_service_dashboard_uri
        }
        {
          name: 'OTEL_CLIENT_ID'
          value: env_outputs_azure_container_registry_managed_identity_client_id
        }
      ]
    }
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${env_outputs_azure_container_registry_managed_identity_id}': { }
      '${api_identity_outputs_id}': { }
    }
  }
}

resource api_website_ra 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(webapp.id, env_outputs_azure_website_contributor_managed_identity_id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'de139f84-1756-47ae-9be6-808fbbe84772'))
  properties: {
    principalId: env_outputs_azure_website_contributor_managed_identity_principal_id
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
