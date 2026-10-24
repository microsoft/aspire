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
    entraDaemonClientId: entra_daemon.outputs.clientId
  }
}

module entra_web 'entra-web/entra-web.bicep' = {
  name: 'entra-web'
  scope: rg
  params: {
    tenantResourceSuffix: tenantResourceSuffix
    entraApiClientId: entra_api.outputs.clientId
    entraApiServicePrincipalId: entra_api.outputs.servicePrincipalId
  }
}

module entra_daemon 'entra-daemon/entra-daemon.bicep' = {
  name: 'entra-daemon'
  scope: rg
  params: {
    tenantResourceSuffix: tenantResourceSuffix
  }
}