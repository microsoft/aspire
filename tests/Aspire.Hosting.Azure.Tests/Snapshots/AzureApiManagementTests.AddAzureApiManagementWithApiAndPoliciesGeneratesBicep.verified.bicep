@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param _apim_computeBackendUrl_catalog_api string

resource apim 'Microsoft.ApiManagement/service@2024-05-01' = {
  name: take('apim${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    publisherEmail: 'api-owners@example.com'
    publisherName: 'Contoso APIs'
    publicNetworkAccess: 'Enabled'
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

resource _apim_proxyOperation_catalog_api 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'proxy'
  properties: {
    displayName: 'Proxy'
    method: '*'
    urlTemplate: '/*'
  }
  parent: catalog_api
}

resource get_product 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'get-product'
  properties: {
    displayName: 'Get product'
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

output id string = apim.id

output principalId string = apim.identity.principalId