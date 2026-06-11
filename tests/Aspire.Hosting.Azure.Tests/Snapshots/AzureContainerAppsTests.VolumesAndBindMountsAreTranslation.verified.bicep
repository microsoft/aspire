@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param env_outputs_volumes_api_0 string

param env_outputs_volumes_api_1 string

param env_outputs_bindmounts_api_0 string

resource api 'Microsoft.App/containerApps@2025-07-01' = {
  name: 'api'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
    }
    environmentId: env_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: 'myimage:latest'
          name: 'api'
          volumeMounts: [
            {
              volumeName: 'v0'
              mountPath: '/path1'
            }
            {
              volumeName: 'v1'
              mountPath: '/path2'
            }
            {
              volumeName: 'bm0'
              mountPath: '/path3'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
      }
      volumes: [
        {
          name: 'v0'
          storageType: 'AzureFile'
          storageName: env_outputs_volumes_api_0
        }
        {
          name: 'v1'
          storageType: 'AzureFile'
          storageName: env_outputs_volumes_api_1
        }
        {
          name: 'bm0'
          storageType: 'AzureFile'
          storageName: env_outputs_bindmounts_api_0
        }
      ]
    }
  }
}