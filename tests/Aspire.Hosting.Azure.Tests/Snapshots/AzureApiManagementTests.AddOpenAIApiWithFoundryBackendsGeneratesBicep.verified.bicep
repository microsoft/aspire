@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param _apim_backendUrl_chat_primary_backend string

param _apim_backendUrl_chat_secondary_backend string

param foundry_primary_outputs_name string

param foundry_secondary_outputs_name string

resource apim 'Microsoft.ApiManagement/service@2025-03-01-preview' = {
  name: take('apim${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    publisherEmail: 'api-owners@example.com'
    publisherName: 'Aspire'
    virtualNetworkType: 'None'
  }
  sku: {
    name: 'StandardV2'
    capacity: 1
  }
  identity: {
    type: 'SystemAssigned'
  }
  tags: {
    'aspire-resource-name': 'apim'
  }
}

resource chat_primary_backend 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  name: 'chat-primary-backend'
  properties: {
    protocol: 'http'
    url: _apim_backendUrl_chat_primary_backend
    title: 'chat-primary-backend'
    type: 'Single'
    tls: {
      validateCertificateChain: true
      validateCertificateName: true
    }
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
  }
  parent: apim
}

resource chat_secondary_backend 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  name: 'chat-secondary-backend'
  properties: {
    protocol: 'http'
    url: _apim_backendUrl_chat_secondary_backend
    title: 'chat-secondary-backend'
    type: 'Single'
    tls: {
      validateCertificateChain: true
      validateCertificateName: true
    }
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
  }
  parent: apim
}

resource openai_pool 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  name: 'openai-pool'
  properties: {
    title: 'openai-pool'
    type: 'Pool'
    pool: {
      services: [
        {
          id: chat_primary_backend.id
          priority: 1
          weight: 3
        }
        {
          id: chat_secondary_backend.id
          priority: 2
          weight: 1
        }
      ]
    }
  }
  parent: apim
  dependsOn: [
    chat_primary_backend
    chat_secondary_backend
  ]
}

resource foundry_primary 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = {
  name: foundry_primary_outputs_name
}

resource foundry_primary_CognitiveServicesUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundry_primary.id, apim.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908'))
  properties: {
    principalId: apim.identity.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
    principalType: 'ServicePrincipal'
  }
  scope: foundry_primary
}

resource foundry_secondary 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = {
  name: foundry_secondary_outputs_name
}

resource foundry_secondary_CognitiveServicesUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundry_secondary.id, apim.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908'))
  properties: {
    principalId: apim.identity.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
    principalType: 'ServicePrincipal'
  }
  scope: foundry_secondary
}

resource openai_api 'Microsoft.ApiManagement/service/apis@2024-05-01' = {
  name: 'openai-api'
  properties: {
    displayName: 'openai-api'
    path: 'openai'
    subscriptionRequired: true
    type: 'http'
    protocols: [
      'https'
    ]
  }
  parent: apim
}

resource _apim_proxyOperation_openai_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy'
  properties: {
    displayName: 'Proxy'
    method: '*'
    urlTemplate: '/*'
  }
  parent: openai_api
}

resource openai_api_chat_completions 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'chat-completions'
  properties: {
    displayName: 'Create chat completion'
    method: 'POST'
    urlTemplate: '/chat/completions'
  }
  parent: openai_api
}

resource _apim_apiPolicy_openai_api 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = {
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: '<policies>\n  <inbound>\n    <base />\n    <authentication-managed-identity resource="https://cognitiveservices.azure.com" />\n    <set-backend-service backend-id="openai-pool" />\n  </inbound>\n  <backend><base /></backend>\n  <outbound><base /></outbound>\n  <on-error><base /></on-error>\n</policies>'
  }
  parent: openai_api
  dependsOn: [
    openai_pool
  ]
}

output gatewayUrl string = apim.properties.gatewayUrl

output name string = apim.name

output id string = apim.id

output principalId string = apim.identity.principalId