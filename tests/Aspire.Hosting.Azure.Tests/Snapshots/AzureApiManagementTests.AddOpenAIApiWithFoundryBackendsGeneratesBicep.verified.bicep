@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param openai_api_chat_primary_Backend_url string

param openai_api_chat_secondary_Backend_url string

param foundry_primary_outputs_name string

param foundry_secondary_outputs_name string

resource apim 'Microsoft.ApiManagement/service@2024-05-01' = {
  name: take('apim${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    publisherEmail: 'api-owners@example.com'
    publisherName: 'Aspire'
    publicNetworkAccess: 'Enabled'
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

resource openai_api_chat_primary_Backend 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  name: 'openai_api_chat_primary_Backend'
  properties: {
    protocol: 'http'
    url: openai_api_chat_primary_Backend_url
    title: 'chat-primary'
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

resource openai_api_chat_secondary_Backend 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  name: 'openai_api_chat_secondary_Backend'
  properties: {
    protocol: 'http'
    url: openai_api_chat_secondary_Backend_url
    title: 'chat-secondary'
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

resource openai_apiPool 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  name: 'openai_apiPool'
  properties: {
    title: 'openai-api'
    type: 'Pool'
    pool: {
      services: [
        {
          id: openai_api_chat_primary_Backend.id
          priority: 1
          weight: 3
        }
        {
          id: openai_api_chat_secondary_Backend.id
          priority: 2
          weight: 1
        }
      ]
    }
  }
  parent: apim
  dependsOn: [
    openai_api_chat_primary_Backend
    openai_api_chat_secondary_Backend
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

resource openai_apiProxy 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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

resource openai_apiPolicy 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = {
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: '<policies>\n  <inbound>\n    <base />\n    <authentication-managed-identity resource="https://cognitiveservices.azure.com" />\n    <set-backend-service backend-id="openai_apiPool" />\n  </inbound>\n  <backend><base /></backend>\n  <outbound><base /></outbound>\n  <on-error><base /></on-error>\n</policies>'
  }
  parent: openai_api
  dependsOn: [
    openai_apiPool
  ]
}

output gatewayUrl string = apim.properties.gatewayUrl

output id string = apim.id

output principalId string = apim.identity.principalId