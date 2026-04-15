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
  parent: frontdoor
}

resource apiOriginGroup 'Microsoft.Cdn/profiles/originGroups@2025-06-01' = {
  name: take('api-og-${uniqueString(resourceGroup().id)}', 90)
  parent: frontdoor
}

resource apiOrigin 'Microsoft.Cdn/profiles/originGroups/origins@2025-06-01' = {
  name: take('api-origin-${uniqueString(resourceGroup().id)}', 90)
  properties: {
    hostName: api_host
    originHostHeader: api_host
  }
  parent: apiOriginGroup
}

resource apiRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2025-06-01' = {
  name: take('api-route-${uniqueString(resourceGroup().id)}', 90)
  properties: {
    forwardingProtocol: 'HttpsOnly'
    httpsRedirect: 'Enabled'
    linkToDefaultDomain: 'Enabled'
    originGroup: {
      id: apiOriginGroup.id
    }
  }
  parent: apiEndpoint
  dependsOn: [
    apiOrigin
  ]
}

output api_endpointUrl string = 'https://${apiEndpoint.properties.hostName}'