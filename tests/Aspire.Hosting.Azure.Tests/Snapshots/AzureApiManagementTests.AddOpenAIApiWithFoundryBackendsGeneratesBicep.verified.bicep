@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param apim_identity_outputs_id string

param _apim_backendUrl_chat_primary_backend string

param _apim_backendUrl_chat_secondary_backend string

param apim_identity_outputs_clientid string

resource apim 'Microsoft.ApiManagement/service@2025-03-01-preview' = {
  name: take('apim${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    publisherEmail: 'api-owners@example.com'
    publisherName: 'Contoso APIs'
    virtualNetworkType: 'None'
  }
  sku: {
    name: 'StandardV2'
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

resource chat_primary_backend 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  name: 'chat-primary-backend'
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
    title: 'chat-primary-backend'
    tls: {
      validateCertificateChain: true
      validateCertificateName: true
    }
    type: 'Single'
    url: _apim_backendUrl_chat_primary_backend
  }
  parent: apim
}

resource chat_secondary_backend 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  name: 'chat-secondary-backend'
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
    title: 'chat-secondary-backend'
    tls: {
      validateCertificateChain: true
      validateCertificateName: true
    }
    type: 'Single'
    url: _apim_backendUrl_chat_secondary_backend
  }
  parent: apim
}

resource openai_pool 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  name: 'openai-pool'
  properties: {
    pool: {
      services: [
        {
          id: chat_primary_backend.id
          weight: 3
          priority: 1
        }
        {
          id: chat_secondary_backend.id
          weight: 1
          priority: 2
        }
      ]
    }
    title: 'openai-pool'
    type: 'Pool'
  }
  parent: apim
  dependsOn: [
    chat_primary_backend
    chat_secondary_backend
  ]
}

resource openai_api 'Microsoft.ApiManagement/service/apis@2024-05-01' = {
  name: 'openai-api'
  properties: {
    type: 'http'
    displayName: 'openai-api'
    subscriptionRequired: true
    path: 'openai'
    protocols: [
      'https'
    ]
  }
  parent: apim
}

resource _apim_proxyDELETERootOperation_openai_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-delete-root'
  properties: {
    displayName: 'Proxy DELETE root'
    method: 'DELETE'
    urlTemplate: '/'
  }
  parent: openai_api
}

resource _apim_proxyDELETEOperation_openai_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-delete'
  properties: {
    displayName: 'Proxy DELETE'
    method: 'DELETE'
    templateParameters: [
      {
        name: 'path'
        type: 'string'
        required: true
      }
    ]
    urlTemplate: '/{*path}'
  }
  parent: openai_api
}

resource _apim_proxyGETRootOperation_openai_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-get-root'
  properties: {
    displayName: 'Proxy GET root'
    method: 'GET'
    urlTemplate: '/'
  }
  parent: openai_api
}

resource _apim_proxyGETOperation_openai_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-get'
  properties: {
    displayName: 'Proxy GET'
    method: 'GET'
    templateParameters: [
      {
        name: 'path'
        type: 'string'
        required: true
      }
    ]
    urlTemplate: '/{*path}'
  }
  parent: openai_api
}

resource _apim_proxyHEADRootOperation_openai_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-head-root'
  properties: {
    displayName: 'Proxy HEAD root'
    method: 'HEAD'
    urlTemplate: '/'
  }
  parent: openai_api
}

resource _apim_proxyHEADOperation_openai_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-head'
  properties: {
    displayName: 'Proxy HEAD'
    method: 'HEAD'
    templateParameters: [
      {
        name: 'path'
        type: 'string'
        required: true
      }
    ]
    urlTemplate: '/{*path}'
  }
  parent: openai_api
}

resource _apim_proxyOPTIONSRootOperation_openai_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-options-root'
  properties: {
    displayName: 'Proxy OPTIONS root'
    method: 'OPTIONS'
    urlTemplate: '/'
  }
  parent: openai_api
}

