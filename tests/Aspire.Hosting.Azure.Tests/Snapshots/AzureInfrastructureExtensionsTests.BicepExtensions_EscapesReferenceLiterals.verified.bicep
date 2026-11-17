extension 'br:registry.example/extensions/\${name}\'\\types:v1' as custom

@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location