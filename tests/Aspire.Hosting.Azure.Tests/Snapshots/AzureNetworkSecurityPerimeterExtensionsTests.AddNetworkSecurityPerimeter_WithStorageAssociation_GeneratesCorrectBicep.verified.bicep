@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param storage_outputs_id string

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

resource my_nsp_storage_assoc 'Microsoft.Network/networkSecurityPerimeters/resourceAssociations@2025-05-01' = {
  name: 'storage-assoc'
  properties: {
    accessMode: 'Enforced'
    privateLinkResource: {
      id: storage_outputs_id
    }
    profile: {
      id: my_nsp_profile.id
    }
  }
  parent: my_nsp
}

output id string = my_nsp.id

output name string = my_nsp.name