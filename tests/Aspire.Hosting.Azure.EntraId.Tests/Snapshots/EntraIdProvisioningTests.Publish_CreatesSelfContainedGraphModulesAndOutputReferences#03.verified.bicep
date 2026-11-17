param location string = resourceGroup().location
param tenantId string
param clientId string

output authority string = 'https://login.microsoftonline.com/${tenantId}/v2.0'
output applicationClientId string = clientId