@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param aks_outputs_name string

resource aks 'Microsoft.ContainerService/managedClusters@2026-07-01' existing = {
  name: aks_outputs_name
}