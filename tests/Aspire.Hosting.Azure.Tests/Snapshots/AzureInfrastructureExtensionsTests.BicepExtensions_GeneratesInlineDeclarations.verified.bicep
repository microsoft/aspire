extension 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0' as microsoftGraphV1

@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

output tenantId string = tenant().tenantId