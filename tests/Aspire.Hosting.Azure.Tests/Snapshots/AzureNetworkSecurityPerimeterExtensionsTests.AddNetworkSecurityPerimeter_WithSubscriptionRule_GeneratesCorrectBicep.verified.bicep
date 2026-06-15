@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource my_nsp 'Microsoft.Network/networkSecurityPerimeters@2025-05-01' = {
  name: take('mynsp${uniqueString(resourceGroup().id)}', 24)
  location: location
  tags: {
    'aspire-resource-name': 'my-nsp'
  }
}

resource my_nsp_profile 'Microsoft.Network/networkSecurityPerimeters/profiles@2025-05-01' = {
  name: 'defaultProfile'
  parent: my_nsp
}

resource my_nsp_profile_allow_subscription 'Microsoft.Network/networkSecurityPerimeters/profiles/accessRules@2025-05-01' = {
  name: 'allow-subscription'
  properties: {
    direction: 'Inbound'
    subscriptions: [
      {
        id: '/subscriptions/00000000-0000-0000-0000-000000000001'
      }
    ]
  }
  parent: my_nsp_profile
}

output id string = my_nsp.id

output name string = my_nsp.name