targetScope = 'subscription'

param resourceGroupName string

param location string

param principalId string

param moduleResourceGroup string = 'rg-shared'

param moduleSubscription string = '12345678-1234-1234-1234-123456789012'

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

module resourceGroupScoped 'resourceGroupScoped/resourceGroupScoped.bicep' = {
  name: 'resourceGroupScoped'
  scope: resourceGroup(moduleSubscription, moduleResourceGroup)
  params: {
    location: location
  }
}

module subscriptionScoped 'subscriptionScoped/subscriptionScoped.bicep' = {
  name: 'subscriptionScoped'
  scope: subscription(moduleSubscription)
  params: {
    location: location
  }
}

module tenantScoped 'tenantScoped/tenantScoped.bicep' = {
  name: 'tenantScoped'
  scope: tenant()
  params: {
    location: location
  }
}