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
    scriptContent: '\$sqlServerFqdn = "\$env:DBSERVER"\n\$sqlDatabaseName = "\$env:DBNAME"\n\$principalName = "\$env:PRINCIPALNAME"\n\$id = "\$env:ID"\n\n\$sqlCmd = @"\nDECLARE @name SYSNAME = \'\$principalName\';\nDECLARE @id UNIQUEIDENTIFIER = \'\$id\';\n\n-- Convert the guid to the right type\nDECLARE @castId NVARCHAR(MAX) = CONVERT(VARCHAR(MAX), CONVERT (VARBINARY(16), @id), 1);\n\n-- Only create the user when it is missing. This script is re-executed on redeploys, and the\n-- retry loop below can also re-run this batch after a transient failure that occurred *after*\n-- the user was already created. An unguarded CREATE USER would then fail with\n-- \'Msg 15023: User already exists in current database\', turning a transient error into a\n-- permanent deployment failure.\nIF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @name)\nBEGIN\n    -- Construct command: CREATE USER [@name] WITH SID = @castId, TYPE = E;\n    DECLARE @cmd NVARCHAR(MAX) = N\'CREATE USER [\' + @name + \'] WITH SID = \' + @castId + \', TYPE = E;\'\n    EXEC (@cmd);\nEND\n\n-- Assign roles to the user. ALTER ROLE ... ADD MEMBER is a no-op when the principal is already a member.\nDECLARE @role1 NVARCHAR(MAX) = N\'ALTER ROLE db_owner ADD MEMBER [\' + @name + \']\';\nEXEC (@role1);\n\n"@\n# Note: the string terminator must not have whitespace before it, therefore it is not indented.\n\nWrite-Host \$sqlCmd\n\n# This script deliberately avoids the SqlServer PowerShell module (Invoke-Sqlcmd). The Azure\n# deployment script host imports the Az modules before running user scripts, and Az.Resources\n# ships Microsoft.Extensions.Caching.Memory 2.2.0. Importing SqlServer afterwards makes its\n# Always Encrypted Azure Key Vault provider - which is registered unconditionally on the first\n# Invoke-Sqlcmd call, even though nothing here uses Always Encrypted - bind against that older\n# assembly and fail with:\n#   System.MissingMethodException: Method not found: \'Void Microsoft.Extensions.Caching.Memory.MemoryCache..ctor(\n#     Microsoft.Extensions.Options.IOptions`1<Microsoft.Extensions.Caching.Memory.MemoryCacheOptions>)\'.\n# Both published SqlServer module versions hit this (22.3.0 here, 22.4.5.1 in\n# https://github.com/microsoft/aspire/issues/9926), so instead we use System.Data.SqlClient, which\n# ships in-box with PowerShell 7 in the azuredeploymentscripts-powershell images, together with a\n# managed identity access token. See https://github.com/microsoft/aspire/issues/18892.\n\$tokenResponse = Get-AzAccessToken -ResourceUrl "https://database.windows.net/"\n\n# Az.Accounts 5.x returns the token as a SecureString, earlier majors return a plain string.\n\$accessToken = if (\$tokenResponse.Token -is [System.Security.SecureString]) {\n    [System.Net.NetworkCredential]::new("", \$tokenResponse.Token).Password\n} else {\n    \$tokenResponse.Token\n}\n\n\$connectionString = "Server=tcp:\${sqlServerFqdn},1433;Initial Catalog=\${sqlDatabaseName};Encrypt=True;TrustServerCertificate=False;"\n\n\$maxRetries = 5\n\$retryDelay = 60\n\$attempt = 0\n\$success = \$false\n\nwhile (-not \$success -and \$attempt -lt \$maxRetries) {\n    \$attempt++\n    Write-Host "Attempt \$attempt of \$maxRetries..."\n    \$connection = \$null\n    try {\n        \$connection = New-Object System.Data.SqlClient.SqlConnection\n        \$connection.ConnectionString = \$connectionString\n        \$connection.AccessToken = \$accessToken\n        \$connection.Open()\n\n        \$command = \$connection.CreateCommand()\n        \$command.CommandText = \$sqlCmd\n        [void]\$command.ExecuteNonQuery()\n\n        \$success = \$true\n        Write-Host "SQL command succeeded on attempt \$attempt."\n    } catch {\n        Write-Host "Attempt \$attempt failed: \$_"\n        if (\$attempt -lt \$maxRetries) {\n            Write-Host "Retrying in \$retryDelay seconds..."\n            Start-Sleep -Seconds \$retryDelay\n        } else {\n            throw\n        }\n    } finally {\n        if (\$null -ne \$connection) {\n            \$connection.Dispose()\n        }\n    }\n}'
  }
}