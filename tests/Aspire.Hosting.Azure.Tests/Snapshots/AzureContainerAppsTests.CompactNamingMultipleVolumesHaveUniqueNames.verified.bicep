@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param userPrincipalId string = ''

param tags object = { }

param my_ace_acr_outputs_name string

var resourceToken = uniqueString(resourceGroup().id)

resource my_ace_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('my_ace_mi-${uniqueString(resourceGroup().id)}', 128)
  location: location
  tags: tags
}

resource my_ace_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: my_ace_acr_outputs_name
}

resource my_ace_acr_my_ace_mi_AcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(my_ace_acr.id, my_ace_mi.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))
  properties: {
    principalId: my_ace_mi.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
  scope: my_ace_acr
}

resource my_ace_law 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: take('myacelaw-${uniqueString(resourceGroup().id)}', 63)
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
  }
  tags: tags
}

resource my_ace 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: take('myace${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: my_ace_law.properties.customerId
        sharedKey: my_ace_law.listKeys().primarySharedKey
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

resource aspireDashboard 'Microsoft.App/managedEnvironments/dotNetComponents@2024-10-02-preview' = {
  name: 'aspire-dashboard'
  properties: {
    componentType: 'AspireDashboard'
  }
  parent: my_ace
}

resource my_ace_storageVolume 'Microsoft.Storage/storageAccounts@2024-01-01' = {
  name: take('myacesv${resourceToken}', 24)
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
  parent: my_ace_storageVolume
}

resource shares_volumes_druid_0 'Microsoft.Storage/storageAccounts/fileServices/shares@2024-01-01' = {
  name: take('${toLower('druid')}-${toLower('druid_shared')}', 60)
  properties: {
    enabledProtocols: 'SMB'
    shareQuota: 1024
  }
  parent: storageVolumeFileService
}

resource managedStorage_volumes_druid_0 'Microsoft.App/managedEnvironments/storages@2025-01-01' = {
  name: take('${toLower('druid')}-${toLower('druid_shared')}-${resourceToken}', 32)
  properties: {
    azureFile: {
      accountName: my_ace_storageVolume.name
      accountKey: my_ace_storageVolume.listKeys().keys[0].value
      accessMode: 'ReadWrite'
      shareName: shares_volumes_druid_0.name
    }
  }
  parent: my_ace
}

resource shares_volumes_druid_1 'Microsoft.Storage/storageAccounts/fileServices/shares@2024-01-01' = {
  name: take('${toLower('druid')}-${toLower('coordinator_var')}', 60)
  properties: {
    enabledProtocols: 'SMB'
    shareQuota: 1024
  }
  parent: storageVolumeFileService
}

resource managedStorage_volumes_druid_1 'Microsoft.App/managedEnvironments/storages@2025-01-01' = {
  name: take('${toLower('druid')}-${toLower('coordinator_var')}-${resourceToken}', 32)
  properties: {
    azureFile: {
      accountName: my_ace_storageVolume.name
      accountKey: my_ace_storageVolume.listKeys().keys[0].value
      accessMode: 'ReadWrite'
      shareName: shares_volumes_druid_1.name
    }
  }
  parent: my_ace
}

resource shares_bindmounts_druid_0 'Microsoft.Storage/storageAccounts/fileServices/shares@2024-01-01' = {
  name: take('${toLower('druid')}-${toLower('bm0')}', 60)
  properties: {
    enabledProtocols: 'SMB'
    shareQuota: 1024
  }
  parent: storageVolumeFileService
}

resource managedStorage_bindmounts_druid_0 'Microsoft.App/managedEnvironments/storages@2025-01-01' = {
  name: take('${toLower('druid')}-${toLower('bm0')}-${resourceToken}', 32)
  properties: {
    azureFile: {
      accountName: my_ace_storageVolume.name
      accountKey: my_ace_storageVolume.listKeys().keys[0].value
      accessMode: 'ReadWrite'
      shareName: shares_bindmounts_druid_0.name
    }
  }
  parent: my_ace
}

output volumes_druid_0 string = managedStorage_volumes_druid_0.name

output volumes_druid_1 string = managedStorage_volumes_druid_1.name

output bindmounts_druid_0 string = managedStorage_bindmounts_druid_0.name

output AZURE_LOG_ANALYTICS_WORKSPACE_NAME string = my_ace_law.name

output AZURE_LOG_ANALYTICS_WORKSPACE_ID string = my_ace_law.id

output AZURE_CONTAINER_REGISTRY_NAME string = my_ace_acr.name

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = my_ace_acr.properties.loginServer

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = my_ace_mi.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_NAME string = my_ace.name

output AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = my_ace.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = my_ace.properties.defaultDomain