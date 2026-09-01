@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param vault_outputs_name string

@secure()
param _apim_namedValueParameter_api_key_value string

param _apim_computeBackendUrl_catalog_api string

param insights_outputs_name string

resource _apim_keyVaultIdentity_apim 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('apim-kv-apim-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: vault_outputs_name
}

resource vault_gateway_certificate 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = {
  name: 'gateway-certificate'
  parent: vault
}

resource vault_KeyVaultCertificateUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, _apim_keyVaultIdentity_apim.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'db79e9a7-68ee-4b58-9aeb-b90e7c24fcba'))
  properties: {
    principalId: _apim_keyVaultIdentity_apim.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'db79e9a7-68ee-4b58-9aeb-b90e7c24fcba')
    principalType: 'ServicePrincipal'
  }
  scope: vault
}

resource apim 'Microsoft.ApiManagement/service@2025-03-01-preview' = {
  name: take('apim${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    publisherEmail: 'api-owners@example.com'
    publisherName: 'Contoso APIs'
    virtualNetworkType: 'None'
    hostnameConfigurations: [
      {
        type: 'Proxy'
        hostName: 'api.contoso.example'
        keyVaultId: '${vault.properties.vaultUri}secrets/gateway-certificate'
        identityClientId: _apim_keyVaultIdentity_apim.properties.clientId
        defaultSslBinding: true
        negotiateClientCertificate: false
      }
    ]
  }
  sku: {
    name: 'StandardV2'
    capacity: 1
  }
  identity: {
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: {
      '${_apim_keyVaultIdentity_apim.id}': { }
    }
  }
  tags: {
    'aspire-resource-name': 'apim'
  }
  dependsOn: [
    vault_KeyVaultCertificateUser
  ]
}

resource backend_region 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  name: 'backend-region'
  properties: {
    displayName: 'backend-region'
    value: 'westus3'
    secret: false
    tags: [
      'routing'
    ]
  }
  parent: apim
}

resource api_key_value 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  name: 'api-key-value'
  properties: {
    displayName: 'ApiKey'
    value: _apim_namedValueParameter_api_key_value
    secret: true
  }
  parent: apim
}

resource vault_upstream_secret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = {
  name: 'upstream-secret'
  parent: vault
}

resource vault_KeyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, _apim_keyVaultIdentity_apim.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6'))
  properties: {
    principalId: _apim_keyVaultIdentity_apim.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalType: 'ServicePrincipal'
  }
  scope: vault
}

resource upstream_secret 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  name: 'upstream-secret'
  properties: {
    displayName: 'UpstreamSecret'
    secret: true
    keyVault: {
      secretIdentifier: '${vault.properties.vaultUri}secrets/upstream-secret'
      identityClientId: _apim_keyVaultIdentity_apim.properties.clientId
    }
  }
  parent: apim
  dependsOn: [
    vault_KeyVaultSecretsUser
  ]
}

resource correlation 'Microsoft.ApiManagement/service/policyFragments@2024-05-01' = {
  name: 'correlation'
  properties: {
    format: 'rawxml'
    value: '<fragment>\n  <set-header name="x-correlation-id" exists-action="skip"><value>@(context.RequestId.ToString())</value></set-header>\n</fragment>'
    description: 'Adds a correlation ID.'
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
    url: _apim_computeBackendUrl_catalog_api
    title: 'catalog-api'
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
    correlation
    backend_region
    api_key_value
    upstream_secret
  ]
}

resource catalog_product 'Microsoft.ApiManagement/service/products@2024-05-01' = {
  name: 'catalog-product'
  properties: {
    displayName: 'Catalog'
    description: 'Catalog APIs'
    terms: 'Use responsibly.'
    subscriptionRequired: true
    approvalRequired: false
    state: 'published'
  }
  parent: apim
}

resource _apim_productApi_catalog_product_catalog_api 'Microsoft.ApiManagement/service/products/apis@2024-05-01' = {
  name: 'catalog-api'
  parent: catalog_product
  dependsOn: [
    catalog_api
  ]
}

resource catalog_client 'Microsoft.ApiManagement/service/subscriptions@2024-05-01' = {
  name: 'catalog-client'
  properties: {
    displayName: 'Catalog client'
    scope: catalog_product.id
    state: 'active'
    allowTracing: false
  }
  parent: apim
}

resource insights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: insights_outputs_name
}

resource _apim_logger_insights 'Microsoft.ApiManagement/service/loggers@2024-05-01' = {
  name: 'insights-application-insights'
  properties: {
    loggerType: 'applicationInsights'
    resourceId: insights.id
    credentials: {
      instrumentationKey: insights.properties.InstrumentationKey
    }
    isBuffered: true
  }
  parent: apim
}

resource _apim_serviceDiagnostic_apim 'Microsoft.ApiManagement/service/diagnostics@2024-05-01' = {
  name: 'applicationinsights'
  properties: {
    loggerId: _apim_logger_insights.id
    alwaysLog: 'allErrors'
    sampling: {
      samplingType: 'fixed'
      percentage: 25
    }
    httpCorrelationProtocol: 'W3C'
    logClientIp: false
    verbosity: 'error'
    operationNameFormat: 'Name'
    metrics: true
  }
  parent: apim
  dependsOn: [
    _apim_logger_insights
  ]
}

resource _apim_apiDiagnostic_catalog_api 'Microsoft.ApiManagement/service/apis/diagnostics@2024-05-01' = {
  name: 'applicationinsights'
  properties: {
    loggerId: _apim_logger_insights.id
    alwaysLog: 'allErrors'
    sampling: {
      samplingType: 'fixed'
      percentage: 50
    }
    httpCorrelationProtocol: 'W3C'
    logClientIp: true
    verbosity: 'information'
    operationNameFormat: 'Name'
    metrics: true
  }
  parent: catalog_api
  dependsOn: [
    _apim_logger_insights
  ]
}

output gatewayUrl string = apim.properties.gatewayUrl

output name string = apim.name

output id string = apim.id

output principalId string = apim.identity.principalId