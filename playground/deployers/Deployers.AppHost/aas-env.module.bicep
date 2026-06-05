@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param userPrincipalId string = ''

param tags object = { }

param aas_env_acr_outputs_name string

resource aas_env_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('aas_env_mi-${uniqueString(resourceGroup().id)}', 128)
  location: location
  tags: tags
}

resource aas_env_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: aas_env_acr_outputs_name
}

resource aas_env_acr_aas_env_mi_AcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aas_env_acr.id, aas_env_mi.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))
  properties: {
    principalId: aas_env_mi.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
  scope: aas_env_acr
}

resource aas_env_asplan 'Microsoft.Web/serverfarms@2025-03-01' = {
  name: take('aasenvasplan-${uniqueString(resourceGroup().id)}', 60)
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

resource aas_env_contributor_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('aas_env_contributor_mi-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

resource aas_env_ra 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, aas_env_contributor_mi.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'acdd72a7-3385-48ef-bd42-f606fba81ae7'))
  properties: {
    principalId: aas_env_contributor_mi.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'acdd72a7-3385-48ef-bd42-f606fba81ae7')
    principalType: 'ServicePrincipal'
  }
}

resource dashboard 'Microsoft.Web/sites@2025-03-01' = {
  name: take('${toLower('aas-env')}-${toLower('aspiredashboard')}-${uniqueString(resourceGroup().id)}', 60)
  location: location
  properties: {
    serverFarmId: aas_env_asplan.id
    siteConfig: {
      numberOfWorkers: 1
      linuxFxVersion: 'ASPIREDASHBOARD|1.0'
      acrUseManagedIdentityCreds: true
      acrUserManagedIdentityID: aas_env_mi.properties.clientId
      appSettings: [
        {
          name: 'Dashboard__Frontend__AuthMode'
          value: 'Unsecured'
        }
        {
          name: 'Dashboard__Otlp__AuthMode'
          value: 'Unsecured'
        }
        {
          name: 'Dashboard__Otlp__SuppressUnsecuredMessage'
          value: 'true'
        }
        {
          name: 'Dashboard__ResourceServiceClient__AuthMode'
          value: 'Unsecured'
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
          value: aas_env_contributor_mi.properties.clientId
        }
        {
          name: 'ALLOWED_MANAGED_IDENTITIES'
          value: aas_env_mi.properties.clientId
        }
        {
          name: 'ASPIRE_ENVIRONMENT_NAME'
          value: 'aas-env'
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
      '${aas_env_contributor_mi.id}': { }
    }
  }
  kind: 'app,linux,aspiredashboard'
}

output name string = aas_env_asplan.name

output planId string = aas_env_asplan.id

output webSiteSuffix string = uniqueString(resourceGroup().id)

output AZURE_CONTAINER_REGISTRY_NAME string = aas_env_acr.name

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = aas_env_acr.properties.loginServer

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = aas_env_mi.id

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID string = aas_env_mi.properties.clientId

output AZURE_WEBSITE_CONTRIBUTOR_MANAGED_IDENTITY_ID string = aas_env_contributor_mi.id

output AZURE_WEBSITE_CONTRIBUTOR_MANAGED_IDENTITY_PRINCIPAL_ID string = aas_env_contributor_mi.properties.principalId

output AZURE_APP_SERVICE_DASHBOARD_URI string = 'https://${take('${toLower('aas-env')}-${toLower('aspiredashboard')}-${uniqueString(resourceGroup().id)}', 60)}.azurewebsites.net'