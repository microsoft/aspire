@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param My_Api_12_With_A_Long_Resource_Name_host string

resource Front_Door_12 'Microsoft.Cdn/profiles@2025-06-01' = {
  name: 'FrontDoor12-p3PlroZNfAX2W76C38jKnNFRgXvGSmGWKm5ddntksq2DwCVP9w1tl6KLy7eD2t423bsJb3zPO9s6UbvOcQGwvnLNLtPgi0s8vaJvjlNg6lOmNO1wMAAmXIHPzO8RntHYRtKUB9F1czAkIza7CtESzflXC46Fm62NN0Fudg4M5JjxixgaObo4ZZyLhXrHdaOfGWGS3P3vz1X06YPfX50lS7XYB4mqYwJ44d0v0yg8i2F8bIOtimUhjCxI'
  tags: {
    'aspire-resource-name': 'Front-Door-12'
  }
  location: 'Global'
  sku: {
    name: 'Standard_AzureFrontDoor'
  }
}

resource My_Api_12_With_A_Long_Resource_NameEndpoint 'Microsoft.Cdn/profiles/afdEndpoints@2025-06-01' = {
  name: 'MyApi12WithALongResourceNameEndpoint-8Sr9DMGcr'
  location: 'Global'
  parent: Front_Door_12
}

resource My_Api_12_With_A_Long_Resource_NameOriginGroup 'Microsoft.Cdn/profiles/originGroups@2025-06-01' = {
  name: 'MyApi12WithALongResourceNameOriginGroup-2527tozZ2dZCZW3p2iMMCVCQrwihgEFvDQjMWvY7urrzIgi5xU'
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
  name: 'MyApi12WithALongResourceNameRoute-LD4AZgDmQ6gsSxHD9jNqDGuPrkEchknnTw3cBc1cjJP11cI31eh9shqe'
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