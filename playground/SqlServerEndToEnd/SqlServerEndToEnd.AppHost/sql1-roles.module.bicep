@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param sql1_outputs_name string

param sql1_outputs_sqlserveradminname string

param principalId string

param principalName string

param principalType string

resource sql1 'Microsoft.Sql/servers@2023-08-01' existing = {
  name: sql1_outputs_name
}

resource sqlServerAdmin 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: sql1_outputs_sqlserveradminname
}

resource mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: principalName
}

resource script_sql1_db1 'Microsoft.Resources/deploymentScripts@2023-08-01' = {
  name: take('script-${uniqueString('sql1', principalName, 'db1', resourceGroup().id)}', 24)
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
        value: 'db1'
      }
      {
        name: 'DBSERVER'
        value: sql1.properties.fullyQualifiedDomainName
      }
      {
        name: 'PRINCIPALTYPE'
        value: principalType
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
    scriptContent: '\$sqlServerFqdn = "\$env:DBSERVER"\r\n\$sqlDatabaseName = "\$env:DBNAME"\r\n\$principalName = "\$env:PRINCIPALNAME"\r\n\$id = "\$env:ID"\r\n\r\n# The principal name is interpolated into a T-SQL string literal below. For a user principal\r\n# it is a UPN, which can legitimately contain an apostrophe (for example o\'brien@contoso.com),\r\n# so double it up to keep the literal well formed.\r\n\$escapedPrincipalName = \$principalName.Replace("\'", "\'\'")\r\n\r\n\$sqlCmd = @"\r\nDECLARE @name SYSNAME = \'\$escapedPrincipalName\';\r\nDECLARE @id UNIQUEIDENTIFIER = \'\$id\';\r\n\r\n-- The SID of an Entra principal is the raw bytes of its object id. @castId is that same\r\n-- value rendered as the 0x... literal that CREATE USER ... WITH SID requires.\r\nDECLARE @sid VARBINARY(16) = CONVERT(VARBINARY(16), @id);\r\nDECLARE @castId NVARCHAR(MAX) = CONVERT(VARCHAR(MAX), @sid, 1);\r\n\r\n-- Reconciliation below can drop and recreate the principal, so run the whole sequence as a\r\n-- single unit. XACT_ABORT rolls the transaction back on any error, so a failure between\r\n-- DROP USER and CREATE USER cannot leave the database with no user for this identity.\r\nSET XACT_ABORT ON;\r\nBEGIN TRANSACTION;\r\n\r\n-- Only external (Entra) users are considered, because that is the only kind this script\r\n-- creates. sys.database_principals also holds SQL users, Windows users, roles and dbo, and\r\n-- any of those sharing this name would have a different sid and so look stale - dropping a\r\n-- principal we do not own, along with its permissions. Ignoring them leaves @existingSid\r\n-- null, so CREATE USER below fails with \'Msg 15023: User already exists in current\r\n-- database\', which is a visible failure rather than a destructive one.\r\nDECLARE @existingSid VARBINARY(85) = (SELECT sid FROM sys.database_principals WHERE name = @name AND type = \'E\');\r\n\r\n-- A user left over from an earlier deployment can carry a stale SID, because deleting and\r\n-- recreating a managed identity keeps the name but changes the object id. Granting a role to\r\n-- that principal would report success while the application still failed to log in, so drop\r\n-- it and let it be recreated against the identity we were actually given.\r\nIF @existingSid IS NOT NULL AND @existingSid <> @sid\r\nBEGIN\r\n    -- QUOTENAME escapes any \']\' in the identifier, which a raw \'[\' + @name + \']\' would not.\r\n    DECLARE @dropCmd NVARCHAR(MAX) = N\'DROP USER \' + QUOTENAME(@name);\r\n    EXEC (@dropCmd);\r\n    SET @existingSid = NULL;\r\nEND\r\n\r\n-- Only create the user when it is missing. This script is re-executed on redeploys, and the\r\n-- retry loop below can also re-run this batch after a transient failure that occurred *after*\r\n-- the user was already created. An unguarded CREATE USER would then fail with\r\n-- \'Msg 15023: User already exists in current database\', turning a transient error into a\r\n-- permanent deployment failure.\r\nIF @existingSid IS NULL\r\nBEGIN\r\n    -- Construct command: CREATE USER [@name] WITH SID = @castId, TYPE = E;\r\n    DECLARE @cmd NVARCHAR(MAX) = N\'CREATE USER \' + QUOTENAME(@name) + N\' WITH SID = \' + @castId + N\', TYPE = E;\'\r\n    EXEC (@cmd);\r\nEND\r\n\r\n-- Assign roles to the user. ALTER ROLE ... ADD MEMBER is a no-op when the principal is already a member.\r\nDECLARE @role1 NVARCHAR(MAX) = N\'ALTER ROLE db_owner ADD MEMBER \' + QUOTENAME(@name);\r\nEXEC (@role1);\r\n\r\nCOMMIT TRANSACTION;\r\n\r\n"@\r\n# Note: the string terminator must not have whitespace before it, therefore it is not indented.\r\n\r\nWrite-Host \$sqlCmd\r\n\r\n# This script deliberately avoids the SqlServer PowerShell module (Invoke-Sqlcmd). The Azure\r\n# deployment script host imports the Az modules before running user scripts, and Az.Resources\r\n# ships Microsoft.Extensions.Caching.Memory 2.2.0. Importing SqlServer afterwards makes its\r\n# Always Encrypted Azure Key Vault provider - which is registered unconditionally on the first\r\n# Invoke-Sqlcmd call, even though nothing here uses Always Encrypted - bind against that older\r\n# assembly and fail with:\r\n#   System.MissingMethodException: Method not found: \'Void Microsoft.Extensions.Caching.Memory.MemoryCache..ctor(\r\n#     Microsoft.Extensions.Options.IOptions`1<Microsoft.Extensions.Caching.Memory.MemoryCacheOptions>)\'.\r\n# Both published SqlServer module versions have hit this class of conflict at some point, and\r\n# upstream tracks the real fix - proper assembly load context isolation - in\r\n# https://github.com/microsoft/SQLServerPSModule/issues/31, which is still open. Pinning a module\r\n# version only works against one combination of Az module and .NET runtime versions in the image:\r\n# 22.3.0 worked until this image bumped its Az modules, and 22.4.5.1 could not load on the older\r\n# .NET 6 based images (https://github.com/microsoft/aspire/issues/9926). Rather than track that\r\n# matrix, use System.Data.SqlClient, which ships in-box with PowerShell in the\r\n# azuredeploymentscripts-powershell images, together with a managed identity access token.\r\n# Nothing here needs Always Encrypted. See https://github.com/microsoft/aspire/issues/18892.\r\n# The token audience is cloud specific - US Gov uses database.usgovcloudapi.net and China uses\r\n# database.chinacloudapi.cn - so derive it from the deployment script\'s Az context rather than\r\n# assuming public cloud. The previous Invoke-Sqlcmd implementation used\r\n# \'Authentication=Active Directory Default\', which let the driver resolve this automatically.\r\n\$sqlDnsSuffix = (Get-AzContext).Environment.SqlDatabaseDnsSuffix\r\nif ([string]::IsNullOrWhiteSpace(\$sqlDnsSuffix)) {\r\n    \$sqlDnsSuffix = ".database.windows.net"\r\n}\r\n\$sqlAudience = "https://" + \$sqlDnsSuffix.TrimStart(\'.\') + "/"\r\n\r\n\$connectionString = "Server=tcp:\${sqlServerFqdn},1433;Initial Catalog=\${sqlDatabaseName};Encrypt=True;TrustServerCertificate=False;"\r\n\r\n\$maxRetries = 5\r\n\$retryDelay = 60\r\n\$attempt = 0\r\n\$success = \$false\r\n\r\nwhile (-not \$success -and \$attempt -lt \$maxRetries) {\r\n    \$attempt++\r\n    Write-Host "Attempt \$attempt of \$maxRetries..."\r\n    \$connection = \$null\r\n    try {\r\n        # Acquired inside the loop so a transient token failure is retried like any other\r\n        # failure, rather than aborting the script before the first attempt.\r\n        \$tokenResponse = Get-AzAccessToken -ResourceUrl \$sqlAudience\r\n\r\n        # Az.Accounts 5.x returns the token as a SecureString, earlier majors return a plain string.\r\n        \$accessToken = if (\$tokenResponse.Token -is [System.Security.SecureString]) {\r\n            [System.Net.NetworkCredential]::new("", \$tokenResponse.Token).Password\r\n        } else {\r\n            \$tokenResponse.Token\r\n        }\r\n\r\n        \$connection = New-Object System.Data.SqlClient.SqlConnection\r\n        \$connection.ConnectionString = \$connectionString\r\n        \$connection.AccessToken = \$accessToken\r\n        \$connection.Open()\r\n\r\n        \$command = \$connection.CreateCommand()\r\n        \$command.CommandText = \$sqlCmd\r\n        [void]\$command.ExecuteNonQuery()\r\n\r\n        \$success = \$true\r\n        Write-Host "SQL command succeeded on attempt \$attempt."\r\n    } catch {\r\n        Write-Host "Attempt \$attempt failed: \$_"\r\n        if (\$attempt -lt \$maxRetries) {\r\n            Write-Host "Retrying in \$retryDelay seconds..."\r\n            Start-Sleep -Seconds \$retryDelay\r\n        } else {\r\n            throw\r\n        }\r\n    } finally {\r\n        if (\$null -ne \$connection) {\r\n            \$connection.Dispose()\r\n        }\r\n    }\r\n}'
  }
}