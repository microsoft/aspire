// Resource: api-identity
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource api_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('api_identity-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

output id string = api_identity.id

output clientId string = api_identity.properties.clientId

output principalId string = api_identity.properties.principalId

output principalName string = api_identity.name

output name string = api_identity.name

// Resource: api-roles-sql
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param sql_outputs_name string

param sql_outputs_sqlserveradminname string

param myvnet_outputs_acisubnet_id string

param sql_store_outputs_name string

param principalId string

param principalName string

param pesubnet_sql_pe_outputs_name string

param pesubnet_files_pe_outputs_name string

resource sql 'Microsoft.Sql/servers@2023-08-01' existing = {
  name: sql_outputs_name
}

resource sqlServerAdmin 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: sql_outputs_sqlserveradminname
}

resource sql_store 'Microsoft.Storage/storageAccounts@2024-01-01' existing = {
  name: sql_store_outputs_name
}

resource mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: principalName
}

resource pesubnet_sql_pe 'Microsoft.Network/privateEndpoints@2025-05-01' existing = {
  name: pesubnet_sql_pe_outputs_name
}

resource pesubnet_files_pe 'Microsoft.Network/privateEndpoints@2025-05-01' existing = {
  name: pesubnet_files_pe_outputs_name
}

resource script_sql_db 'Microsoft.Resources/deploymentScripts@2023-08-01' = {
  name: take('script-${uniqueString('sql', principalName, 'db', resourceGroup().id)}', 24)
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${sqlServerAdmin.id}': { }
    }
  }
  kind: 'AzurePowerShell'
  properties: {
    azPowerShellVersion: '14.0'
    retentionInterval: 'PT1H'
    containerSettings: {
      subnetIds: [
        {
          id: myvnet_outputs_acisubnet_id
        }
      ]
    }
    environmentVariables: [
      {
        name: 'DBNAME'
        value: 'db'
      }
      {
        name: 'DBSERVER'
        value: sql.properties.fullyQualifiedDomainName
      }
      {
        name: 'PRINCIPALTYPE'
        value: 'ServicePrincipal'
      }
      {
        name: 'PRINCIPALNAME'
        value: principalName
      }
      {
        name: 'ID'
        value: mi.properties.clientId
      }
    ]
    scriptContent: '\$sqlServerFqdn = "\$env:DBSERVER"\n\$sqlDatabaseName = "\$env:DBNAME"\n\$principalName = "\$env:PRINCIPALNAME"\n\$id = "\$env:ID"\n\n# Install SqlServer module - using specific version to avoid breaking changes in 22.4.5.1 (see https://github.com/microsoft/aspire/issues/9926)\nInstall-Module -Name SqlServer -RequiredVersion 22.3.0 -Force -AllowClobber -Scope CurrentUser\nImport-Module SqlServer\n\n\$sqlCmd = @"\nDECLARE @name SYSNAME = \'\$principalName\';\nDECLARE @id UNIQUEIDENTIFIER = \'\$id\';\n\n-- Convert the guid to the right type\nDECLARE @castId NVARCHAR(MAX) = CONVERT(VARCHAR(MAX), CONVERT (VARBINARY(16), @id), 1);\n\n-- Construct command: CREATE USER [@name] WITH SID = @castId, TYPE = E;\nDECLARE @cmd NVARCHAR(MAX) = N\'CREATE USER [\' + @name + \'] WITH SID = \' + @castId + \', TYPE = E;\'\nEXEC (@cmd);\n\n-- Assign roles to the new user\nDECLARE @role1 NVARCHAR(MAX) = N\'ALTER ROLE db_owner ADD MEMBER [\' + @name + \']\';\nEXEC (@role1);\n\n"@\n# Note: the string terminator must not have whitespace before it, therefore it is not indented.\n\nWrite-Host \$sqlCmd\n\n\$connectionString = "Server=tcp:\${sqlServerFqdn},1433;Initial Catalog=\${sqlDatabaseName};Authentication=Active Directory Default;"\n\n\$maxRetries = 5\n\$retryDelay = 60\n\$attempt = 0\n\$success = \$false\n\nwhile (-not \$success -and \$attempt -lt \$maxRetries) {\n    \$attempt++\n    Write-Host "Attempt \$attempt of \$maxRetries..."\n    try {\n        Invoke-Sqlcmd -ConnectionString \$connectionString -Query \$sqlCmd\n        \$success = \$true\n        Write-Host "SQL command succeeded on attempt \$attempt."\n    } catch {\n        Write-Host "Attempt \$attempt failed: \$_"\n        if (\$attempt -lt \$maxRetries) {\n            Write-Host "Retrying in \$retryDelay seconds..."\n            Start-Sleep -Seconds \$retryDelay\n        } else {\n            throw\n        }\n    }\n}'
    storageAccountSettings: {
      storageAccountName: sql_store_outputs_name
    }
  }
  dependsOn: [
    pesubnet_sql_pe
    pesubnet_files_pe
  ]
}

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

