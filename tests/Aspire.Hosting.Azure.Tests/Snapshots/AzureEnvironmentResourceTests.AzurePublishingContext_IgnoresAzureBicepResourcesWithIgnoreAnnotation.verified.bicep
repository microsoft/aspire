targetScope = 'subscription'

param resourceGroupName string

param location string

param principalId string

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

module included_storage 'included-storage/included-storage.bicep' = {
  name: 'included-storage'
  scope: rg
  params: {
    location: location
  }
}

module included_storage_roles 'included-storage-roles/included-storage-roles.bicep' = {
  name: 'included-storage-roles'
  scope: rg
  params: {
    location: location
    included_storage_outputs_name: included_storage.outputs.name
    principalType: ''
    principalId: principalId
  }
}