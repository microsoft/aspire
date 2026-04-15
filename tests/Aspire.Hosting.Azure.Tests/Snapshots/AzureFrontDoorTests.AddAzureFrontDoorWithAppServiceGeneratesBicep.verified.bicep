@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param api_host string

resource frontdoor 'Microsoft.Cdn/profiles@2025-06-01' = {
  name: take('frontdoor-${uniqueString(resourceGroup().id)}', 90)
  location: 'Global'
  sku: {
    name: 'Standard_AzureFrontDoor'
  }
  tags: {
    'aspire-resource-name': 'frontdoor'
  }
}

resource apiEndpoint 'Microsoft.Cdn/profiles/afdEndpoints@2025-06-01' = {
  name: take('api-${uniqueString(resourceGroup().id)}', 46)
  location: 'Global'
  properties: {
    enabledState: 'Enabled'
  }
  parent: frontdoor
}

resource apiOriginGroup 'Microsoft.Cdn/profiles/originGroups@2025-06-01' = {
  name: take('api-og-${uniqueString(resourceGroup().id)}', 90)
  properties: {
    healthProbeSettings: {
      probePath: '/'
      probeRequestType: 'HEAD'
      probeProtocol: 'Https'
      probeIntervalInSeconds: 240
    }
    loadBalancingSettings: {
      sampleSize: 4
      successfulSamplesRequired: 3
      additionalLatencyInMilliseconds: 50
    }
    sessionAffinityState: 'Disabled'
  }
  parent: frontdoor
}

resource apiOrigin 'Microsoft.Cdn/profiles/originGroups/origins@2025-06-01' = {
  name: take('api-origin-${uniqueString(resourceGroup().id)}', 90)
  properties: {
    enabledState: 'Enabled'
    enforceCertificateNameCheck: true
    hostName: api_host
    httpPort: 80
    httpsPort: 443
    originHostHeader: api_host
    priority: 1
    weight: 1000
  }
  parent: apiOriginGroup
}

resource apiRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2025-06-01' = {
  name: take('api-route-${uniqueString(resourceGroup().id)}', 90)
  properties: {
    cacheConfiguration: {
      queryStringCachingBehavior: 'IgnoreQueryString'
      compressionSettings: {
        contentTypesToCompress: [
          'text/plain'
          'text/html'
          'text/css'
          'application/javascript'
          'application/json'
          'image/svg+xml'
        ]
        isCompressionEnabled: true
      }
    }
    enabledState: 'Enabled'
    forwardingProtocol: 'HttpsOnly'
    httpsRedirect: 'Enabled'
    linkToDefaultDomain: 'Enabled'
    originGroup: {
      id: apiOriginGroup.id
    }
    originPath: '/'
    patternsToMatch: [
      '/*'
    ]
    supportedProtocols: [
      'Http'
      'Https'
    ]
  }
  parent: apiEndpoint
  dependsOn: [
    apiOrigin
  ]
}

output api_endpointUrl string = 'https://${apiEndpoint.properties.hostName}'