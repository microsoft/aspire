@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param my_api_host string

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

resource my_apiEndpoint 'Microsoft.Cdn/profiles/afdEndpoints@2025-06-01' = {
  name: take('myapiEndpoint-${uniqueString(resourceGroup().id)}', 46)
  location: 'Global'
  parent: frontdoor
}

resource my_apiOriginGroup 'Microsoft.Cdn/profiles/originGroups@2025-06-01' = {
  name: take('myapiOriginGroup-${uniqueString(resourceGroup().id)}', 90)
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

resource my_apiOrigin 'Microsoft.Cdn/profiles/originGroups/origins@2025-06-01' = {
  name: take('my-apiOrigin-${uniqueString(resourceGroup().id, my_api_host)}', 90)
  properties: {
    hostName: my_api_host
    originHostHeader: my_api_host
  }
  parent: my_apiOriginGroup
}

resource my_apiRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2025-06-01' = {
  name: take('myapiRoute-${uniqueString(resourceGroup().id)}', 90)
  properties: {
    originGroup: {
      id: my_apiOriginGroup.id
    }
    patternsToMatch: [
      '/*'
    ]
    forwardingProtocol: 'HttpsOnly'
    linkToDefaultDomain: 'Enabled'
    httpsRedirect: 'Enabled'
  }
  parent: my_apiEndpoint
  dependsOn: [
    my_apiOrigin
  ]
}

output my_api_endpointUrl string = 'https://${my_apiEndpoint.properties.hostName}'