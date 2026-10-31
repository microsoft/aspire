extension radius

param web_http_BrowserOrigin string

param web_http_BrowserOrigins string

param web_http_BrowserOriginsJson string

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

resource web 'Radius.Compute/containers@2025-08-01-preview' = {
  name: 'web'
  properties: {
    containers: {
      web: {
        image: 'image:latest'
        ports: {
          http: {
            containerPort: 8080
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
          ORIGIN: {
            value: web_http_BrowserOrigin
          }
          ORIGINS: {
            value: web_http_BrowserOrigins
          }
          JSON: {
            value: web_http_BrowserOriginsJson
          }
          CONFIG: {
            value: 'origin=${web_http_BrowserOrigin}'
          }
        }
      }
    }
    application: app.id
    environment: myenv.id
  }
}