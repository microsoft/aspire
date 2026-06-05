targetScope = 'subscription'

param resourceGroupName string

param location string

param principalId string

param storage_Sku string = 'Standard_LRS'

param skuDescription string = 'The sku is '

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

module acaEnv_acr 'acaEnv-acr/acaEnv-acr.bicep' = {
  name: 'acaEnv-acr'
  scope: rg
  params: {
    location: location
  }
}

module acaEnv 'acaEnv/acaEnv.bicep' = {
  name: 'acaEnv'
  scope: rg
  params: {
    location: location
    acaenv_acr_outputs_name: acaEnv_acr.outputs.name
    userPrincipalId: principalId
  }
}

module kv 'kv/kv.bicep' = {
  name: 'kv'
  scope: rg
  params: {
    location: location
  }
}

module account 'account/account.bicep' = {
  name: 'account'
  scope: rg
  params: {
    location: location
  }
}

module storage 'storage/storage.bicep' = {
  name: 'storage'
  scope: rg
  params: {
    location: location
    storage_Sku: storage_Sku
    sku_description: '${skuDescription} ${storage_Sku}'
  }
}

module fe_identity 'fe-identity/fe-identity.bicep' = {
  name: 'fe-identity'
  scope: rg
  params: {
    location: location
  }
}

module fe_roles_storage 'fe-roles-storage/fe-roles-storage.bicep' = {
  name: 'fe-roles-storage'
  scope: rg
  params: {
    location: location
    storage_outputs_name: storage.outputs.name
    principalId: fe_identity.outputs.principalId
  }
}

module fe_roles_account 'fe-roles-account/fe-roles-account.bicep' = {
  name: 'fe-roles-account'
  scope: rg
  params: {
    location: location
    account_outputs_name: account.outputs.name
    principalId: fe_identity.outputs.principalId
  }
}

output acaEnv_AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = acaEnv.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN

output acaEnv_AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = acaEnv.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_ID

output acaEnv_AZURE_CONTAINER_REGISTRY_ENDPOINT string = acaEnv.outputs.AZURE_CONTAINER_REGISTRY_ENDPOINT

output acaEnv_AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = acaEnv.outputs.AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID

output fe_identity_id string = fe_identity.outputs.id

output storage_blobEndpoint string = storage.outputs.blobEndpoint

output account_connectionString string = account.outputs.connectionString

output fe_identity_clientId string = fe_identity.outputs.clientId

output kv_vaultUri string = kv.outputs.vaultUri