@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param userPrincipalId string = ''

param tags object = { }

param env_1_acr_outputs_name string

resource env_1_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('env_1_mi-${uniqueString(resourceGroup().id)}', 128)
  location: location
  tags: tags
}

resource env_1_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: env_1_acr_outputs_name
}

resource env_1_acr_env_1_mi_AcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(env_1_acr.id, env_1_mi.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))
  properties: {
    principalId: env_1_mi.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
  scope: env_1_acr
}

resource env_1_asplan 'Microsoft.Web/serverfarms@2025-03-01' = {
  name: take('env1asplan-${uniqueString(resourceGroup().id)}', 60)
  location: location
  properties: {
    perSiteScaling: true
    reserved: true
  }
  kind: 'Linux'
  sku: {
    name: 'P0V3'
    tier: 'Premium'
  }
}

resource env_1_contributor_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('env_1_contributor_mi-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

resource env_1_ra 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, env_1_contributor_mi.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'acdd72a7-3385-48ef-bd42-f606fba81ae7'))
  properties: {
    principalId: env_1_contributor_mi.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'acdd72a7-3385-48ef-bd42-f606fba81ae7')
    principalType: 'ServicePrincipal'
  }
}

resource dashboard 'Microsoft.Web/sites@2025-03-01' = {
  name: take('${toLower('env-1')}-${toLower('aspiredashboard')}-${uniqueString(resourceGroup().id)}', 60)
  location: location
  properties: {
    serverFarmId: env_1_asplan.id
    siteConfig: {
      numberOfWorkers: 1
      linuxFxVersion: 'ASPIREDASHBOARD|1.0'
      acrUseManagedIdentityCreds: true
      acrUserManagedIdentityID: env_1_mi.properties.clientId
      appSettings: [
        {
          name: 'DASHBOARD__FRONTEND__AUTHMODE'
          value: 'Unsecured'
        }
        {
          name: 'DASHBOARD__OTLP__AUTHMODE'
          value: 'Unsecured'
        }
        {
          name: 'DASHBOARD__OTLP__SUPPRESSUNSECUREDTELEMETRYMESSAGE'
          value: 'true'
        }
        {
          name: 'DASHBOARD__RESOURCESERVICECLIENT__AUTHMODE'
          value: 'Unsecured'
        }
        {
          name: 'DASHBOARD__UI__DISABLEIMPORT'
          value: 'true'
        }
        {
          name: 'WEBSITES_PORT'
          value: '5000'
        }
        {
          name: 'HTTP20_ONLY_PORT'
          value: '4317'
        }
        {
          name: 'WEBSITE_START_SCM_WITH_PRELOAD'
          value: 'true'
        }
        {
          name: 'AZURE_CLIENT_ID'
          value: env_1_contributor_mi.properties.clientId
        }
        {
          name: 'ALLOWED_MANAGED_IDENTITIES'
          value: env_1_mi.properties.clientId
        }
        {
          name: 'ASPIRE_ENVIRONMENT_NAME'
          value: 'env-1'
        }
      ]
      alwaysOn: true
      http20Enabled: true
      http20ProxyFlag: 1
    }
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${env_1_contributor_mi.id}': { }
    }
  }
  kind: 'app,linux,aspiredashboard'
}

resource env_1_law 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: take('env1law-${uniqueString(resourceGroup().id)}', 63)
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
  }
}

resource env_1_ai 'Microsoft.Insights/components@2020-02-02' = {
  name: take('env_1_ai-${uniqueString(resourceGroup().id)}', 260)
  kind: 'web'
  location: location
  properties: {
    Application_Type: 'web'
    IngestionMode: 'LogAnalytics'
    WorkspaceResourceId: env_1_law.id
  }
}

output name string = env_1_asplan.name

output planId string = env_1_asplan.id

output webSiteSuffix string = uniqueString(resourceGroup().id)

output AZURE_CONTAINER_REGISTRY_NAME string = env_1_acr.name

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = env_1_acr.properties.loginServer

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = env_1_mi.id

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID string = env_1_mi.properties.clientId

output AZURE_WEBSITE_CONTRIBUTOR_MANAGED_IDENTITY_ID string = env_1_contributor_mi.id

output AZURE_WEBSITE_CONTRIBUTOR_MANAGED_IDENTITY_PRINCIPAL_ID string = env_1_contributor_mi.properties.principalId

output AZURE_APP_SERVICE_DASHBOARD_URI string = 'https://${take('${toLower('env-1')}-${toLower('aspiredashboard')}-${uniqueString(resourceGroup().id)}', 60)}.azurewebsites.net'

output AZURE_APPLICATION_INSIGHTS_INSTRUMENTATIONKEY string = env_1_ai.properties.InstrumentationKey

output AZURE_APPLICATION_INSIGHTS_CONNECTION_STRING string = env_1_ai.properties.ConnectionString