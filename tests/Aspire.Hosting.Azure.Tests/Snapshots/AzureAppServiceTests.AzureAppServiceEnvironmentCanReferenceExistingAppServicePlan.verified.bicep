@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param userPrincipalId string = ''

param tags object = { }

param env_acr_pull_identity_outputs_id string

param env_acr_pull_identity_outputs_clientid string

param env_acr_outputs_name string

param appServicePlanName string

param appServicePlanResourceGroup string

resource env_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: env_acr_outputs_name
}

resource env 'Microsoft.Web/serverfarms@2025-03-01' existing = {
  name: appServicePlanName
  scope: resourceGroup(appServicePlanResourceGroup)
}

resource env_contributor_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('env_contributor_mi-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

resource env_ra 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, env_contributor_mi.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'acdd72a7-3385-48ef-bd42-f606fba81ae7'))
  properties: {
    principalId: env_contributor_mi.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'acdd72a7-3385-48ef-bd42-f606fba81ae7')
    principalType: 'ServicePrincipal'
  }
}

resource dashboard 'Microsoft.Web/sites@2025-03-01' = {
  name: take('${toLower('env')}-${toLower('aspiredashboard')}-${uniqueString(resourceGroup().id)}', 60)
  location: location
  properties: {
    serverFarmId: env.id
    siteConfig: {
      numberOfWorkers: 1
      linuxFxVersion: 'ASPIREDASHBOARD|1.0'
      acrUseManagedIdentityCreds: true
      acrUserManagedIdentityID: env_acr_pull_identity_outputs_clientid
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
          value: env_contributor_mi.properties.clientId
        }
        {
          name: 'ALLOWED_MANAGED_IDENTITIES'
          value: env_acr_pull_identity_outputs_clientid
        }
        {
          name: 'ASPIRE_ENVIRONMENT_NAME'
          value: 'env'
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
      '${env_contributor_mi.id}': { }
    }
  }
  kind: 'app,linux,aspiredashboard'
}

output name string = env.name

output planId string = env.id

output webSiteSuffix string = uniqueString(resourceGroup().id)

output AZURE_CONTAINER_REGISTRY_NAME string = env_acr.name

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = env_acr.properties.loginServer

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = env_acr_pull_identity_outputs_id

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID string = env_acr_pull_identity_outputs_clientid

output AZURE_WEBSITE_CONTRIBUTOR_MANAGED_IDENTITY_ID string = env_contributor_mi.id

output AZURE_WEBSITE_CONTRIBUTOR_MANAGED_IDENTITY_PRINCIPAL_ID string = env_contributor_mi.properties.principalId

output AZURE_APP_SERVICE_DASHBOARD_URI string = 'https://${take('${toLower('env')}-${toLower('aspiredashboard')}-${uniqueString(resourceGroup().id)}', 60)}.azurewebsites.net'