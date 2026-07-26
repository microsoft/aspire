@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param mysqlserver_outputs_name string

param mysqlserver_outputs_sqlserveradminname string

param principalId string

param principalName string

resource mysqlserver 'Microsoft.Sql/servers@2023-08-01' existing = {
  name: mysqlserver_outputs_name
}

resource sqlServerAdmin 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: mysqlserver_outputs_sqlserveradminname
}

resource mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: principalName
}

resource script_mysqlserver_todosdb 'Microsoft.Resources/deploymentScripts@2023-08-01' = {
  name: take('script-${uniqueString('mysqlserver', principalName, 'todosdb', resourceGroup().id)}', 24)
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${sqlServerAdmin.id}': { }
    }
  }
  kind: 'AzurePowerShell'
  properties: {
    azPowerShellVersion: '10.0'
    retentionInterval: 'PT1H'
    environmentVariables: [
      {
        name: 'DBNAME'
        value: 'todosdb'
      }
      {
        name: 'DBSERVER'
        value: mysqlserver.properties.fullyQualifiedDomainName
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
    scriptContent: '\$sqlServerFqdn = "\$env:DBSERVER"\r\n\$sqlDatabaseName = "\$env:DBNAME"\r\n\$principalName = "\$env:PRINCIPALNAME"\r\n\$id = "\$env:ID"\r\n\r\n\$sqlCmd = @"\r\nDECLARE @name SYSNAME = \'\$principalName\';\r\nDECLARE @id UNIQUEIDENTIFIER = \'\$id\';\r\n\r\n-- Convert the guid to the right type\r\nDECLARE @castId NVARCHAR(MAX) = CONVERT(VARCHAR(MAX), CONVERT (VARBINARY(16), @id), 1);\r\n\r\n-- Only create the user when it is missing. This script is re-executed on redeploys, and the\r\n-- retry loop below can also re-run this batch after a transient failure that occurred *after*\r\n-- the user was already created. An unguarded CREATE USER would then fail with\r\n-- \'Msg 15023: User already exists in current database\', turning a transient error into a\r\n-- permanent deployment failure.\r\nIF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @name)\r\nBEGIN\r\n    -- Construct command: CREATE USER [@name] WITH SID = @castId, TYPE = E;\r\n    DECLARE @cmd NVARCHAR(MAX) = N\'CREATE USER [\' + @name + \'] WITH SID = \' + @castId + \', TYPE = E;\'\r\n    EXEC (@cmd);\r\nEND\r\n\r\n-- Assign roles to the user. ALTER ROLE ... ADD MEMBER is a no-op when the principal is already a member.\r\nDECLARE @role1 NVARCHAR(MAX) = N\'ALTER ROLE db_owner ADD MEMBER [\' + @name + \']\';\r\nEXEC (@role1);\r\n\r\n"@\r\n# Note: the string terminator must not have whitespace before it, therefore it is not indented.\r\n\r\nWrite-Host \$sqlCmd\r\n\r\n# This script deliberately avoids the SqlServer PowerShell module (Invoke-Sqlcmd). The Azure\r\n# deployment script host imports the Az modules before running user scripts, and Az.Resources\r\n# ships Microsoft.Extensions.Caching.Memory 2.2.0. Importing SqlServer afterwards makes its\r\n# Always Encrypted Azure Key Vault provider - which is registered unconditionally on the first\r\n# Invoke-Sqlcmd call, even though nothing here uses Always Encrypted - bind against that older\r\n# assembly and fail with:\r\n#   System.MissingMethodException: Method not found: \'Void Microsoft.Extensions.Caching.Memory.MemoryCache..ctor(\r\n#     Microsoft.Extensions.Options.IOptions`1<Microsoft.Extensions.Caching.Memory.MemoryCacheOptions>)\'.\r\n# Both published SqlServer module versions hit this (22.3.0 here, 22.4.5.1 in\r\n# https://github.com/microsoft/aspire/issues/9926), so instead we use System.Data.SqlClient, which\r\n# ships in-box with PowerShell 7 in the azuredeploymentscripts-powershell images, together with a\r\n# managed identity access token. See https://github.com/microsoft/aspire/issues/18892.\r\n\$tokenResponse = Get-AzAccessToken -ResourceUrl "https://database.windows.net/"\r\n\r\n# Az.Accounts 5.x returns the token as a SecureString, earlier majors return a plain string.\r\n\$accessToken = if (\$tokenResponse.Token -is [System.Security.SecureString]) {\r\n    [System.Net.NetworkCredential]::new("", \$tokenResponse.Token).Password\r\n} else {\r\n    \$tokenResponse.Token\r\n}\r\n\r\n\$connectionString = "Server=tcp:\${sqlServerFqdn},1433;Initial Catalog=\${sqlDatabaseName};Encrypt=True;TrustServerCertificate=False;"\r\n\r\n\$maxRetries = 5\r\n\$retryDelay = 60\r\n\$attempt = 0\r\n\$success = \$false\r\n\r\nwhile (-not \$success -and \$attempt -lt \$maxRetries) {\r\n    \$attempt++\r\n    Write-Host "Attempt \$attempt of \$maxRetries..."\r\n    \$connection = \$null\r\n    try {\r\n        \$connection = New-Object System.Data.SqlClient.SqlConnection\r\n        \$connection.ConnectionString = \$connectionString\r\n        \$connection.AccessToken = \$accessToken\r\n        \$connection.Open()\r\n\r\n        \$command = \$connection.CreateCommand()\r\n        \$command.CommandText = \$sqlCmd\r\n        [void]\$command.ExecuteNonQuery()\r\n\r\n        \$success = \$true\r\n        Write-Host "SQL command succeeded on attempt \$attempt."\r\n    } catch {\r\n        Write-Host "Attempt \$attempt failed: \$_"\r\n        if (\$attempt -lt \$maxRetries) {\r\n            Write-Host "Retrying in \$retryDelay seconds..."\r\n            Start-Sleep -Seconds \$retryDelay\r\n        } else {\r\n            throw\r\n        }\r\n    } finally {\r\n        if (\$null -ne \$connection) {\r\n            \$connection.Dispose()\r\n        }\r\n    }\r\n}'
  }
}