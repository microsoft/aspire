@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param cluster_name string

param cluster_group string

resource aks 'Microsoft.ContainerService/managedClusters@2026-07-01' existing = {
  name: cluster_name
  scope: resourceGroup('cluster-subscription', cluster_group)
}