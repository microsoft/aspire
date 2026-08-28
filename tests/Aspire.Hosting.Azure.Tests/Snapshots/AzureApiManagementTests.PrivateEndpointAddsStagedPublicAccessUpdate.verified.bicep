@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param apim_outputs_name string

param private_endpoint_subnet_apim_pe_outputs_name string

param _apim_forceUpdateTag string = utcNow()

resource apim 'Microsoft.ApiManagement/service@2025-03-01-preview' existing = {
  name: apim_outputs_name
}

resource private_endpoint_subnet_apim_pe 'Microsoft.Network/privateEndpoints@2025-05-01' existing = {
  name: private_endpoint_subnet_apim_pe_outputs_name
}

resource _apim_disablePublicAccessIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('apim-network-id-${uniqueString(apim.id)}', 128)
  location: location
}

resource _apim_disablePublicAccessRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(apim.id, _apim_disablePublicAccessIdentity.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'e022efe7-f5ba-4159-bbe4-b44f577e9b61'))
  properties: {
    principalId: _apim_disablePublicAccessIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'e022efe7-f5ba-4159-bbe4-b44f577e9b61')
    principalType: 'ServicePrincipal'
  }
  scope: apim
}

resource _apim_privateEndpointReaderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(private_endpoint_subnet_apim_pe.id, _apim_disablePublicAccessIdentity.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'acdd72a7-3385-48ef-bd42-f606fba81ae7'))
  properties: {
    principalId: _apim_disablePublicAccessIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'acdd72a7-3385-48ef-bd42-f606fba81ae7')
    principalType: 'ServicePrincipal'
  }
  scope: private_endpoint_subnet_apim_pe
}

resource _apim_disablePublicAccess 'Microsoft.Resources/deploymentScripts@2023-08-01' = {
  name: take('apim-network-update-${uniqueString(apim.id)}', 64)
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${_apim_disablePublicAccessIdentity.id}': { }
    }
  }
  kind: 'AzureCLI'
  properties: {
    azCliVersion: '2.64.0'
    retentionInterval: 'PT1H'
    environmentVariables: [
      {
        name: 'APIM_ID'
        value: apim.id
      }
      {
        name: 'PRIVATE_ENDPOINT_ID'
        value: private_endpoint_subnet_apim_pe.id
      }
    ]
    forceUpdateTag: _apim_forceUpdateTag
    scriptContent: 'approved=false\nfor attempt in \$(seq 1 60); do\n  connection_state=\$(az resource show \\\n    --ids "\${PRIVATE_ENDPOINT_ID}" \\\n    --api-version 2024-05-01 \\\n    --query "properties.privateLinkServiceConnections[0].properties.privateLinkServiceConnectionState.status" \\\n    --output tsv)\n  case "\${connection_state}" in\n    Approved)\n      approved=true\n      break\n      ;;\n    Rejected|Disconnected)\n      echo "The API Management private endpoint connection was \${connection_state}. Public network access was not disabled." >&2\n      exit 1\n      ;;\n  esac\n  sleep 10\ndone\n\nif [ "\${approved}" != "true" ]; then\n  echo "The API Management private endpoint connection was not approved before the timeout. Public network access was not disabled." >&2\n  exit 1\nfi\n\nupdated=false\nfor attempt in \$(seq 1 30); do\n  if az resource update \\\n    --ids "\${APIM_ID}" \\\n    --api-version 2025-03-01-preview \\\n    --set properties.publicNetworkAccess=Disabled; then\n    updated=true\n    break\n  fi\n  sleep 10\ndone\n\nif [ "\${updated}" != "true" ]; then\n  echo "Failed to start the public network access update." >&2\n  exit 1\nfi\n\nfor attempt in \$(seq 1 60); do\n  public_access=\$(az resource show \\\n    --ids "\${APIM_ID}" \\\n    --api-version 2025-03-01-preview \\\n    --query properties.publicNetworkAccess \\\n    --output tsv)\n  if [ "\${public_access}" = "Disabled" ]; then\n    exit 0\n  fi\n  sleep 10\ndone\n\necho "Failed to disable public network access after the private endpoint was created." >&2\nexit 1'
    timeout: 'PT30M'
  }
  dependsOn: [
    _apim_disablePublicAccessRole
    _apim_privateEndpointReaderRole
    private_endpoint_subnet_apim_pe
  ]
}