resource env 'Microsoft.App/managedEnvironments@2025-01-01' = {
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

resource aspireDashboard 'Microsoft.App/managedEnvironments/dotNetComponents@2024-10-02-preview' = {
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

resource acisubnet 'Microsoft.Network/virtualNetworks/subnets@2025-05-01' = {
  name: 'acisubnet'
  properties: {
    addressPrefix: '10.0.2.0/29'
    delegations: [
      {
        properties: {
          serviceName: 'Microsoft.ContainerInstance/containerGroups'
        }
        name: 'Microsoft.ContainerInstance/containerGroups'
      }
    ]
  }
  parent: myvnet
  dependsOn: [
    pesubnet
  ]
}

output pesubnet_Id string = pesubnet.id

output acisubnet_Id string = acisubnet.id

output id string = myvnet.id

output name string = myvnet.name

// Resource: pesubnet-files-pe
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param privatelink_file_core_windows_net_outputs_name string

param myvnet_outputs_pesubnet_id string

param sql_store_outputs_id string

resource privatelink_file_core_windows_net 'Microsoft.Network/privateDnsZones@2024-06-01' existing = {
  name: privatelink_file_core_windows_net_outputs_name
}

resource pesubnet_files_pe 'Microsoft.Network/privateEndpoints@2025-05-01' = {
  name: take('pesubnet_files_pe-${uniqueString(resourceGroup().id)}', 64)
  location: location
  properties: {
    privateLinkServiceConnections: [
      {
        properties: {
          privateLinkServiceId: sql_store_outputs_id
          groupIds: [
            'file'
          ]
        }
        name: 'pesubnet-files-pe-connection'
      }
    ]
    subnet: {
      id: myvnet_outputs_pesubnet_id
    }
  }
  tags: {
    'aspire-resource-name': 'pesubnet-files-pe'
  }
}

resource pesubnet_files_pe_dnsgroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2025-05-01' = {
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'privatelink_file_core_windows_net'
        properties: {
          privateDnsZoneId: privatelink_file_core_windows_net.id
        }
      }
    ]
  }
  parent: pesubnet_files_pe
}

output id string = pesubnet_files_pe.id

output name string = pesubnet_files_pe.name

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

// Resource: privatelink-file-core-windows-net
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param myvnet_outputs_id string

resource privatelink_file_core_windows_net 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: 'privatelink.file.core.windows.net'
  location: 'global'
  tags: {
    'aspire-resource-name': 'privatelink-file-core-windows-net'
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
    'aspire-resource-name': 'privatelink-file-core-windows-net-myvnet-link'
  }
  parent: privatelink_file_core_windows_net
}

output id string = privatelink_file_core_windows_net.id

output name string = 'privatelink.file.core.windows.net'

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

// Resource: sql-admin-identity
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param sql_outputs_sqlserveradminname string

resource sql_admin_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: sql_outputs_sqlserveradminname
}

output id string = sql_admin_identity.id

output clientId string = sql_admin_identity.properties.clientId

output principalId string = sql_admin_identity.properties.principalId

output principalName string = sql_admin_identity.name

output name string = sql_admin_identity.name

// Resource: sql-admin-identity-roles-sql-store
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param sql_store_outputs_name string

param principalId string

resource sql_store 'Microsoft.Storage/storageAccounts@2024-01-01' existing = {
  name: sql_store_outputs_name
}

resource sql_store_StorageFileDataPrivilegedContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(sql_store.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69566ab7-960f-475b-8e7c-b3118f30c6bd'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69566ab7-960f-475b-8e7c-b3118f30c6bd')
    principalType: 'ServicePrincipal'
  }
  scope: sql_store
}

// Resource: sql-store
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource sql_store 'Microsoft.Storage/storageAccounts@2024-01-01' = {
  name: take('sqlstore${uniqueString(resourceGroup().id)}', 24)
  kind: 'StorageV2'
  location: location
  sku: {
    name: 'Standard_GRS'
  }
  properties: {
    accessTier: 'Hot'
    allowSharedKeyAccess: true
    isHnsEnabled: false
    minimumTlsVersion: 'TLS1_2'
    networkAcls: {
      defaultAction: 'Deny'
    }
    publicNetworkAccess: 'Disabled'
  }
  tags: {
    'aspire-resource-name': 'sql-store'
  }
}

output blobEndpoint string = sql_store.properties.primaryEndpoints.blob

output dataLakeEndpoint string = sql_store.properties.primaryEndpoints.dfs

output queueEndpoint string = sql_store.properties.primaryEndpoints.queue

output tableEndpoint string = sql_store.properties.primaryEndpoints.table

output name string = sql_store.name

output id string = sql_store.id

