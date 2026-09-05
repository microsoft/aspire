extension radius

resource myenv 'Applications.Core/environments@2023-10-01-preview' = {
  name: 'myenv'
  properties: {
    compute: {
      kind: 'kubernetes'
      namespace: 'default'
    }
    recipes: {
      'Applications.Datastores/mongoDatabases': {
        default: {
          templateKind: 'bicep'
          templatePath: 'ghcr.io/radius-project/recipes/local-dev/mongodatabases:latest'
        }
      }
    }
  }
}

resource app 'Applications.Core/applications@2023-10-01-preview' = {
  name: 'app'
  properties: {
    environment: myenv.id
  }
}

resource mongo 'Applications.Datastores/mongoDatabases@2023-10-01-preview' = {
  name: 'mongo'
  properties: {
    application: app.id
    environment: myenv.id
  }
}