// Resource: env
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param userPrincipalId string = ''

param tags object = { }

param env_acr_outputs_name string

resource env_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('env_mi-${uniqueString(resourceGroup().id)}', 128)
  location: location
  tags: tags
}

resource env_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: env_acr_outputs_name
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
  name: take('envlaw-${uniqueString(resourceGroup().id)}', 63)
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
  }
  tags: tags
}

resource env 'Microsoft.App/managedEnvironments@2025-07-01' = {
  name: take('env${uniqueString(resourceGroup().id)}', 24)
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

output AZURE_LOG_ANALYTICS_WORKSPACE_NAME string = env_law.name

output AZURE_LOG_ANALYTICS_WORKSPACE_ID string = env_law.id

output AZURE_CONTAINER_REGISTRY_NAME string = env_acr.name

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = env_acr.properties.loginServer

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = env_mi.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_NAME string = env.name

output AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = env.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = env.properties.defaultDomain

// Resource: env-acr
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource env_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: take('envacr${uniqueString(resourceGroup().id)}', 50)
  location: location
  sku: {
    name: 'Basic'
  }
  tags: {
    'aspire-resource-name': 'env-acr'
  }
}

output name string = env_acr.name

output loginServer string = env_acr.properties.loginServer

output id string = env_acr.id

// Resource: myvnet
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource myvnet 'Microsoft.Network/virtualNetworks@2025-05-01' = {
  name: take('myvnet-${uniqueString(resourceGroup().id)}', 64)
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.0.0.0/16'
      ]
    }
  }
  location: location
  tags: {
    'aspire-resource-name': 'myvnet'
  }
}

resource pesubnet 'Microsoft.Network/virtualNetworks/subnets@2025-05-01' = {
  name: 'pesubnet'
  properties: {
    addressPrefix: '10.0.1.0/24'
  }
  parent: myvnet
}

output pesubnet_Id string = pesubnet.id

output id string = myvnet.id

output name string = myvnet.name

// Resource: pesubnet-sql-pe
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param privatelink_database_windows_net_outputs_name string

param myvnet_outputs_pesubnet_id string

param sql_outputs_id string

resource privatelink_database_windows_net 'Microsoft.Network/privateDnsZones@2024-06-01' existing = {
  name: privatelink_database_windows_net_outputs_name
}

resource pesubnet_sql_pe 'Microsoft.Network/privateEndpoints@2025-05-01' = {
  name: take('pesubnet_sql_pe-${uniqueString(resourceGroup().id)}', 64)
  location: location
  properties: {
    privateLinkServiceConnections: [
      {
        properties: {
          privateLinkServiceId: sql_outputs_id
          groupIds: [
            'sqlServer'
          ]
        }
        name: 'pesubnet-sql-pe-connection'
      }
    ]
    subnet: {
      id: myvnet_outputs_pesubnet_id
    }
  }
  tags: {
    'aspire-resource-name': 'pesubnet-sql-pe'
  }
}

resource pesubnet_sql_pe_dnsgroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2025-05-01' = {
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'privatelink_database_windows_net'
        properties: {
          privateDnsZoneId: privatelink_database_windows_net.id
        }
      }
    ]
  }
  parent: pesubnet_sql_pe
}

output id string = pesubnet_sql_pe.id

output name string = pesubnet_sql_pe.name

// Resource: privatelink-database-windows-net
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param myvnet_outputs_id string

resource privatelink_database_windows_net 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: 'privatelink.database.windows.net'
  location: 'global'
  tags: {
    'aspire-resource-name': 'privatelink-database-windows-net'
  }
}

resource myvnet_link 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  name: 'myvnet-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: myvnet_outputs_id
    }
  }
  tags: {
    'aspire-resource-name': 'privatelink-database-windows-net-myvnet-link'
  }
  parent: privatelink_database_windows_net
}

output id string = privatelink_database_windows_net.id

output name string = 'privatelink.database.windows.net'

// Resource: sql
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource sqlServerAdminManagedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('sql-admin-${uniqueString(resourceGroup().id)}', 63)
  location: location
}

resource sql 'Microsoft.Sql/servers@2023-08-01' = {
  name: take('sql-${uniqueString(resourceGroup().id)}', 63)
  location: location
  properties: {
    administrators: {
      administratorType: 'ActiveDirectory'
      login: sqlServerAdminManagedIdentity.name
      sid: sqlServerAdminManagedIdentity.properties.principalId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
    }
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
    version: '12.0'
  }
  tags: {
    'aspire-resource-name': 'sql'
  }
}

resource db 'Microsoft.Sql/servers/databases@2023-08-01' = {
  name: 'db'
  location: location
  properties: {
    freeLimitExhaustionBehavior: 'AutoPause'
    useFreeLimit: true
  }
  sku: {
    name: 'GP_S_Gen5_2'
  }
  parent: sql
}

output sqlServerFqdn string = sql.properties.fullyQualifiedDomainName

output name string = sql.name

output id string = sql.id

output sqlServerAdminName string = sql.properties.administrators.login

