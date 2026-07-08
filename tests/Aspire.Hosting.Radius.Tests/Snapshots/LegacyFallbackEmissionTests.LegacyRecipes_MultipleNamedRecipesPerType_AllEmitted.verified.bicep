extension radius

resource myenv 'Applications.Core/environments@2023-10-01-preview' = {
  name: 'myenv'
  properties: {
    compute: {
      kind: 'kubernetes'
      namespace: 'default'
    }
    recipes: {
      'Applications.Datastores/redisCaches': {
        recipeA: {
          templateKind: 'bicep'
          templatePath: 'ghcr.io/myorg/recipes/redis-a:latest'
        }
        recipeB: {
          templateKind: 'bicep'
          templatePath: 'ghcr.io/myorg/recipes/redis-b:latest'
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

resource cacheA 'Applications.Datastores/redisCaches@2023-10-01-preview' = {
  name: 'cacheA'
  properties: {
    application: app.id
    environment: myenv.id
    recipe: {
      name: 'recipeA'
    }
  }
}

resource cacheB 'Applications.Datastores/redisCaches@2023-10-01-preview' = {
  name: 'cacheB'
  properties: {
    application: app.id
    environment: myenv.id
    recipe: {
      name: 'recipeB'
    }
  }
}