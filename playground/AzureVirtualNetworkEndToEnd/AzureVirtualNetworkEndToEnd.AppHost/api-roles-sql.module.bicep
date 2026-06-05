@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param sql_outputs_name string

param sql_outputs_sqlserveradminname string

param vnet_outputs_sql_aci_subnet_id string

param sql_store_outputs_name string

param principalId string

param principalName string

param private_endpoints_sql_pe_outputs_name string

param private_endpoints_files_pe_outputs_name string

resource sql 'Microsoft.Sql/servers@2023-08-01' existing = {
  name: sql_outputs_name
}

resource sqlServerAdmin 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: sql_outputs_sqlserveradminname
}

resource sql_store 'Microsoft.Storage/storageAccounts@2024-01-01' existing = {
  name: sql_store_outputs_name
}

resource mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: principalName
}

resource private_endpoints_sql_pe 'Microsoft.Network/privateEndpoints@2025-05-01' existing = {
  name: private_endpoints_sql_pe_outputs_name
}

resource private_endpoints_files_pe 'Microsoft.Network/privateEndpoints@2025-05-01' existing = {
  name: private_endpoints_files_pe_outputs_name
}

resource script_sql_sqldb 'Microsoft.Resources/deploymentScripts@2023-08-01' = {
  name: take('script-${uniqueString('sql', principalName, 'sqldb', resourceGroup().id)}', 24)
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${sqlServerAdmin.id}': { }
    }
  }
  kind: 'AzurePowerShell'
  properties: {
    azPowerShellVersion: '14.0'
    retentionInterval: 'PT1H'
    containerSettings: {
      subnetIds: [
        {
          id: vnet_outputs_sql_aci_subnet_id
        }
      ]
    }
    environmentVariables: [
      {
        name: 'DBNAME'
        value: 'sqldb'
      }
      {
        name: 'DBSERVER'
        value: sql.properties.fullyQualifiedDomainName
      }
      {
        name: 'PRINCIPALTYPE'
        value: 'ServicePrincipal'
      }
      {
        name: 'PRINCIPALNAME'
        value: principalName
      }
      {
        name: 'ID'
        value: mi.properties.clientId
      }
    ]
    scriptContent: '\$sqlServerFqdn = "\$env:DBSERVER"\r\n\$sqlDatabaseName = "\$env:DBNAME"\r\n\$principalName = "\$env:PRINCIPALNAME"\r\n\$id = "\$env:ID"\r\n\r\n# Install SqlServer module - using specific version to avoid breaking changes in 22.4.5.1 (see https://github.com/microsoft/aspire/issues/9926)\r\nInstall-Module -Name SqlServer -RequiredVersion 22.3.0 -Force -AllowClobber -Scope CurrentUser\r\nImport-Module SqlServer\r\n\r\n\$sqlCmd = @"\r\nDECLARE @name SYSNAME = \'\$principalName\';\r\nDECLARE @id UNIQUEIDENTIFIER = \'\$id\';\r\n\r\n-- Convert the guid to the right type\r\nDECLARE @castId NVARCHAR(MAX) = CONVERT(VARCHAR(MAX), CONVERT (VARBINARY(16), @id), 1);\r\n\r\n-- Construct command: CREATE USER [@name] WITH SID = @castId, TYPE = E;\r\nDECLARE @cmd NVARCHAR(MAX) = N\'CREATE USER [\' + @name + \'] WITH SID = \' + @castId + \', TYPE = E;\'\r\nEXEC (@cmd);\r\n\r\n-- Assign roles to the new user\r\nDECLARE @role1 NVARCHAR(MAX) = N\'ALTER ROLE db_owner ADD MEMBER [\' + @name + \']\';\r\nEXEC (@role1);\r\n\r\n"@\r\n# Note: the string terminator must not have whitespace before it, therefore it is not indented.\r\n\r\nWrite-Host \$sqlCmd\r\n\r\n\$connectionString = "Server=tcp:\${sqlServerFqdn},1433;Initial Catalog=\${sqlDatabaseName};Authentication=Active Directory Default;"\r\n\r\n\$maxRetries = 5\r\n\$retryDelay = 60\r\n\$attempt = 0\r\n\$success = \$false\r\n\r\nwhile (-not \$success -and \$attempt -lt \$maxRetries) {\r\n    \$attempt++\r\n    Write-Host "Attempt \$attempt of \$maxRetries..."\r\n    try {\r\n        Invoke-Sqlcmd -ConnectionString \$connectionString -Query \$sqlCmd\r\n        \$success = \$true\r\n        Write-Host "SQL command succeeded on attempt \$attempt."\r\n    } catch {\r\n        Write-Host "Attempt \$attempt failed: \$_"\r\n        if (\$attempt -lt \$maxRetries) {\r\n            Write-Host "Retrying in \$retryDelay seconds..."\r\n            Start-Sleep -Seconds \$retryDelay\r\n        } else {\r\n            throw\r\n        }\r\n    }\r\n}'
    storageAccountSettings: {
      storageAccountName: sql_store_outputs_name
    }
  }
  dependsOn: [
    private_endpoints_sql_pe
    private_endpoints_files_pe
  ]
}