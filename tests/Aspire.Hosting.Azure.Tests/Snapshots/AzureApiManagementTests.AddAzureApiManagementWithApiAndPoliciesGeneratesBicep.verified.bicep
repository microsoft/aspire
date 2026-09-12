@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param _apim_computeBackendUrl_catalog_api string

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
    capacity: 2
  }
  identity: {
    type: 'SystemAssigned'
  }
  tags: {
    'aspire-resource-name': 'apim'
  }
}

resource _apim_servicePolicy_apim 'Microsoft.ApiManagement/service/policies@2024-05-01' = {
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: '<policies>\n  <inbound>\n    <set-header name="x-gateway" exists-action="override"><value>apim</value></set-header>\n  </inbound>\n  <backend><forward-request /></backend>\n  <outbound />\n  <on-error />\n</policies>'
  }
  parent: apim
}

resource _apim_computeBackend_catalog_api 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  name: 'catalog_apiBackend'
  properties: {
    protocol: 'http'
    url: _apim_computeBackendUrl_catalog_api
    title: 'Catalog API'
    type: 'Single'
    tls: {
      validateCertificateChain: true
      validateCertificateName: true
    }
  }
  parent: apim
}

resource catalog_api 'Microsoft.ApiManagement/service/apis@2024-05-01' = {
  name: 'catalog-api'
  properties: {
    displayName: 'Catalog API'
    path: 'catalog'
    subscriptionRequired: true
    type: 'http'
    protocols: [
      'https'
    ]
  }
  parent: apim
}

resource _apim_proxyDELETEOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-delete'
  properties: {
    displayName: 'Proxy DELETE'
    method: 'DELETE'
    urlTemplate: '/{*path}'
    templateParameters: [
      {
        name: 'path'
        type: 'string'
        required: true
      }
    ]
  }
  parent: catalog_api
}

resource _apim_proxyGETOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-get'
  properties: {
    displayName: 'Proxy GET'
    method: 'GET'
    urlTemplate: '/{*path}'
    templateParameters: [
      {
        name: 'path'
        type: 'string'
        required: true
      }
    ]
  }
  parent: catalog_api
}

resource _apim_proxyHEADOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-head'
  properties: {
    displayName: 'Proxy HEAD'
    method: 'HEAD'
    urlTemplate: '/{*path}'
    templateParameters: [
      {
        name: 'path'
        type: 'string'
        required: true
      }
    ]
  }
  parent: catalog_api
}

resource _apim_proxyOPTIONSOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-options'
  properties: {
    displayName: 'Proxy OPTIONS'
    method: 'OPTIONS'
    urlTemplate: '/{*path}'
    templateParameters: [
      {
        name: 'path'
        type: 'string'
        required: true
      }
    ]
  }
  parent: catalog_api
}

resource _apim_proxyPATCHOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-patch'
  properties: {
    displayName: 'Proxy PATCH'
    method: 'PATCH'
    urlTemplate: '/{*path}'
    templateParameters: [
      {
        name: 'path'
        type: 'string'
        required: true
      }
    ]
  }
  parent: catalog_api
}

resource _apim_proxyPOSTOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-post'
  properties: {
    displayName: 'Proxy POST'
    method: 'POST'
    urlTemplate: '/{*path}'
    templateParameters: [
      {
        name: 'path'
        type: 'string'
        required: true
      }
    ]
  }
  parent: catalog_api
}

resource _apim_proxyPUTOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-put'
  properties: {
    displayName: 'Proxy PUT'
    method: 'PUT'
    urlTemplate: '/{*path}'
    templateParameters: [
      {
        name: 'path'
        type: 'string'
        required: true
      }
    ]
  }
  parent: catalog_api
}

resource _apim_proxyTRACEOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy-trace'
  properties: {
    displayName: 'Proxy TRACE'
    method: 'TRACE'
    urlTemplate: '/{*path}'
    templateParameters: [
      {
        name: 'path'
        type: 'string'
        required: true
      }
    ]
  }
  parent: catalog_api
}

resource get_product 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'get-product'
  properties: {
    displayName: 'Get product'
    method: 'GET'
    urlTemplate: '/products/{id}/{*path}'
    templateParameters: [
      {
        name: 'id'
        type: 'string'
        required: true
      }
      {
        name: 'path'
        type: 'string'
        required: true
      }
    ]
  }
  parent: catalog_api
}

resource _apim_operationPolicy_get_product 'Microsoft.ApiManagement/service/apis/operations/policies@2024-05-01' = {
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: '<policies>\n  <inbound>\n    <base />\n    <set-query-parameter name="source" exists-action="override"><value>apim</value></set-query-parameter>\n  </inbound>\n  <backend><base /></backend>\n  <outbound><base /></outbound>\n  <on-error><base /></on-error>\n</policies>'
  }
  parent: get_product
}

resource _apim_apiPolicy_catalog_api 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = {
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: '<policies>\n  <inbound>\n    <base />\n    <set-backend-service backend-id="catalog_apiBackend" />\n    <rate-limit calls="100" renewal-period="60" />\n  </inbound>\n  <backend><base /></backend>\n  <outbound><base /></outbound>\n  <on-error><base /></on-error>\n</policies>'
  }
  parent: catalog_api
  dependsOn: [
    _apim_computeBackend_catalog_api
  ]
}

output gatewayUrl string = apim.properties.gatewayUrl

output name string = apim.name

output id string = apim.id

output principalId string = apim.identity.principalId