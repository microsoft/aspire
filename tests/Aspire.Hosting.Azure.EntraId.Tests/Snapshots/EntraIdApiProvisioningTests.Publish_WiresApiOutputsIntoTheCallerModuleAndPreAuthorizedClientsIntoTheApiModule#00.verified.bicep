targetScope = 'subscription'

param resourceGroupName string

param location string

param principalId string

param tenantResourceSuffix string = ''

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

module entra_api 'entra-api/entra-api.bicep' = {
  name: 'entra-api'
  scope: rg
  params: {
    tenantResourceSuffix: tenantResourceSuffix
    entra_daemonClientId: entra_daemon.outputs.clientId
  }
}

module entra_web 'entra-web/entra-web.bicep' = {
  name: 'entra-web'
  scope: rg
  params: {
    tenantResourceSuffix: tenantResourceSuffix
    entra_apiClientId: entra_api.outputs.clientId
    entra_apiServicePrincipalId: entra_api.outputs.servicePrincipalId
  }
}

module entra_daemon 'entra-daemon/entra-daemon.bicep' = {
  name: 'entra-daemon'
  scope: rg
  params: {
    tenantResourceSuffix: tenantResourceSuffix
  }
}