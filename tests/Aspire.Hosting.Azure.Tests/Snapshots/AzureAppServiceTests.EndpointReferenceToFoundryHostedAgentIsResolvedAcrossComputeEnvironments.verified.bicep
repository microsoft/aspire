@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_registry_endpoint string

param env_outputs_planid string

param env_outputs_azure_container_registry_managed_identity_id string

param env_outputs_azure_container_registry_managed_identity_client_id string

param web_containerimage string

param web_containerport string

param project_outputs_endpoint string

param web_identity_outputs_id string

param web_identity_outputs_clientid string

param env_outputs_azure_app_service_dashboard_uri string

param env_outputs_azure_website_contributor_managed_identity_id string

param env_outputs_azure_website_contributor_managed_identity_principal_id string

resource mainContainer 'Microsoft.Web/sites/sitecontainers@2025-03-01' = {
  name: 'main'
  properties: {
    authType: 'UserAssigned'
    image: web_containerimage
    isMain: true
    targetPort: web_containerport
    userManagedIdentityClientId: env_outputs_azure_container_registry_managed_identity_client_id
  }
  parent: webapp
}

resource webapp 'Microsoft.Web/sites@2025-03-01' = {
  name: take('${toLower('web')}-${uniqueString(resourceGroup().id)}', 60)
  location: location
  properties: {
    serverFarmId: env_outputs_planid
    keyVaultReferenceIdentity: web_identity_outputs_id
    siteConfig: {
      numberOfWorkers: 30
      linuxFxVersion: 'SITECONTAINERS'
      acrUseManagedIdentityCreds: true
      acrUserManagedIdentityID: env_outputs_azure_container_registry_managed_identity_client_id
      appSettings: [
        {
          name: 'WEBSITES_PORT'
          value: web_containerport
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
          value: web_containerport
        }
        {
          name: 'AGENT_HTTP'
          value: '${project_outputs_endpoint}/agents/agent-ha'
        }
        {
          name: 'services__agent__http__0'
          value: '${project_outputs_endpoint}/agents/agent-ha'
        }
        {
          name: 'AGENT_URL'
          value: '${project_outputs_endpoint}/agents/agent-ha'
        }
        {
          name: 'AZURE_CLIENT_ID'
          value: web_identity_outputs_clientid
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
          value: 'web'
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
      '${web_identity_outputs_id}': { }
    }
  }
}

resource web_website_ra 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
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
      'AGENT_HTTP'
      'services__agent__http__0'
      'OTEL_SERVICE_NAME'
    ]
  }
  parent: webapp
}