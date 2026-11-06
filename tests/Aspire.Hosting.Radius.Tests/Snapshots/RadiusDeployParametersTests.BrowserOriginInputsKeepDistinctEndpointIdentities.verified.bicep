extension radius

param a_b_c_BrowserOrigins string

param a_b_c_BrowserOrigins_2eb5787a775df1cb string

resource recipepack 'Radius.Core/recipePacks@2025-08-01-preview' = {
  name: 'default'
  properties: {
    recipes: {
      'Radius.Compute/containers': {
        kind: 'bicep'
        source: 'ghcr.io/radius-project/kube-recipes/containers:latest'
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

resource a_b 'Radius.Compute/containers@2025-08-01-preview' = {
  name: 'a-b'
  properties: {
    containers: {
      'a-b': {
        image: 'image:latest'
        ports: {
          c: {
            containerPort: 80
            protocol: 'TCP'
          }
        }
      }
    }
    application: app.id
    environment: myenv.id
  }
}

resource a 'Radius.Compute/containers@2025-08-01-preview' = {
  name: 'a'
  properties: {
    containers: {
      a: {
        image: 'image:latest'
        ports: {
          'b-c': {
            containerPort: 80
            protocol: 'TCP'
          }
        }
      }
    }
    application: app.id
    environment: myenv.id
  }
}

resource consumer 'Radius.Compute/containers@2025-08-01-preview' = {
  name: 'consumer'
  properties: {
    containers: {
      consumer: {
        image: 'image:latest'
        env: {
          FIRST: {
            value: a_b_c_BrowserOrigins
          }
          SECOND: {
            value: a_b_c_BrowserOrigins_2eb5787a775df1cb
          }
          REPEATED: {
            value: a_b_c_BrowserOrigins
          }
        }
      }
    }
    application: app.id
    environment: myenv.id
  }
}