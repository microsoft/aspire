@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param _apim_backendUrl_catalog_backend string

resource apim 'Microsoft.ApiManagement/service@2025-03-01-preview' = {
  name: take('apim${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    publisherEmail: 'api-owners@example.com'
    publisherName: 'Aspire'
    virtualNetworkType: 'None'
  }
  sku: {
    name: 'Developer'
    capacity: 1
  }
  identity: {
    type: 'SystemAssigned'
  }
  tags: {
    'aspire-resource-name': 'apim'
  }
}

resource catalog_backend 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  name: 'catalog-backend'
  properties: {
    protocol: 'http'
    url: _apim_backendUrl_catalog_backend
    title: 'catalog-backend'
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
    displayName: 'catalog-api'
    path: 'catalog'
    subscriptionRequired: true
    type: 'http'
    protocols: [
      'https'
    ]
    format: 'openapi+json'
    value: '{"openapi":"3.0.1","info":{"title":"Catalog","version":"v1"},"paths":{}}'
  }
  parent: apim
}

resource get_product 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'get-product'
  properties: {
    displayName: 'get-product'
    method: 'GET'
    urlTemplate: '/products/{id}'
    templateParameters: [
      {
        name: 'id'
        type: 'string'
        required: true
      }
    ]
  }
  parent: catalog_api
}

resource list_products 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'list-products'
  properties: {
    displayName: 'list-products'
    method: 'GET'
    urlTemplate: '/products'
  }
  parent: catalog_api
}

resource _apim_operationPolicy_list_products 'Microsoft.ApiManagement/service/apis/operations/policies@2024-05-01' = {
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: '<policies>\n  <inbound>\n    <base />\n    <set-header name="x-source" exists-action="override"><value>apim</value></set-header>\n  </inbound>\n  <backend><base /></backend>\n  <outbound><base /></outbound>\n  <on-error><base /></on-error>\n</policies>'
  }
  parent: list_products
}

resource _apim_apiPolicy_catalog_api 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = {
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: '<policies><inbound><base /><set-backend-service backend-id="catalog-backend" /></inbound></policies>'
  }
  parent: catalog_api
  dependsOn: [
    catalog_backend
    get_product
    _apim_operationPolicy_list_products
  ]
}

resource other_api 'Microsoft.ApiManagement/service/apis@2024-05-01' = {
  name: 'other-api'
  properties: {
    displayName: 'other-api'
    path: 'other'
    subscriptionRequired: true
    type: 'http'
    protocols: [
      'https'
    ]
  }
  parent: apim
}

resource _apim_proxyDELETEOperation_other_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: other_api
}

resource _apim_proxyGETOperation_other_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: other_api
}

resource _apim_proxyHEADOperation_other_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: other_api
}

resource _apim_proxyOPTIONSOperation_other_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: other_api
}

resource _apim_proxyPATCHOperation_other_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: other_api
}

resource _apim_proxyPOSTOperation_other_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: other_api
}

resource _apim_proxyPUTOperation_other_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: other_api
}

resource _apim_proxyTRACEOperation_other_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
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
  parent: other_api
}

resource other_operation 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'other-operation'
  properties: {
    displayName: 'other-operation'
    method: 'GET'
    urlTemplate: '/other'
  }
  parent: other_api
}

resource _apim_apiPolicy_other_api 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = {
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: '<policies>\n  <inbound>\n    <base />\n    <set-backend-service backend-id="catalog-backend" />\n  </inbound>\n  <backend><base /></backend>\n  <outbound><base /></outbound>\n  <on-error><base /></on-error>\n</policies>'
  }
  parent: other_api
  dependsOn: [
    catalog_backend
    _apim_proxyDELETEOperation_other_api
    _apim_proxyGETOperation_other_api
    _apim_proxyHEADOperation_other_api
    _apim_proxyOPTIONSOperation_other_api
    _apim_proxyPATCHOperation_other_api
    _apim_proxyPOSTOperation_other_api
    _apim_proxyPUTOperation_other_api
    _apim_proxyTRACEOperation_other_api
    other_operation
  ]
}

output gatewayUrl string = apim.properties.gatewayUrl

output name string = apim.name

output id string = apim.id

output principalId string = apim.identity.principalId