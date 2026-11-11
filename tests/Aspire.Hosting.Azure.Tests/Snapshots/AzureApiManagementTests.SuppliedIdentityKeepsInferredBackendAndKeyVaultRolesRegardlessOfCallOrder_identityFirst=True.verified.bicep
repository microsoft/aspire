@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param shared_identity_outputs_id string

param vault_outputs_name string

param shared_identity_outputs_clientid string

param _apim_backendUrl_blob_backend string

resource apim 'Microsoft.ApiManagement/service@2025-03-01-preview' = {
  name: take('apim${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    publisherEmail: 'api-owners@example.com'
    publisherName: 'Contoso APIs'
    virtualNetworkType: 'None'
  }
  sku: {
    name: 'Developer'
    capacity: 1
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${shared_identity_outputs_id}': { }
    }
  }
  tags: {
    'aspire-resource-name': 'apim'
  }
}

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: vault_outputs_name
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
      identityClientId: shared_identity_outputs_clientid
    }
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

resource blob_api 'Microsoft.ApiManagement/service/apis@2024-05-01' = {
  name: 'blob-api'
  properties: {
    type: 'http'
    displayName: 'blob-api'
    subscriptionRequired: true
    path: 'blob'
    protocols: [
      'https'
    ]
  }
  parent: apim
}

resource _apim_proxyDELETERootOperation_blob_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-delete-root'
  properties: {
    displayName: 'Proxy DELETE root'
    method: 'DELETE'
    urlTemplate: '/'
  }
  parent: blob_api
}

resource _apim_proxyDELETEOperation_blob_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: blob_api
}

resource _apim_proxyGETRootOperation_blob_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-get-root'
  properties: {
    displayName: 'Proxy GET root'
    method: 'GET'
    urlTemplate: '/'
  }
  parent: blob_api
}

resource _apim_proxyGETOperation_blob_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: blob_api
}

resource _apim_proxyHEADRootOperation_blob_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-head-root'
  properties: {
    displayName: 'Proxy HEAD root'
    method: 'HEAD'
    urlTemplate: '/'
  }
  parent: blob_api
}

resource _apim_proxyHEADOperation_blob_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: blob_api
}

resource _apim_proxyOPTIONSRootOperation_blob_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-options-root'
  properties: {
    displayName: 'Proxy OPTIONS root'
    method: 'OPTIONS'
    urlTemplate: '/'
  }
  parent: blob_api
}

resource _apim_proxyOPTIONSOperation_blob_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: blob_api
}

resource _apim_proxyPATCHRootOperation_blob_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-patch-root'
  properties: {
    displayName: 'Proxy PATCH root'
    method: 'PATCH'
    urlTemplate: '/'
  }
  parent: blob_api
}

resource _apim_proxyPATCHOperation_blob_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: blob_api
}

resource _apim_proxyPOSTRootOperation_blob_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-post-root'
  properties: {
    displayName: 'Proxy POST root'
    method: 'POST'
    urlTemplate: '/'
  }
  parent: blob_api
}

resource _apim_proxyPOSTOperation_blob_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: blob_api
}

resource _apim_proxyPUTRootOperation_blob_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-put-root'
  properties: {
    displayName: 'Proxy PUT root'
    method: 'PUT'
    urlTemplate: '/'
  }
  parent: blob_api
}

resource _apim_proxyPUTOperation_blob_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: blob_api
}

resource _apim_proxyTRACERootOperation_blob_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-trace-root'
  properties: {
    displayName: 'Proxy TRACE root'
    method: 'TRACE'
    urlTemplate: '/'
  }
  parent: blob_api
}

resource _apim_proxyTRACEOperation_blob_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: blob_api
}

resource _apim_apiPolicy_blob_api 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = {
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: '<policies>\n  <inbound>\n    <base />\n    <authentication-managed-identity resource="https://storage.azure.com/" client-id="${shared_identity_outputs_clientid}" />\n    <set-backend-service backend-id="blob-backend" />\n  </inbound>\n  <backend><base /></backend>\n  <outbound><base /></outbound>\n  <on-error><base /></on-error>\n</policies>'
  }
  parent: blob_api
  dependsOn: [
    blob_backend
    _apim_proxyDELETERootOperation_blob_api
    _apim_proxyDELETEOperation_blob_api
    _apim_proxyGETRootOperation_blob_api
    _apim_proxyGETOperation_blob_api
    _apim_proxyHEADRootOperation_blob_api
    _apim_proxyHEADOperation_blob_api
    _apim_proxyOPTIONSRootOperation_blob_api
    _apim_proxyOPTIONSOperation_blob_api
    _apim_proxyPATCHRootOperation_blob_api
    _apim_proxyPATCHOperation_blob_api
    _apim_proxyPOSTRootOperation_blob_api
    _apim_proxyPOSTOperation_blob_api
    _apim_proxyPUTRootOperation_blob_api
    _apim_proxyPUTOperation_blob_api
    _apim_proxyTRACERootOperation_blob_api
    _apim_proxyTRACEOperation_blob_api
    upstream_secret
  ]
}

output gatewayUrl string = apim.properties.gatewayUrl

output name string = apim.name

output id string = apim.id