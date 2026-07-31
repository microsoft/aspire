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
    scriptContent: '\$sqlServerFqdn = "\$env:DBSERVER"\n\$sqlDatabaseName = "\$env:DBNAME"\n\$principalName = "\$env:PRINCIPALNAME"\n\$id = "\$env:ID"\n\n# The principal name is interpolated into a T-SQL string literal below. For a user principal\n# it is a UPN, which can legitimately contain an apostrophe (for example o\'brien@contoso.com),\n# so double it up to keep the literal well formed.\n\$escapedPrincipalName = \$principalName.Replace("\'", "\'\'")\n\n\$sqlCmd = @"\nDECLARE @name SYSNAME = \'\$escapedPrincipalName\';\nDECLARE @id UNIQUEIDENTIFIER = \'\$id\';\n\n-- The SID of an Entra principal is the raw bytes of its object id. @castId is that same\n-- value rendered as the 0x... literal that CREATE USER ... WITH SID requires.\nDECLARE @sid VARBINARY(16) = CONVERT(VARBINARY(16), @id);\nDECLARE @castId NVARCHAR(MAX) = CONVERT(VARCHAR(MAX), @sid, 1);\n\n-- Reconciliation below can drop and recreate the principal, so run the whole sequence as a\n-- single unit. XACT_ABORT rolls the transaction back on any error, so a failure between\n-- DROP USER and CREATE USER cannot leave the database with no user for this identity.\nSET XACT_ABORT ON;\nBEGIN TRANSACTION;\n\n-- Only external (Entra) users are considered, because that is the only kind this script\n-- creates. sys.database_principals also holds SQL users, Windows users, roles and dbo, and\n-- any of those sharing this name would have a different sid and so look stale - dropping a\n-- principal we do not own, along with its permissions. Ignoring them leaves @existingSid\n-- null, so CREATE USER below fails with \'Msg 15023: User already exists in current\n-- database\', which is a visible failure rather than a destructive one.\nDECLARE @existingSid VARBINARY(85) = (SELECT sid FROM sys.database_principals WHERE name = @name AND type = \'E\');\n\n-- A user left over from an earlier deployment can carry a stale SID, because deleting and\n-- recreating a managed identity keeps the name but changes the object id. Granting a role to\n-- that principal would report success while the application still failed to log in, so drop\n-- it and let it be recreated against the identity we were actually given.\nIF @existingSid IS NOT NULL AND @existingSid <> @sid\nBEGIN\n    -- QUOTENAME escapes any \']\' in the identifier, which a raw \'[\' + @name + \']\' would not.\n    DECLARE @dropCmd NVARCHAR(MAX) = N\'DROP USER \' + QUOTENAME(@name);\n    EXEC (@dropCmd);\n    SET @existingSid = NULL;\nEND\n\n-- Only create the user when it is missing. This script is re-executed on redeploys, and the\n-- retry loop below can also re-run this batch after a transient failure that occurred *after*\n-- the user was already created. An unguarded CREATE USER would then fail with\n-- \'Msg 15023: User already exists in current database\', turning a transient error into a\n-- permanent deployment failure.\nIF @existingSid IS NULL\nBEGIN\n    -- Construct command: CREATE USER [@name] WITH SID = @castId, TYPE = E;\n    DECLARE @cmd NVARCHAR(MAX) = N\'CREATE USER \' + QUOTENAME(@name) + N\' WITH SID = \' + @castId + N\', TYPE = E;\'\n    EXEC (@cmd);\nEND\n\n-- Assign roles to the user. ALTER ROLE ... ADD MEMBER is a no-op when the principal is already a member.\nDECLARE @role1 NVARCHAR(MAX) = N\'ALTER ROLE db_owner ADD MEMBER \' + QUOTENAME(@name);\nEXEC (@role1);\n\nCOMMIT TRANSACTION;\n\n"@\n# Note: the string terminator must not have whitespace before it, therefore it is not indented.\n\nWrite-Host \$sqlCmd\n\n# This script deliberately avoids the SqlServer PowerShell module (Invoke-Sqlcmd). The Azure\n# deployment script host imports the Az modules before running user scripts, and Az.Resources\n# ships Microsoft.Extensions.Caching.Memory 2.2.0. Importing SqlServer afterwards makes its\n# Always Encrypted Azure Key Vault provider - which is registered unconditionally on the first\n# Invoke-Sqlcmd call, even though nothing here uses Always Encrypted - bind against that older\n# assembly and fail with:\n#   System.MissingMethodException: Method not found: \'Void Microsoft.Extensions.Caching.Memory.MemoryCache..ctor(\n#     Microsoft.Extensions.Options.IOptions`1<Microsoft.Extensions.Caching.Memory.MemoryCacheOptions>)\'.\n# Both published SqlServer module versions have hit this class of conflict at some point, and\n# upstream tracks the real fix - proper assembly load context isolation - in\n# https://github.com/microsoft/SQLServerPSModule/issues/31, which is still open. Pinning a module\n# version only works against one combination of Az module and .NET runtime versions in the image:\n# 22.3.0 worked until this image bumped its Az modules, and 22.4.5.1 could not load on the older\n# .NET 6 based images (https://github.com/microsoft/aspire/issues/9926). Rather than track that\n# matrix, use System.Data.SqlClient, which ships in-box with PowerShell in the\n# azuredeploymentscripts-powershell images, together with a managed identity access token.\n# Nothing here needs Always Encrypted. See https://github.com/microsoft/aspire/issues/18892.\n# The token audience is cloud specific - US Gov uses database.usgovcloudapi.net and China uses\n# database.chinacloudapi.cn - so derive it from the deployment script\'s Az context rather than\n# assuming public cloud. The previous Invoke-Sqlcmd implementation used\n# \'Authentication=Active Directory Default\', which let the driver resolve this automatically.\n\$sqlDnsSuffix = (Get-AzContext).Environment.SqlDatabaseDnsSuffix\nif ([string]::IsNullOrWhiteSpace(\$sqlDnsSuffix)) {\n    \$sqlDnsSuffix = ".database.windows.net"\n}\n\$sqlAudience = "https://" + \$sqlDnsSuffix.TrimStart(\'.\') + "/"\n\n\$connectionString = "Server=tcp:\${sqlServerFqdn},1433;Initial Catalog=\${sqlDatabaseName};Encrypt=True;TrustServerCertificate=False;"\n\n\$maxRetries = 5\n\$retryDelay = 60\n\$attempt = 0\n\$success = \$false\n\nwhile (-not \$success -and \$attempt -lt \$maxRetries) {\n    \$attempt++\n    Write-Host "Attempt \$attempt of \$maxRetries..."\n    \$connection = \$null\n    try {\n        # Acquired inside the loop so a transient token failure is retried like any other\n        # failure, rather than aborting the script before the first attempt.\n        \$tokenResponse = Get-AzAccessToken -ResourceUrl \$sqlAudience\n\n        # Az.Accounts 5.x returns the token as a SecureString, earlier majors return a plain string.\n        \$accessToken = if (\$tokenResponse.Token -is [System.Security.SecureString]) {\n            [System.Net.NetworkCredential]::new("", \$tokenResponse.Token).Password\n        } else {\n            \$tokenResponse.Token\n        }\n\n        \$connection = New-Object System.Data.SqlClient.SqlConnection\n        \$connection.ConnectionString = \$connectionString\n        \$connection.AccessToken = \$accessToken\n        \$connection.Open()\n\n        \$command = \$connection.CreateCommand()\n        \$command.CommandText = \$sqlCmd\n        [void]\$command.ExecuteNonQuery()\n\n        \$success = \$true\n        Write-Host "SQL command succeeded on attempt \$attempt."\n    } catch {\n        Write-Host "Attempt \$attempt failed: \$_"\n        if (\$attempt -lt \$maxRetries) {\n            Write-Host "Retrying in \$retryDelay seconds..."\n            Start-Sleep -Seconds \$retryDelay\n        } else {\n            throw\n        }\n    } finally {\n        if (\$null -ne \$connection) {\n            \$connection.Dispose()\n        }\n    }\n}'
  }
}