extension radius

resource recipepack 'Radius.Core/recipePacks@2025-08-01-preview' = {
  name: 'default'
  properties: {
    recipes: {
      'Radius.Data/postgreSqlDatabases': {
        recipeKind: 'bicep'
        recipeLocation: 'ghcr.io/radius-project/recipes/local-dev/postgresqldatabases:latest'
      }
      'Radius.Compute/containers': {
        recipeKind: 'bicep'
        recipeLocation: 'ghcr.io/radius-project/kube-recipes/containers:latest'
      }
    }
  }
}

resource myenv 'Radius.Core/environments@2025-08-01-preview' = {
  name: 'myenv'
  properties: {
    recipePacks: [
      recipepack.id
    ]
    providers: {
      kubernetes: {
        namespace: 'default'
      }
    }
  }
}

resource app 'Radius.Core/applications@2025-08-01-preview' = {
  name: 'app'
  properties: {
    environment: myenv.id
  }
}

resource db 'Radius.Data/postgreSqlDatabases@2025-08-01-preview' = {
  name: 'db'
  properties: {
    application: app.id
    environment: myenv.id
  }
}

resource api 'Radius.Compute/containers@2025-08-01-preview' = {
  name: 'api'
  properties: {
    containers: {
      api: {
        image: 'myapp/api:latest'
      }
    }
    application: app.id
    environment: myenv.id
  }
}