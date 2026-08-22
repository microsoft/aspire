@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param api_aca_eastus_host string

param api_aca_westeu_host string

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

resource api_aca_eastusOrigin 'Microsoft.Cdn/profiles/originGroups/origins@2025-06-01' = {
  name: take('apiacaeastusOrigin-${uniqueString(resourceGroup().id)}', 90)
  properties: {
    hostName: api_aca_eastus_host
    originHostHeader: api_aca_eastus_host
    priority: 1
  }
  parent: apiOriginGroup
}

resource api_aca_westeuOrigin 'Microsoft.Cdn/profiles/originGroups/origins@2025-06-01' = {
  name: take('apiacawesteuOrigin-${uniqueString(resourceGroup().id)}', 90)
  properties: {
    hostName: api_aca_westeu_host
    originHostHeader: api_aca_westeu_host
    priority: 2
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
    api_aca_eastusOrigin
    api_aca_westeuOrigin
  ]
}

output api_endpointUrl string = 'https://${apiEndpoint.properties.hostName}'