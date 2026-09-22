@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param My_Api_12_With_A_Long_Resource_Name_host string

resource Front_Door_12 'Microsoft.Cdn/profiles@2025-06-01' = {
  name: take('FrontDoor12-${uniqueString(resourceGroup().id)}', 260)
  tags: {
    'aspire-resource-name': 'Front-Door-12'
  }
  location: 'Global'
  sku: {
    name: 'Standard_AzureFrontDoor'
  }
}

resource My_Api_12_With_A_Long_Resource_NameEndpoint 'Microsoft.Cdn/profiles/afdEndpoints@2025-06-01' = {
  name: take('MyApi12WithALongResourceNameEndpoint-${uniqueString(resourceGroup().id)}', 46)
  location: 'Global'
  parent: Front_Door_12
}

resource My_Api_12_With_A_Long_Resource_NameOriginGroup 'Microsoft.Cdn/profiles/originGroups@2025-06-01' = {
  name: take('MyApi12WithALongResourceNameOriginGroup-${uniqueString(resourceGroup().id)}', 90)
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
  parent: Front_Door_12
}

resource My_Api_12_With_A_Long_Resource_NameOrigin 'Microsoft.Cdn/profiles/originGroups/origins@2025-06-01' = {
  name: take('My-Api-12-With-A-Long-Resource-NameOrigin-${uniqueString(resourceGroup().id, My_Api_12_With_A_Long_Resource_Name_host)}', 90)
  properties: {
    hostName: My_Api_12_With_A_Long_Resource_Name_host
    originHostHeader: My_Api_12_With_A_Long_Resource_Name_host
  }
  parent: My_Api_12_With_A_Long_Resource_NameOriginGroup
}

resource My_Api_12_With_A_Long_Resource_NameRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2025-06-01' = {
  name: take('MyApi12WithALongResourceNameRoute-${uniqueString(resourceGroup().id)}', 90)
  properties: {
    originGroup: {
      id: My_Api_12_With_A_Long_Resource_NameOriginGroup.id
    }
    patternsToMatch: [
      '/*'
    ]
    forwardingProtocol: 'HttpsOnly'
    linkToDefaultDomain: 'Enabled'
    httpsRedirect: 'Enabled'
  }
  parent: My_Api_12_With_A_Long_Resource_NameEndpoint
  dependsOn: [
    My_Api_12_With_A_Long_Resource_NameOrigin
  ]
}

output My_Api_12_With_A_Long_Resource_Name_endpointUrl string = 'https://${My_Api_12_With_A_Long_Resource_NameEndpoint.properties.hostName}'