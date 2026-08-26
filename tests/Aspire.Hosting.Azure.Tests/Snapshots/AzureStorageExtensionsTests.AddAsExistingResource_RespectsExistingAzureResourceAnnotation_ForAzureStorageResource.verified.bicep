@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existing_storage_name string

param existing_storage_rg string

resource test_storage 'Microsoft.Storage/storageAccounts@2025-06-01' existing = {
  name: existing_storage_name
  scope: resourceGroup(existing_storage_rg)
}