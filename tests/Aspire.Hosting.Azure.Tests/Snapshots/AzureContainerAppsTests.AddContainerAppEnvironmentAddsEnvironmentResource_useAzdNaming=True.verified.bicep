@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param userPrincipalId string = ''

param tags object = { }

param env_acr_outputs_name string

var resourceToken = uniqueString(resourceGroup().id)

resource env_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: 'mi-${resourceToken}'
  location: location
  tags: tags
}

resource env_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: replace('acr-${resourceToken}', '-', '')
}

resource env_acr_env_mi_AcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(env_acr.id, env_mi.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))
  properties: {
    principalId: env_mi.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
  scope: env_acr
}

resource env_law 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: 'law-${resourceToken}'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
  }
  tags: tags
}

resource env 'Microsoft.App/managedEnvironments@2025-07-01' = {
  name: 'cae-${resourceToken}'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: env_law.properties.customerId
        sharedKey: env_law.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [
      {
        name: 'consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
  tags: tags
}

resource aspireDashboard 'Microsoft.App/managedEnvironments/dotNetComponents@2025-10-02-preview' = {
  name: 'aspire-dashboard'
  properties: {
    componentType: 'AspireDashboard'
  }
  parent: env
}

resource env_storageVolume 'Microsoft.Storage/storageAccounts@2024-01-01' = {
  name: 'vol${resourceToken}'
  kind: 'StorageV2'
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    largeFileSharesState: 'Enabled'
    minimumTlsVersion: 'TLS1_2'
  }
  tags: tags
}

resource storageVolumeFileService 'Microsoft.Storage/storageAccounts/fileServices@2024-01-01' = {
  name: 'default'
  parent: env_storageVolume
}

resource shares_volumes_cache_0 'Microsoft.Storage/storageAccounts/fileServices/shares@2024-01-01' = {
  name: take('${toLower('cache')}-${toLower('Appdata')}', 60)
  properties: {
    enabledProtocols: 'SMB'
    shareQuota: 1024
  }
  parent: storageVolumeFileService
}

resource managedStorage_volumes_cache_0 'Microsoft.App/managedEnvironments/storages@2025-07-01' = {
  name: take('${toLower('cache')}-${toLower('Appdata')}', 32)
  properties: {
    azureFile: {
      accountName: env_storageVolume.name
      accountKey: env_storageVolume.listKeys().keys[0].value
      accessMode: 'ReadWrite'
      shareName: shares_volumes_cache_0.name
    }
  }
  parent: env
}

output volumes_cache_0 string = managedStorage_volumes_cache_0.name

output AZURE_LOG_ANALYTICS_WORKSPACE_NAME string = env_law.name

output AZURE_LOG_ANALYTICS_WORKSPACE_ID string = env_law.id

output AZURE_CONTAINER_REGISTRY_NAME string = env_acr.name

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = env_acr.properties.loginServer

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = env_mi.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_NAME string = env.name

output AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = env.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = env.properties.defaultDomain