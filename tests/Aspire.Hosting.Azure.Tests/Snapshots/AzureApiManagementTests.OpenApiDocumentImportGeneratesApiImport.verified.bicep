@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param apim_identity_outputs_id string

param _apim_backendUrl_catalog_backend string

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
      '${apim_identity_outputs_id}': { }
    }
  }
  tags: {
    'aspire-resource-name': 'apim'
  }
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
    format: 'openapi+json'
    subscriptionRequired: true
    path: 'catalog'
    protocols: [
      'https'
    ]
    value: '{"openapi":"3.0.1","info":{"title":"Catalog","version":"v1"},"paths":{}}'
  }
  parent: apim
}

resource _apim_apiPolicy_catalog_api 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = {
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: '<policies>\n  <inbound>\n    <base />\n    <set-backend-service backend-id="catalog-backend" />\n  </inbound>\n  <backend><base /></backend>\n  <outbound><base /></outbound>\n  <on-error><base /></on-error>\n</policies>'
  }
  parent: catalog_api
  dependsOn: [
    catalog_backend
  ]
}

output gatewayUrl string = apim.properties.gatewayUrl

output name string = apim.name

output id string = apim.id