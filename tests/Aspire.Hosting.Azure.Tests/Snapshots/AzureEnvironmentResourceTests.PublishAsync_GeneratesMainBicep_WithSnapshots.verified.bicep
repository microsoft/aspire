targetScope = 'subscription'

param resourceGroupName string

param location string

param principalId string

param kvRg string

param kvName string

param storageSku string = 'Standard_LRS'

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
  scope: resourceGroup(kvRg)
  params: {
    location: location
    kvName: kvName
  }
}

module existing_storage 'existing-storage/existing-storage.bicep' = {
  name: 'existing-storage'
  scope: resourceGroup('rg-shared')
  params: {
    location: location
  }
}

module pg 'pg/pg.bicep' = {
  name: 'pg'
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
    storageSku: storageSku
    sku_description: '${skuDescription} ${storageSku}'
  }
}

module mod 'mod/mod.bicep' = {
  name: 'mod'
  scope: rg
  params: {
    location: location
    pgdb: '${pg.outputs.connectionString};Database=pgdb'
  }
}

module myapp_identity 'myapp-identity/myapp-identity.bicep' = {
  name: 'myapp-identity'
  scope: rg
  params: {
    location: location
  }
}

module myapp_roles_account 'myapp-roles-account/myapp-roles-account.bicep' = {
  name: 'myapp-roles-account'
  scope: rg
  params: {
    location: location
    account_outputs_name: account.outputs.name
    principalId: myapp_identity.outputs.principalId
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

output acaEnv_acr_name string = acaEnv_acr.outputs.name

output acaEnv_acr_loginServer string = acaEnv_acr.outputs.loginServer

output acaEnv_AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = acaEnv.outputs.AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID

output acaEnv_AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = acaEnv.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN

output acaEnv_AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = acaEnv.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_ID

output myapp_identity_id string = myapp_identity.outputs.id

output account_connectionString string = account.outputs.connectionString

output myapp_identity_clientId string = myapp_identity.outputs.clientId

output acaEnv_AZURE_CONTAINER_REGISTRY_ENDPOINT string = acaEnv.outputs.AZURE_CONTAINER_REGISTRY_ENDPOINT

output fe_identity_id string = fe_identity.outputs.id

output storage_blobEndpoint string = storage.outputs.blobEndpoint

output fe_identity_clientId string = fe_identity.outputs.clientId
