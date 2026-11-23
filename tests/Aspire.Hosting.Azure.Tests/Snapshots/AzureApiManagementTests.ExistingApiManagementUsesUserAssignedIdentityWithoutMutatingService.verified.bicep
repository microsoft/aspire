@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param _apim_backendUrl_catalog_backend string

param apim_existing_identity_outputs_clientid string

resource apim 'Microsoft.ApiManagement/service@2025-03-01-preview' existing = {
  name: 'existing-apim'
}

resource catalog_backend 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  name: 'catalog-backend'
  properties: {
    protocol: 'http'
    title: 'catalog-backend'
    tls: {
      validateCertificateChain: true
      validateCertificateName: true
    }
    type: 'Single'
    url: _apim_backendUrl_catalog_backend
  }
  parent: apim
}

resource catalog_api 'Microsoft.ApiManagement/service/apis@2024-05-01' = {
  name: 'catalog-api'
  properties: {
    type: 'http'
    displayName: 'catalog-api'
    subscriptionRequired: true
    path: 'catalog'
    protocols: [
      'https'
    ]
  }
  parent: apim
}

resource _apim_proxyDELETERootOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-delete-root'
  properties: {
    displayName: 'Proxy DELETE root'
    method: 'DELETE'
    urlTemplate: '/'
  }
  parent: catalog_api
}

resource _apim_proxyDELETEOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: catalog_api
}

resource _apim_proxyGETRootOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-get-root'
  properties: {
    displayName: 'Proxy GET root'
    method: 'GET'
    urlTemplate: '/'
  }
  parent: catalog_api
}

resource _apim_proxyGETOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: catalog_api
}

resource _apim_proxyHEADRootOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-head-root'
  properties: {
    displayName: 'Proxy HEAD root'
    method: 'HEAD'
    urlTemplate: '/'
  }
  parent: catalog_api
}

resource _apim_proxyHEADOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: catalog_api
}

resource _apim_proxyOPTIONSRootOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-options-root'
  properties: {
    displayName: 'Proxy OPTIONS root'
    method: 'OPTIONS'
    urlTemplate: '/'
  }
  parent: catalog_api
}

resource _apim_proxyOPTIONSOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: catalog_api
}

resource _apim_proxyPATCHRootOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-patch-root'
  properties: {
    displayName: 'Proxy PATCH root'
    method: 'PATCH'
    urlTemplate: '/'
  }
  parent: catalog_api
}

resource _apim_proxyPATCHOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: catalog_api
}

resource _apim_proxyPOSTRootOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-post-root'
  properties: {
    displayName: 'Proxy POST root'
    method: 'POST'
    urlTemplate: '/'
  }
  parent: catalog_api
}

resource _apim_proxyPOSTOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: catalog_api
}

resource _apim_proxyPUTRootOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-put-root'
  properties: {
    displayName: 'Proxy PUT root'
    method: 'PUT'
    urlTemplate: '/'
  }
  parent: catalog_api
}

resource _apim_proxyPUTOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: catalog_api
}

resource _apim_proxyTRACERootOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-trace-root'
  properties: {
    displayName: 'Proxy TRACE root'
    method: 'TRACE'
    urlTemplate: '/'
  }
  parent: catalog_api
}

resource _apim_proxyTRACEOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: catalog_api
}

resource _apim_apiPolicy_catalog_api 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = {
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: '<policies>\n  <inbound>\n    <base />\n    <authentication-managed-identity resource="api://catalog" client-id="${apim_existing_identity_outputs_clientid}" />\n    <set-backend-service backend-id="catalog-backend" />\n  </inbound>\n  <backend><base /></backend>\n  <outbound><base /></outbound>\n  <on-error><base /></on-error>\n</policies>'
  }
  parent: catalog_api
  dependsOn: [
    catalog_backend
    _apim_proxyDELETERootOperation_catalog_api
    _apim_proxyDELETEOperation_catalog_api
    _apim_proxyGETRootOperation_catalog_api
    _apim_proxyGETOperation_catalog_api
    _apim_proxyHEADRootOperation_catalog_api
    _apim_proxyHEADOperation_catalog_api
    _apim_proxyOPTIONSRootOperation_catalog_api
    _apim_proxyOPTIONSOperation_catalog_api
    _apim_proxyPATCHRootOperation_catalog_api
    _apim_proxyPATCHOperation_catalog_api
    _apim_proxyPOSTRootOperation_catalog_api
    _apim_proxyPOSTOperation_catalog_api
    _apim_proxyPUTRootOperation_catalog_api
    _apim_proxyPUTOperation_catalog_api
    _apim_proxyTRACERootOperation_catalog_api
    _apim_proxyTRACEOperation_catalog_api
  ]
}

output gatewayUrl string = apim.properties.gatewayUrl

output name string = 'existing-apim'

output id string = apim.id