// Resource: apim
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param apim_identity_outputs_id string

param vault_outputs_name string

param apim_identity_outputs_clientid string

param _apim_backendUrl_openai_backend string

param _apim_backendUrl_foundry_backend string

param _apim_backendUrl_blob_backend string

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: vault_outputs_name
}

resource vault_gateway_certificate 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = {
  name: 'gateway-certificate'
  parent: vault
}

resource apim 'Microsoft.ApiManagement/service@2025-03-01-preview' = {
  name: take('apim${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    publisherEmail: 'api-owners@example.com'
    publisherName: 'Contoso APIs'
    hostnameConfigurations: [
      {
        type: 'Proxy'
        hostName: 'api.contoso.example'
        keyVaultId: '${vault.properties.vaultUri}secrets/gateway-certificate'
        identityClientId: apim_identity_outputs_clientid
        defaultSslBinding: false
        negotiateClientCertificate: false
      }
    ]
    virtualNetworkType: 'None'
  }
  sku: {
    name: 'Developer'
    capacity: 1
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${apim_identity_outputs_id}': { }
    }
  }
  tags: {
    'aspire-resource-name': 'apim'
  }
}

resource vault_upstream_secret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = {
  name: 'upstream-secret'
  parent: vault
}

resource upstream_secret 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  name: 'upstream-secret'
  properties: {
    displayName: 'upstream-secret'
    secret: true
    keyVault: {
      secretIdentifier: '${vault.properties.vaultUri}secrets/upstream-secret'
      identityClientId: apim_identity_outputs_clientid
    }
  }
  parent: apim
}

resource vault_another_secret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = {
  name: 'another-secret'
  parent: vault
}

resource another_secret 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  name: 'another-secret'
  properties: {
    displayName: 'another-secret'
    secret: true
    keyVault: {
      secretIdentifier: '${vault.properties.vaultUri}secrets/another-secret'
      identityClientId: apim_identity_outputs_clientid
    }
  }
  parent: apim
}

resource openai_backend 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  name: 'openai-backend'
  properties: {
    circuitBreaker: {
      rules: [
        {
          name: 'openAIThrottling'
          failureCondition: {
            count: 1
            interval: 'PT10S'
            statusCodeRanges: [
              {
                min: 429
                max: 429
              }
            ]
          }
          tripDuration: 'PT10S'
          acceptRetryAfter: true
        }
      ]
    }
    protocol: 'http'
    title: 'openai-backend'
    tls: {
      validateCertificateChain: true
      validateCertificateName: true
    }
    type: 'Single'
    url: _apim_backendUrl_openai_backend
  }
  parent: apim
}

resource foundry_backend 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  name: 'foundry-backend'
  properties: {
    circuitBreaker: {
      rules: [
        {
          name: 'openAIThrottling'
          failureCondition: {
            count: 1
            interval: 'PT10S'
            statusCodeRanges: [
              {
                min: 429
                max: 429
              }
            ]
          }
          tripDuration: 'PT10S'
          acceptRetryAfter: true
        }
      ]
    }
    protocol: 'http'
    title: 'foundry-backend'
    tls: {
      validateCertificateChain: true
      validateCertificateName: true
    }
    type: 'Single'
    url: _apim_backendUrl_foundry_backend
  }
  parent: apim
}

resource blob_backend 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  name: 'blob-backend'
  properties: {
    protocol: 'http'
    title: 'blob-backend'
    tls: {
      validateCertificateChain: true
      validateCertificateName: true
    }
    type: 'Single'
    url: _apim_backendUrl_blob_backend
  }
  parent: apim
}

output gatewayUrl string = apim.properties.gatewayUrl

output name string = apim.name

output id string = apim.id

// Resource: apim-identity
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource apim_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('apim_identity-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

output id string = apim_identity.id

output clientId string = apim_identity.properties.clientId

output principalId string = apim_identity.properties.principalId

output principalName string = apim_identity.name

output name string = apim_identity.name

// Resource: apim-identity-roles-foundry
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param foundry_outputs_name string

param principalId string

resource foundry 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = {
  name: foundry_outputs_name
}

resource foundry_CognitiveServicesUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundry.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
    principalType: 'ServicePrincipal'
  }
  scope: foundry
}

// Resource: apim-identity-roles-openai
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param openai_outputs_name string

param principalId string

