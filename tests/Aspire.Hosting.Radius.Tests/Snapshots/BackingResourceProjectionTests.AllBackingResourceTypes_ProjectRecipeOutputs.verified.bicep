extension radius

@secure()
param pg_password string

resource recipepack 'Radius.Core/recipePacks@2025-08-01-preview' = {
  name: 'default'
  properties: {
    recipes: {
      'Radius.Data/postgreSqlDatabases': {
        recipeKind: 'bicep'
        recipeLocation: 'ghcr.io/radius-project/kube-recipes/postgresqldatabases:latest'
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

resource myenv_legacy 'Applications.Core/environments@2023-10-01-preview' = {
  name: 'myenv'
  properties: {
    compute: {
      kind: 'kubernetes'
      namespace: 'default'
    }
    recipes: {
      'Applications.Datastores/redisCaches': {
        default: {
          templateKind: 'bicep'
          templatePath: 'ghcr.io/radius-project/recipes/local-dev/rediscaches:latest'
        }
      }
      'Applications.Datastores/mongoDatabases': {
        default: {
          templateKind: 'bicep'
          templatePath: 'ghcr.io/radius-project/recipes/local-dev/mongodatabases:latest'
        }
      }
      'Applications.Messaging/rabbitMQQueues': {
        default: {
          templateKind: 'bicep'
          templatePath: 'ghcr.io/radius-project/recipes/local-dev/rabbitmqqueues:latest'
        }
      }
      'Applications.Datastores/sqlDatabases': {
        default: {
          templateKind: 'bicep'
          templatePath: 'ghcr.io/radius-project/recipes/local-dev/sqldatabases:latest'
        }
      }
    }
  }
}

resource app_legacy 'Applications.Core/applications@2023-10-01-preview' = {
  name: 'app'
  properties: {
    environment: myenv_legacy.id
  }
}

resource cache 'Applications.Datastores/redisCaches@2023-10-01-preview' = {
  name: 'cache'
  properties: {
    application: app_legacy.id
    environment: myenv_legacy.id
  }
}

resource pg 'Radius.Data/postgreSqlDatabases@2025-08-01-preview' = {
  name: 'pg'
  properties: {
    application: app.id
    environment: myenv.id
    username: 'postgres'
    password: pg_password
    database: 'pgdb'
  }
}

resource mongo 'Applications.Datastores/mongoDatabases@2023-10-01-preview' = {
  name: 'mongo'
  properties: {
    application: app_legacy.id
    environment: myenv_legacy.id
  }
}

resource rabbit 'Applications.Messaging/rabbitMQQueues@2023-10-01-preview' = {
  name: 'rabbit'
  properties: {
    application: app_legacy.id
    environment: myenv_legacy.id
  }
}

resource sqlserver 'Applications.Datastores/sqlDatabases@2023-10-01-preview' = {
  name: 'sqlserver'
  properties: {
    application: app_legacy.id
    environment: myenv_legacy.id
  }
}

resource api 'Radius.Compute/containers@2025-08-01-preview' = {
  name: 'api'
  properties: {
    containers: {
      api: {
        image: 'myapp/api:latest'
        env: {
          ConnectionStrings__cache: {
            value: '${cache.properties.host}:${cache.properties.port},password=${cache.listSecrets().password}'
          }
          CACHE_HOST: {
            value: cache.properties.host
          }
          CACHE_PORT: {
            value: string(cache.properties.port)
          }
          CACHE_PASSWORD: {
            value: cache.listSecrets().password
          }
          CACHE_URI: {
            value: 'redis://:${uriComponent(cache.listSecrets().password)}@${cache.properties.host}:${cache.properties.port}'
          }
          ConnectionStrings__pgdb: {
            value: 'Host=${pg.properties.host};Port=${pg.properties.port};Username=postgres;Password=${pg_password};Database=pgdb'
          }
          PGDB_HOST: {
            value: pg.properties.host
          }
          PGDB_PORT: {
            value: string(pg.properties.port)
          }
          PGDB_USERNAME: {
            value: 'postgres'
          }
          PGDB_PASSWORD: {
            value: pg_password
          }
          PGDB_URI: {
            value: 'postgresql://postgres:${uriComponent(pg_password)}@${pg.properties.host}:${pg.properties.port}/pgdb'
          }
          PGDB_JDBCCONNECTIONSTRING: {
            value: 'jdbc:postgresql://${pg.properties.host}:${pg.properties.port}/pgdb'
          }
          PGDB_DATABASENAME: {
            value: 'pgdb'
          }
          ConnectionStrings__mongo: {
            value: 'mongodb://admin:${uriComponent(mongo.listSecrets().password)}@${mongo.properties.host}:${mongo.properties.port}/?authSource=admin&authMechanism=SCRAM-SHA-256'
          }
          MONGO_HOST: {
            value: mongo.properties.host
          }
          MONGO_PORT: {
            value: string(mongo.properties.port)
          }
          MONGO_USERNAME: {
            value: 'admin'
          }
          MONGO_PASSWORD: {
            value: mongo.listSecrets().password
          }
          MONGO_AUTHENTICATIONDATABASE: {
            value: 'admin'
          }
          MONGO_AUTHENTICATIONMECHANISM: {
            value: 'SCRAM-SHA-256'
          }
          MONGO_URI: {
            value: 'mongodb://admin:${uriComponent(mongo.listSecrets().password)}@${mongo.properties.host}:${mongo.properties.port}/?authSource=admin&authMechanism=SCRAM-SHA-256'
          }
          ConnectionStrings__rabbit: {
            value: 'amqp://guest:${uriComponent(rabbit.listSecrets().password)}@${rabbit.properties.host}:${rabbit.properties.port}'
          }
          RABBIT_HOST: {
            value: rabbit.properties.host
          }
          RABBIT_PORT: {
            value: string(rabbit.properties.port)
          }
          RABBIT_USERNAME: {
            value: 'guest'
          }
          RABBIT_PASSWORD: {
            value: rabbit.listSecrets().password
          }
          RABBIT_URI: {
            value: 'amqp://guest:${uriComponent(rabbit.listSecrets().password)}@${rabbit.properties.host}:${rabbit.properties.port}'
          }
          ConnectionStrings__sqlserver: {
            value: 'Server=${sqlserver.properties.server},${sqlserver.properties.port};User ID=sa;Password=${sqlserver.listSecrets().password};TrustServerCertificate=true'
          }
          SQLSERVER_HOST: {
            value: sqlserver.properties.server
          }
          SQLSERVER_PORT: {
            value: string(sqlserver.properties.port)
          }
          SQLSERVER_USERNAME: {
            value: 'sa'
          }
          SQLSERVER_PASSWORD: {
            value: sqlserver.listSecrets().password
          }
          SQLSERVER_URI: {
            value: 'mssql://sa:${uriComponent(sqlserver.listSecrets().password)}@${sqlserver.properties.server}:${sqlserver.properties.port}'
          }
          SQLSERVER_JDBCCONNECTIONSTRING: {
            value: 'jdbc:sqlserver://${sqlserver.properties.server}:${sqlserver.properties.port};trustServerCertificate=true'
          }
        }
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
    connections: {
      cache: {
        source: cache.id
      }
      pg: {
        source: pg.id
      }
      mongo: {
        source: mongo.id
      }
      rabbit: {
        source: rabbit.id
      }
      sqlserver: {
        source: sqlserver.id
      }
    }
  }
}