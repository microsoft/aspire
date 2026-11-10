@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param apim_identity_outputs_id string

param vault_outputs_name string

param apim_identity_outputs_clientid string

@secure()
param _apim_namedValueParameter_api_key_value string

param _apim_computeBackendUrl_catalog_api string

param insights_outputs_name string

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
        defaultSslBinding: true
        negotiateClientCertificate: false
      }
    ]
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

resource backend_region 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  name: 'backend-region'
  properties: {
    displayName: 'backend-region'
    secret: false
    tags: [
      'routing'
    ]
    value: 'westus3'
  }
  parent: apim
}

resource api_key_value 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  name: 'api-key-value'
  properties: {
    displayName: 'ApiKey'
    secret: true
    value: _apim_namedValueParameter_api_key_value
  }
  parent: apim
}

resource vault_upstream_secret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = {
  name: 'upstream-secret'
  parent: vault
}

resource upstream_secret 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  name: 'upstream-secret'
  properties: {
    displayName: 'UpstreamSecret'
    secret: true
    keyVault: {
      secretIdentifier: '${vault.properties.vaultUri}secrets/upstream-secret'
      identityClientId: apim_identity_outputs_clientid
    }
  }
  parent: apim
}

resource correlation 'Microsoft.ApiManagement/service/policyFragments@2024-05-01' = {
  name: 'correlation'
  properties: {
    description: 'Adds a correlation ID.'
    format: 'rawxml'
    value: '<fragment>\n  <set-header name="x-correlation-id" exists-action="skip"><value>@(context.RequestId.ToString())</value></set-header>\n</fragment>'
  }
  parent: apim
  dependsOn: [
    backend_region
    api_key_value
    upstream_secret
  ]
}

resource _apim_servicePolicy_apim 'Microsoft.ApiManagement/service/policies@2024-05-01' = {
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: '<policies>\n  <inbound>\n    <include-fragment fragment-id="correlation" />\n  </inbound>\n  <backend><forward-request /></backend>\n  <outbound />\n  <on-error />\n</policies>'
  }
  parent: apim
  dependsOn: [
    correlation
    backend_region
    api_key_value
    upstream_secret
  ]
}

resource _apim_computeBackend_catalog_api 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  name: 'catalog_apiBackend'
  properties: {
    protocol: 'http'
    title: 'catalog-api'
    tls: {
      validateCertificateChain: true
      validateCertificateName: true
    }
    type: 'Single'
    url: _apim_computeBackendUrl_catalog_api
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

resource get_product 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  name: 'get-product'
  properties: {
    displayName: 'get-product'
    method: 'GET'
    templateParameters: [
      {
        name: 'id'
        type: 'string'
        required: true
      }
    ]
    urlTemplate: '/products/{id}'
  }
  parent: catalog_api
}

resource _apim_operationPolicy_get_product 'Microsoft.ApiManagement/service/apis/operations/policies@2024-05-01' = {
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: '<policies>\n  <inbound>\n    <base />\n    <include-fragment fragment-id="correlation" />\n  </inbound>\n  <backend><base /></backend>\n  <outbound><base /></outbound>\n  <on-error><base /></on-error>\n</policies>'
  }
  parent: get_product
  dependsOn: [
    correlation
    backend_region
    api_key_value
    upstream_secret
  ]
}

resource _apim_apiPolicy_catalog_api 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = {
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: '<policies>\n  <inbound>\n    <base />\n    <set-backend-service backend-id="catalog_apiBackend" />\n    <include-fragment fragment-id="correlation" />\n  </inbound>\n  <backend><base /></backend>\n  <outbound><base /></outbound>\n  <on-error><base /></on-error>\n</policies>'
  }
  parent: catalog_api
  dependsOn: [
    _apim_computeBackend_catalog_api
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
    _apim_operationPolicy_get_product
    correlation
    backend_region
    api_key_value
    upstream_secret
  ]
}

resource catalog_product 'Microsoft.ApiManagement/service/products@2024-05-01' = {
  name: 'catalog-product'
  properties: {
    description: 'Catalog APIs'
    displayName: 'Catalog'
    approvalRequired: false
    subscriptionRequired: true
    state: 'published'
    terms: 'Use responsibly.'
  }
  parent: apim
}

resource _apim_productApi_catalog_product_catalog_api_51517753 'Microsoft.ApiManagement/service/products/apiLinks@2024-05-01' = {
  name: 'catalog-api'
  properties: {
    apiId: catalog_api.id
  }
  parent: catalog_product
  dependsOn: [
    catalog_api
  ]
}

resource catalog_client 'Microsoft.ApiManagement/service/subscriptions@2024-05-01' = {
  name: 'catalog-client'
  properties: {
    allowTracing: false
    displayName: 'Catalog client'
    scope: catalog_product.id
    state: 'active'
  }
  parent: apim
}

resource insights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: insights_outputs_name
}

resource _apim_logger_insights 'Microsoft.ApiManagement/service/loggers@2024-05-01' = {
  name: 'insights-application-insights'
  properties: {
    credentials: {
      instrumentationKey: insights.properties.InstrumentationKey
    }
    isBuffered: true
    loggerType: 'applicationInsights'
    resourceId: insights.id
  }
  parent: apim
}

resource _apim_serviceDiagnostic_apim 'Microsoft.ApiManagement/service/diagnostics@2024-05-01' = {
  name: 'applicationinsights'
  properties: {
    alwaysLog: 'allErrors'
    httpCorrelationProtocol: 'W3C'
    logClientIp: false
    loggerId: _apim_logger_insights.id
    metrics: true
    operationNameFormat: 'Name'
    sampling: {
      samplingType: 'fixed'
      percentage: 25
    }
    verbosity: 'error'
  }
  parent: apim
  dependsOn: [
    _apim_logger_insights
  ]
}

resource _apim_apiDiagnostic_catalog_api 'Microsoft.ApiManagement/service/apis/diagnostics@2024-05-01' = {
  name: 'applicationinsights'
  properties: {
    alwaysLog: 'allErrors'
    httpCorrelationProtocol: 'W3C'
    logClientIp: true
    loggerId: _apim_logger_insights.id
    metrics: true
    operationNameFormat: 'Name'
    sampling: {
      samplingType: 'fixed'
      percentage: 50
    }
    verbosity: 'information'
  }
  parent: catalog_api
  dependsOn: [
    _apim_logger_insights
  ]
}

output gatewayUrl string = apim.properties.gatewayUrl

output name string = apim.name

output id string = apim.id