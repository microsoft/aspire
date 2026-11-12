extension radius

@secure()
param pg_password string

@secure()
param rabbit_password string

param rabbituser string

resource recipepack 'Radius.Core/recipePacks@2025-08-01-preview' = {
  name: 'default'
  properties: {
    recipes: {
      'Radius.Data/redisCaches': {
        kind: 'bicep'
        source: 'ghcr.io/radius-project/kube-recipes/rediscaches:ebdeec9509036f2b2f271e41661e6fcfe45eda89'
      }
      'Radius.Data/postgreSqlDatabases': {
        kind: 'bicep'
        source: 'ghcr.io/radius-project/kube-recipes/postgresqldatabases:latest'
      }
      'Radius.Messaging/rabbitMQ': {
        kind: 'bicep'
        source: 'ghcr.io/radius-project/kube-recipes/rabbitmq:ebdeec9509036f2b2f271e41661e6fcfe45eda89'
      }
      'Radius.Security/secrets': {
        kind: 'bicep'
        source: 'ghcr.io/radius-project/kube-recipes/secrets:latest'
      }
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

resource myenv_legacy 'Applications.Core/environments@2023-10-01-preview' = {
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

resource rabbit_password_secret 'Radius.Security/secrets@2025-08-01-preview' = {
  name: 'rabbit-password-secret'
  properties: {
    environment: myenv.id
    application: app.id
    data: {
      password: {
        value: rabbit_password
        encoding: 'string'
      }
    }
  }
}

resource api_env_secret 'Radius.Security/secrets@2025-08-01-preview' = {
  name: 'api-env-secret'
  properties: {
    environment: myenv.id
    application: app.id
    data: {
      ConnectionStrings__pgdb: {
        value: 'Host=${pg.properties.host};Port=${pg.properties.port};Username=postgres;Password=${pg_password};Database=pgdb'
        encoding: 'string'
      }
      PGDB_PASSWORD: {
        value: pg_password
        encoding: 'string'
      }
      PGDB_URI: {
        value: 'postgresql://postgres:${uriComponent(pg_password)}@${pg.properties.host}:${pg.properties.port}/pgdb'
        encoding: 'string'
      }
      ConnectionStrings__mongo: {
        value: 'mongodb://admin:${uriComponent(mongo.listSecrets().password)}@${mongo.properties.host}:${mongo.properties.port}/?authSource=admin&authMechanism=SCRAM-SHA-256'
        encoding: 'string'
      }
      MONGO_PASSWORD: {
        value: mongo.listSecrets().password
        encoding: 'string'
      }
      MONGO_URI: {
        value: 'mongodb://admin:${uriComponent(mongo.listSecrets().password)}@${mongo.properties.host}:${mongo.properties.port}/?authSource=admin&authMechanism=SCRAM-SHA-256'
        encoding: 'string'
      }
      ConnectionStrings__rabbit: {
        value: 'amqp://${uriComponent(rabbituser)}:${uriComponent(rabbit_password)}@${rabbit.properties.host}:${rabbit.properties.port}'
        encoding: 'string'
      }
      RABBIT_PASSWORD: {
        value: rabbit_password
        encoding: 'string'
      }
      RABBIT_URI: {
        value: 'amqp://${uriComponent(rabbituser)}:${uriComponent(rabbit_password)}@${rabbit.properties.host}:${rabbit.properties.port}'
        encoding: 'string'
      }
      ConnectionStrings__sqlserver: {
        value: 'Server=${sqlserver.properties.server},${sqlserver.properties.port};User ID=sa;Password=${sqlserver.listSecrets().password};TrustServerCertificate=true'
        encoding: 'string'
      }
      SQLSERVER_PASSWORD: {
        value: sqlserver.listSecrets().password
        encoding: 'string'
      }
      SQLSERVER_URI: {
        value: 'mssql://sa:${uriComponent(sqlserver.listSecrets().password)}@${sqlserver.properties.server}:${sqlserver.properties.port}'
        encoding: 'string'
      }
    }
  }
}

resource cache 'Radius.Data/redisCaches@2025-08-01-preview' = {
  name: 'cache'
  properties: {
    application: app.id
    environment: myenv.id
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

resource rabbit 'Radius.Messaging/rabbitMQ@2025-08-01-preview' = {
  name: 'rabbit'
  properties: {
    application: app.id
    environment: myenv.id
    username: rabbituser
    password: rabbit_password_secret.id
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
            value: '${cache.properties.host}:${cache.properties.port},password='
          }
          CACHE_HOST: {
            value: cache.properties.host
          }
          CACHE_PORT: {
            value: string(cache.properties.port)
          }
          CACHE_PASSWORD: {
            value: ''
          }
          CACHE_URI: {
            value: 'redis://:@${cache.properties.host}:${cache.properties.port}'
          }
          ConnectionStrings__pgdb: {
            valueFrom: {
              secretKeyRef: {
                secretName: 'api-env-secret'
                key: 'ConnectionStrings__pgdb'
              }
            }
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
            valueFrom: {
              secretKeyRef: {
                secretName: 'api-env-secret'
                key: 'PGDB_PASSWORD'
              }
            }
          }
          PGDB_URI: {
            valueFrom: {
              secretKeyRef: {
                secretName: 'api-env-secret'
                key: 'PGDB_URI'
              }
            }
          }
          PGDB_JDBCCONNECTIONSTRING: {
            value: 'jdbc:postgresql://${pg.properties.host}:${pg.properties.port}/pgdb'
          }
          PGDB_DATABASENAME: {
            value: 'pgdb'
          }
          ConnectionStrings__mongo: {
            valueFrom: {
              secretKeyRef: {
                secretName: 'api-env-secret'
                key: 'ConnectionStrings__mongo'
              }
            }
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
            valueFrom: {
              secretKeyRef: {
                secretName: 'api-env-secret'
                key: 'MONGO_PASSWORD'
              }
            }
          }
          MONGO_AUTHENTICATIONDATABASE: {
            value: 'admin'
          }
          MONGO_AUTHENTICATIONMECHANISM: {
            value: 'SCRAM-SHA-256'
          }
          MONGO_URI: {
            valueFrom: {
              secretKeyRef: {
                secretName: 'api-env-secret'
                key: 'MONGO_URI'
              }
            }
          }
          ConnectionStrings__rabbit: {
            valueFrom: {
              secretKeyRef: {
                secretName: 'api-env-secret'
                key: 'ConnectionStrings__rabbit'
              }
            }
          }
          RABBIT_HOST: {
            value: rabbit.properties.host
          }
          RABBIT_PORT: {
            value: string(rabbit.properties.port)
          }
          RABBIT_USERNAME: {
            value: rabbituser
          }
          RABBIT_PASSWORD: {
            valueFrom: {
              secretKeyRef: {
                secretName: 'api-env-secret'
                key: 'RABBIT_PASSWORD'
              }
            }
          }
          RABBIT_URI: {
            valueFrom: {
              secretKeyRef: {
                secretName: 'api-env-secret'
                key: 'RABBIT_URI'
              }
            }
          }
          ConnectionStrings__sqlserver: {
            valueFrom: {
              secretKeyRef: {
                secretName: 'api-env-secret'
                key: 'ConnectionStrings__sqlserver'
              }
            }
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
            valueFrom: {
              secretKeyRef: {
                secretName: 'api-env-secret'
                key: 'SQLSERVER_PASSWORD'
              }
            }
          }
          SQLSERVER_URI: {
            valueFrom: {
              secretKeyRef: {
                secretName: 'api-env-secret'
                key: 'SQLSERVER_URI'
              }
            }
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