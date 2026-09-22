@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param api_host string

resource frontdoor 'Microsoft.Cdn/profiles@2025-06-01' = {
  name: 'custom-profile'
  tags: {
    'aspire-resource-name': 'frontdoor'
  }
  location: 'Global'
  sku: {
    name: 'Standard_AzureFrontDoor'
  }
}

resource apiEndpoint 'Microsoft.Cdn/profiles/afdEndpoints@2025-06-01' = {
  name: 'custom-endpoint'
  location: 'Global'
  parent: frontdoor
}

resource apiOriginGroup 'Microsoft.Cdn/profiles/originGroups@2025-06-01' = {
  name: 'custom-origin-group'
  properties: {
    loadBalancingSettings: {
      sampleSize: 4
      successfulSamplesRequired: 3
      additionalLatencyInMilliseconds: 50
    }
    healthProbeSettings: {
      probePath: '/'
      probeProtocol: 'Https'
    }
  }
  parent: frontdoor
}

resource apiOrigin 'Microsoft.Cdn/profiles/originGroups/origins@2025-06-01' = {
  name: 'custom-origin'
  properties: {
    hostName: api_host
    originHostHeader: api_host
  }
  parent: apiOriginGroup
}

resource apiRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2025-06-01' = {
  name: 'custom-route'
  properties: {
    originGroup: {
      id: apiOriginGroup.id
    }
    patternsToMatch: [
      '/*'
    ]
    forwardingProtocol: 'HttpsOnly'
    linkToDefaultDomain: 'Enabled'
    httpsRedirect: 'Enabled'
  }
  parent: apiEndpoint
  dependsOn: [
    apiOrigin
  ]
}

output api_endpointUrl string = 'https://${apiEndpoint.properties.hostName}'