resource openai 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = {
  name: openai_outputs_name
}

resource openai_CognitiveServicesOpenAIUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openai.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalType: 'ServicePrincipal'
  }
  scope: openai
}

// Resource: apim-identity-roles-storage
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param storage_outputs_name string

param principalId string

resource storage 'Microsoft.Storage/storageAccounts@2026-06-01' existing = {
  name: storage_outputs_name
}

resource storage_StorageBlobDataReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
    principalType: 'ServicePrincipal'
  }
  scope: storage
}

// Resource: apim-identity-roles-vault
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param vault_outputs_name string

param principalId string

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: vault_outputs_name
}

resource vault_KeyVaultCertificateUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'db79e9a7-68ee-4b58-9aeb-b90e7c24fcba'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'db79e9a7-68ee-4b58-9aeb-b90e7c24fcba')
    principalType: 'ServicePrincipal'
  }
  scope: vault
}

resource vault_KeyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalType: 'ServicePrincipal'
  }
  scope: vault
}

// Resource: foundry
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource foundry 'Microsoft.CognitiveServices/accounts@2025-09-01' = {
  name: toLower('foundry-${uniqueString(resourceGroup().id)}')
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  kind: 'AIServices'
  properties: {
    customSubDomainName: toLower('foundry-${uniqueString(resourceGroup().id)}')
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
    allowProjectManagement: true
  }
  sku: {
    name: 'S0'
  }
  tags: {
    'aspire-resource-name': 'foundry'
  }
}

resource foundry_caphost 'Microsoft.CognitiveServices/accounts/capabilityHosts@2025-10-01-preview' = {
  name: 'foundry-caphost'
  properties: {
    capabilityHostKind: 'Agents'
    enablePublicHostingEnvironment: true
  }
  parent: foundry
}

resource foundry_chat 'Microsoft.CognitiveServices/accounts/deployments@2025-09-01' = {
  name: 'foundry-chat'
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-5-mini'
      version: '2025-08-07'
    }
  }
  sku: {
    name: 'GlobalStandard'
    capacity: 1
  }
  parent: foundry
}

output aiFoundryApiEndpoint string = foundry.properties.endpoints['AI Foundry API']

output endpoint string = foundry.properties.endpoint

output name string = foundry.name

output id string = foundry.id

// Resource: openai
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource openai 'Microsoft.CognitiveServices/accounts@2025-09-01' = {
  name: take('openai-${uniqueString(resourceGroup().id)}', 64)
  location: location
  kind: 'OpenAI'
  properties: {
    customSubDomainName: toLower(take(concat('openai', uniqueString(resourceGroup().id)), 24))
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
  }
  sku: {
    name: 'S0'
  }
  tags: {
    'aspire-resource-name': 'openai'
  }
}

resource chat 'Microsoft.CognitiveServices/accounts/deployments@2025-09-01' = {
  name: 'chat'
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-5-mini'
      version: '2025-08-07'
    }
  }
  sku: {
    name: 'Standard'
    capacity: 8
  }
  parent: openai
}

output connectionString string = 'Endpoint=${openai.properties.endpoint}'

output endpoint string = openai.properties.endpoint

output name string = openai.name

output id string = openai.id

// Resource: storage
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource storage 'Microsoft.Storage/storageAccounts@2026-06-01' = {
  name: take('storage${uniqueString(resourceGroup().id)}', 24)
  tags: {
    'aspire-resource-name': 'storage'
  }
  location: location
  properties: {
    isHnsEnabled: false
    minimumTlsVersion: 'TLS1_2'
    allowSharedKeyAccess: false
    accessTier: 'Hot'
    networkAcls: {
      defaultAction: 'Allow'
    }
  }
  sku: {
    name: 'Standard_GRS'
  }
  kind: 'StorageV2'
}

output blobEndpoint string = storage.properties.primaryEndpoints.blob

output dataLakeEndpoint string = storage.properties.primaryEndpoints.dfs

output queueEndpoint string = storage.properties.primaryEndpoints.queue

output tableEndpoint string = storage.properties.primaryEndpoints.table

output name string = storage.name

output id string = storage.id

// Resource: vault
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: take('vault-${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
  }
  tags: {
    'aspire-resource-name': 'vault'
  }
}

output vaultUri string = vault.properties.vaultUri

output name string = vault.name

output id string = vault.id

