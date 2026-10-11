@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param api_host string

param web_host string

resource frontdoor 'Microsoft.Cdn/profiles@2025-06-01' = {
  name: take('frontdoor-${uniqueString(resourceGroup().id)}', 260)
  tags: {
    'aspire-resource-name': 'frontdoor'
  }
  location: 'Global'
  sku: {
    name: 'Standard_AzureFrontDoor'
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
  name: take('apiOrigin-${uniqueString(resourceGroup().id, api_host)}', 90)
  properties: {
    hostName: api_host
    originHostHeader: api_host
  }
  parent: apiOriginGroup
}

resource apiRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2025-06-01' = {
  name: take('apiRoute-${uniqueString(resourceGroup().id)}', 90)
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

resource webEndpoint 'Microsoft.Cdn/profiles/afdEndpoints@2025-06-01' = {
  name: take('webEndpoint-${uniqueString(resourceGroup().id)}', 46)
  location: 'Global'
  parent: frontdoor
}

resource webOriginGroup 'Microsoft.Cdn/profiles/originGroups@2025-06-01' = {
  name: take('webOriginGroup-${uniqueString(resourceGroup().id)}', 90)
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

resource webOrigin 'Microsoft.Cdn/profiles/originGroups/origins@2025-06-01' = {
  name: take('webOrigin-${uniqueString(resourceGroup().id, web_host)}', 90)
  properties: {
    hostName: web_host
    originHostHeader: web_host
  }
  parent: webOriginGroup
}

resource webRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2025-06-01' = {
  name: take('webRoute-${uniqueString(resourceGroup().id)}', 90)
  properties: {
    originGroup: {
      id: webOriginGroup.id
    }
    patternsToMatch: [
      '/*'
    ]
    forwardingProtocol: 'HttpsOnly'
    linkToDefaultDomain: 'Enabled'
    httpsRedirect: 'Enabled'
  }
  parent: webEndpoint
  dependsOn: [
    webOrigin
  ]
}

output api_endpointUrl string = 'https://${apiEndpoint.properties.hostName}'

output web_endpointUrl string = 'https://${webEndpoint.properties.hostName}'