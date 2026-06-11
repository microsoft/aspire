@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param sql_outputs_name string

param sql_outputs_sqlserveradminname string

param principalId string

param principalName string

resource sql 'Microsoft.Sql/servers@2023-08-01' existing = {
  name: sql_outputs_name
}

resource sqlServerAdmin 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: sql_outputs_sqlserveradminname
}

resource mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: principalName
}

resource script_sql_db 'Microsoft.Resources/deploymentScripts@2023-08-01' = {
  name: take('script-${uniqueString('sql', principalName, 'db', resourceGroup().id)}', 24)
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
    environmentVariables: [
      {
        name: 'DBNAME'
        value: 'db'
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
    scriptContent: '\$sqlServerFqdn = "\$env:DBSERVER"\n\$sqlDatabaseName = "\$env:DBNAME"\n\$principalName = "\$env:PRINCIPALNAME"\n\$id = "\$env:ID"\n\n# Install SqlServer module - using specific version to avoid breaking changes in 22.4.5.1 (see https://github.com/microsoft/aspire/issues/9926)\nInstall-Module -Name SqlServer -RequiredVersion 22.3.0 -Force -AllowClobber -Scope CurrentUser\nImport-Module SqlServer\n\n\$sqlCmd = @"\nDECLARE @name SYSNAME = \'\$principalName\';\nDECLARE @id UNIQUEIDENTIFIER = \'\$id\';\n\n-- Convert the guid to the right type\nDECLARE @castId NVARCHAR(MAX) = CONVERT(VARCHAR(MAX), CONVERT (VARBINARY(16), @id), 1);\n\n-- Construct command: CREATE USER [@name] WITH SID = @castId, TYPE = E;\nDECLARE @cmd NVARCHAR(MAX) = N\'CREATE USER [\' + @name + \'] WITH SID = \' + @castId + \', TYPE = E;\'\nEXEC (@cmd);\n\n-- Assign roles to the new user\nDECLARE @role1 NVARCHAR(MAX) = N\'ALTER ROLE db_owner ADD MEMBER [\' + @name + \']\';\nEXEC (@role1);\n\n"@\n# Note: the string terminator must not have whitespace before it, therefore it is not indented.\n\nWrite-Host \$sqlCmd\n\n\$connectionString = "Server=tcp:\${sqlServerFqdn},1433;Initial Catalog=\${sqlDatabaseName};Authentication=Active Directory Default;"\n\n\$maxRetries = 5\n\$retryDelay = 60\n\$attempt = 0\n\$success = \$false\n\nwhile (-not \$success -and \$attempt -lt \$maxRetries) {\n    \$attempt++\n    Write-Host "Attempt \$attempt of \$maxRetries..."\n    try {\n        Invoke-Sqlcmd -ConnectionString \$connectionString -Query \$sqlCmd\n        \$success = \$true\n        Write-Host "SQL command succeeded on attempt \$attempt."\n    } catch {\n        Write-Host "Attempt \$attempt failed: \$_"\n        if (\$attempt -lt \$maxRetries) {\n            Write-Host "Retrying in \$retryDelay seconds..."\n            Start-Sleep -Seconds \$retryDelay\n        } else {\n            throw\n        }\n    }\n}'
  }
}