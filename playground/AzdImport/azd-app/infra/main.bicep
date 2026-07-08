// Existing azd infrastructure for the Contoso Store template.
//
// The Aspire importer preserves this folder by reference (it records the infra path and emits a
// diagnostic) instead of regenerating it, so the customer keeps the exact Bicep they already ship.
// It is intentionally minimal here because the playground runs locally on containers and never
// provisions Azure; in a real azd repo this is the full deployment definition.
targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the environment used to generate a short unique hash for resources.')
param environmentName string

@minLength(1)
@description('Primary location for all resources.')
param location string

var tags = { 'azd-env-name': environmentName }

resource rg 'Microsoft.Resources/resourceGroups@2022-09-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: tags
}

output AZURE_LOCATION string = location
output AZURE_RESOURCE_GROUP string = rg.name
