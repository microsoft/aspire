@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param api_host string

param env_environment_id string

param env_location string

param web_host string

resource frontdoor 'Microsoft.Cdn/profiles@2025-06-01' = {
  name: take('frontdoor-${uniqueString(resourceGroup().id)}', 260)
  tags: {
    'aspire-resource-name': 'frontdoor'
  }
  location: 'Global'
  sku: {
    name: 'Premium_AzureFrontDoor'
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
    sharedPrivateLinkResource: {
      privateLink: {
        id: env_environment_id
      }
      privateLinkLocation: (privateLinkRegions[?env_location] ?? env_location)
      groupId: 'managedEnvironments'
      requestMessage: 'Azure Front Door frontdoor requests access to env.'
    }
    enforceCertificateNameCheck: true
  }
  parent: apiOriginGroup
}

var privateLinkRegions = {
  australiacentral: 'australiaeast'
  australiacentral2: 'australiaeast'
  australiasoutheast: 'australiaeast'
  brazilsoutheast: 'brazilsouth'
  canadaeast: 'canadacentral'
  centraluseuap: 'centralus'
  chinaeast: 'chinaeast3'
  chinaeast2: 'chinaeast3'
  chinanorth: 'chinanorth3'
  chinanorth2: 'chinanorth3'
  eastus2euap: 'eastus2'
  eastusstg: 'eastus'
  francesouth: 'francecentral'
  germanynorth: 'germanywestcentral'
  japanwest: 'japaneast'
  jioindiacentral: 'centralindia'
  jioindiawest: 'centralindia'
  koreasouth: 'koreacentral'
  northcentralus: 'centralus'
  norwaywest: 'norwayeast'
  southafricawest: 'southafricanorth'
  southcentralusstg: 'southcentralus'
  southindia: 'centralindia'
  swedensouth: 'swedencentral'
  uaecentral: 'uaenorth'
  ukwest: 'uksouth'
  westcentralus: 'centralus'
  westindia: 'centralindia'
  westus: 'westus2'
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
    sharedPrivateLinkResource: {
      privateLink: {
        id: env_environment_id
      }
      privateLinkLocation: (privateLinkRegions[?env_location] ?? env_location)
      groupId: 'managedEnvironments'
      requestMessage: 'Azure Front Door frontdoor requests access to env.'
    }
    enforceCertificateNameCheck: true
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

output api_endpointId string = apiEndpoint.id

output web_endpointUrl string = 'https://${webEndpoint.properties.hostName}'

output web_endpointId string = webEndpoint.id