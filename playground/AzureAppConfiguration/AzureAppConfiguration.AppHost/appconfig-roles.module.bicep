@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param appconfig_outputs_name string

param principalType string

param principalId string

resource appconfig 'Microsoft.AppConfiguration/configurationStores@2024-06-01' existing = {
  name: appconfig_outputs_name
}

resource appconfig_AppConfigurationDataOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appconfig.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5ae67dd6-50cb-40e7-96ff-dc2bfa4b606b'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5ae67dd6-50cb-40e7-96ff-dc2bfa4b606b')
    principalType: principalType
  }
  scope: appconfig
}