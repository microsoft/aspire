@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param api_east_host string

param api_west_host string

resource frontdoor 'Microsoft.Cdn/profiles@2025-06-01' = {
  name: take('frontdoor-${uniqueString(resourceGroup().id)}', 260)
  location: 'Global'
  sku: {
    name: 'Standard_AzureFrontDoor'
  }
  tags: {
    'aspire-resource-name': 'frontdoor'
  }
}

resource apiEndpoint 'Microsoft.Cdn/profiles/afdEndpoints@2025-06-01' = {
  name: take('apiEndpoint-${uniqueString(resourceGroup().id)}', 46)
  location: 'Global'
  parent: frontdoor
}

resource apiOriginGroup 'Microsoft.Cdn/profiles/originGroups@2025-06-01' = {
  name: take('apiOriginGroup-${uniqueString(resourceGroup().id)}', 90)
  properties: {
    healthProbeSettings: {
      probePath: '/'
      probeProtocol: 'Https'
    }
    loadBalancingSettings: {
      sampleSize: 4
      successfulSamplesRequired: 3
      additionalLatencyInMilliseconds: 50
    }
  }
  parent: frontdoor
}

resource api_eastOrigin 'Microsoft.Cdn/profiles/originGroups/origins@2025-06-01' = {
  name: take('apieastOrigin-${uniqueString(resourceGroup().id)}', 90)
  properties: {
    enabledState: 'Enabled'
    hostName: api_east_host
    originHostHeader: api_east_host
    priority: 1
    weight: 1000
  }
  parent: apiOriginGroup
}

resource api_westOrigin 'Microsoft.Cdn/profiles/originGroups/origins@2025-06-01' = {
  name: take('apiwestOrigin-${uniqueString(resourceGroup().id)}', 90)
  properties: {
    enabledState: 'Enabled'
    hostName: api_west_host
    originHostHeader: api_west_host
    priority: 1
    weight: 1000
  }
  parent: apiOriginGroup
}

resource apiRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2025-06-01' = {
  name: take('apiRoute-${uniqueString(resourceGroup().id)}', 90)
  properties: {
    forwardingProtocol: 'HttpsOnly'
    httpsRedirect: 'Enabled'
    linkToDefaultDomain: 'Enabled'
    originGroup: {
      id: apiOriginGroup.id
    }
    patternsToMatch: [
      '/*'
    ]
  }
  parent: apiEndpoint
  dependsOn: [
    api_eastOrigin
    api_westOrigin
  ]
}

output api_endpointUrl string = 'https://${apiEndpoint.properties.hostName}'