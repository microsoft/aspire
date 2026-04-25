@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param my_api_host string

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

resource my_apiEndpoint 'Microsoft.Cdn/profiles/afdEndpoints@2025-06-01' = {
  name: take('my-api-${uniqueString(resourceGroup().id)}', 46)
  location: 'Global'
  parent: frontdoor
}

resource my_apiOriginGroup 'Microsoft.Cdn/profiles/originGroups@2025-06-01' = {
  name: take('my-api-og-${uniqueString(resourceGroup().id)}', 90)
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

resource my_apiOrigin 'Microsoft.Cdn/profiles/originGroups/origins@2025-06-01' = {
  name: take('my-api-origin-${uniqueString(resourceGroup().id)}', 90)
  properties: {
    hostName: my_api_host
    originHostHeader: my_api_host
  }
  parent: my_apiOriginGroup
}

resource my_apiRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2025-06-01' = {
  name: take('my-api-route-${uniqueString(resourceGroup().id)}', 90)
  properties: {
    forwardingProtocol: 'HttpsOnly'
    httpsRedirect: 'Enabled'
    linkToDefaultDomain: 'Enabled'
    originGroup: {
      id: my_apiOriginGroup.id
    }
    patternsToMatch: [
      '/*'
    ]
  }
  parent: my_apiEndpoint
  dependsOn: [
    my_apiOrigin
  ]
}

output my_api_endpointUrl string = 'https://${my_apiEndpoint.properties.hostName}'