resource _apim_proxyOPTIONSOperation_openai_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-options'
  properties: {
    displayName: 'Proxy OPTIONS'
    method: 'OPTIONS'
    templateParameters: [
      {
        name: 'path'
        type: 'string'
        required: true
      }
    ]
    urlTemplate: '/{*path}'
  }
  parent: openai_api
}

resource _apim_proxyPATCHRootOperation_openai_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-patch-root'
  properties: {
    displayName: 'Proxy PATCH root'
    method: 'PATCH'
    urlTemplate: '/'
  }
  parent: openai_api
}

resource _apim_proxyPATCHOperation_openai_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-patch'
  properties: {
    displayName: 'Proxy PATCH'
    method: 'PATCH'
    templateParameters: [
      {
        name: 'path'
        type: 'string'
        required: true
      }
    ]
    urlTemplate: '/{*path}'
  }
  parent: openai_api
}

resource _apim_proxyPOSTRootOperation_openai_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-post-root'
  properties: {
    displayName: 'Proxy POST root'
    method: 'POST'
    urlTemplate: '/'
  }
  parent: openai_api
}

resource _apim_proxyPOSTOperation_openai_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-post'
  properties: {
    displayName: 'Proxy POST'
    method: 'POST'
    templateParameters: [
      {
        name: 'path'
        type: 'string'
        required: true
      }
    ]
    urlTemplate: '/{*path}'
  }
  parent: openai_api
}

resource _apim_proxyPUTRootOperation_openai_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-put-root'
  properties: {
    displayName: 'Proxy PUT root'
    method: 'PUT'
    urlTemplate: '/'
  }
  parent: openai_api
}

resource _apim_proxyPUTOperation_openai_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-put'
  properties: {
    displayName: 'Proxy PUT'
    method: 'PUT'
    templateParameters: [
      {
        name: 'path'
        type: 'string'
        required: true
      }
    ]
    urlTemplate: '/{*path}'
  }
  parent: openai_api
}

resource _apim_proxyTRACERootOperation_openai_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-trace-root'
  properties: {
    displayName: 'Proxy TRACE root'
    method: 'TRACE'
    urlTemplate: '/'
  }
  parent: openai_api
}

resource _apim_proxyTRACEOperation_openai_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-trace'
  properties: {
    displayName: 'Proxy TRACE'
    method: 'TRACE'
    templateParameters: [
      {
        name: 'path'
        type: 'string'
        required: true
      }
    ]
    urlTemplate: '/{*path}'
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
    value: '<policies>\n  <inbound>\n    <base />\n    <authentication-managed-identity resource="https://cognitiveservices.azure.com" client-id="${apim_identity_outputs_clientid}" />\n    <set-backend-service backend-id="openai-pool" />\n  </inbound>\n  <backend><base /></backend>\n  <outbound><base /></outbound>\n  <on-error><base /></on-error>\n</policies>'
  }
  parent: openai_api
  dependsOn: [
    openai_pool
    _apim_proxyDELETERootOperation_openai_api
    _apim_proxyDELETEOperation_openai_api
    _apim_proxyGETRootOperation_openai_api
    _apim_proxyGETOperation_openai_api
    _apim_proxyHEADRootOperation_openai_api
    _apim_proxyHEADOperation_openai_api
    _apim_proxyOPTIONSRootOperation_openai_api
    _apim_proxyOPTIONSOperation_openai_api
    _apim_proxyPATCHRootOperation_openai_api
    _apim_proxyPATCHOperation_openai_api
    _apim_proxyPOSTRootOperation_openai_api
    _apim_proxyPOSTOperation_openai_api
    _apim_proxyPUTRootOperation_openai_api
    _apim_proxyPUTOperation_openai_api
    _apim_proxyTRACERootOperation_openai_api
    _apim_proxyTRACEOperation_openai_api
    openai_api_chat_completions
  ]
}

output gatewayUrl string = apim.properties.gatewayUrl

output name string = apim.name

output id string = apim.id