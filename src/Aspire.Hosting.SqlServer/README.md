# SQL Server hosting integration

Use this integration to model, configure, and orchestrate a SQL Server database resource in an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.SqlServer` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.SqlServer
```

## Usage example

In the AppHost, add a SQL Server resource and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var db = builder.AddSqlServer("sql").AddDatabase("db");

var myService = builder.AddProject<Projects.MyService>()
   .WithReference(db);
```

**TypeScript**

```typescript
const db = await builder.addSqlServer("sql").addDatabase("db");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
   .withReference(db);
```

## SQL REPL

Call `WithRepl()` to opt into a **REPL** command on the SQL Server resource in the dashboard:

```csharp
builder.AddSqlServer("sqlserver").WithRepl();
```

```typescript
await builder.addSqlServer("sqlserver").withRepl();
```

REPL access is disabled by default and is available only in run mode. Enable it only for trusted dashboard
users: the shell runs as `sa` and can execute server-side operating system commands when enabled.
Sharing the dashboard through a tunnel, Codespaces, or VS Code remote development also exposes this
capability to users who can execute resource commands.

Use `QUIT` before closing the terminal tab to end the session cleanly. Closing the tab alone can
leave `sqlcmd` running inside the container. Stopping the container also ends any remaining REPL processes.

While the container is running, the command opens `sqlcmd` in the terminal dock. It connects inside the container as `sa` to the `master` database, using the configured password without putting it in command-line arguments. No local SQL client installation is required.

Enter SQL statements followed by `GO` on its own line to execute a batch:

```sql
SELECT DB_NAME();
GO
```

Use `USE [db];` followed by `GO` to switch databases, and `QUIT` to exit. The command trusts the local server's self-signed certificate, matching the integration's local connection string.

### Select the client executable

The REPL invokes `/opt/mssql-tools18/bin/sqlcmd` directly by default, matching the tools version included
in the integration's default `2022-latest` image. It does not probe for another client or invoke a shell.
No override is needed when using the default image.

When pinning an older image that includes `/opt/mssql-tools/bin/sqlcmd`, select the version 17 command.
Replace `<your-pinned-image-tag>` with the tag of that image:

**C#**

```csharp
builder.AddSqlServer("sqlserver")
    .WithImageTag("<your-pinned-image-tag>")
    .WithRepl(options => options.Command = SqlServerReplCommand.Version17);
```

**TypeScript**

```typescript
import { SqlServerReplCommand } from "./.aspire/modules/aspire.mjs";

await builder.addSqlServer("sqlserver")
    .withImageTag("<your-pinned-image-tag>")
    .withRepl({
        configure: async options => {
            await options.command.set(SqlServerReplCommand.Version17);
        }
    });
```

`SqlServerReplCommand.Version17` and `SqlServerReplCommand.Version18` are string constants for the
well-known executable paths. Choose based on the client tools installed in the image, not the SQL Server
version. Changing `Command` does not install tools or change the image.
This command is available only in run mode and uses the configured Docker or Podman runtime.

For an image that installs sqlcmd elsewhere, configure the resource with that executable path instead:

```csharp
sqlServer.WithRepl(options => options.Command = "/usr/local/bin/sqlcmd");
```

```typescript
await sqlServer.withRepl({
    configure: async options => {
        await options.command.set("/usr/local/bin/sqlcmd");
    }
});
```

### Run a wrapper script

Set `Command` to an executable script inside the container to run work before and after the interactive
SQL session. For example, place these two files in a `sql` directory under the AppHost directory.

**`sql/Dockerfile`**

```dockerfile
FROM mcr.microsoft.com/mssql/server:2022-latest
COPY --chmod=755 sqlcmd-wrapper.sh /usr/local/bin/sqlcmd-wrapper
```

**`sql\sqlcmd-wrapper.sh`**

```sh
#!/bin/sh

echo "Running pre-session work"

/opt/mssql-tools18/bin/sqlcmd "$@"
status=$?

echo "Running post-session work"

exit "$status"
```

Replace the echoed messages with the work you want to run, then configure Aspire to build the image
and launch the wrapper:

**C#**

```csharp
builder.AddSqlServer("sqlserver")
    .WithDockerfile("sql")
    .WithRepl(options => options.Command = "/usr/local/bin/sqlcmd-wrapper");
```

**TypeScript**

```typescript
await builder.addSqlServer("sqlserver")
    .withDockerfile("sql")
    .withRepl({
        configure: async options => {
            await options.command.set("/usr/local/bin/sqlcmd-wrapper");
        }
    });
```

The wrapper receives Aspire's usual sqlcmd arguments and the `SQLCMDPASSWORD` environment variable.
Forward `"$@"` to preserve argument boundaries; do not put the password in arguments or log it.
The example preserves sqlcmd's exit code.

Save the script with **LF line endings** and make it executable; the Dockerfile's `COPY --chmod=755`
sets the permissions. Its `#!/bin/sh` line selects the interpreter when the script is executed.
Do not use `exec` to invoke sqlcmd if you need work to run afterward, because it replaces the wrapper
process. Post-session work runs when sqlcmd returns; forcibly stopping the container can prevent it.

## Connection Properties

When you reference a SQL Server resource using `WithReference`, the following connection properties are made available to the consuming project:

### SQL Server server

The SQL Server server resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname or IP address of the SQL Server |
| `Port` | The port number the SQL Server is listening on |
| `Username` | The username for authentication |
| `Password` | The password for authentication |
| `Uri` | The connection URI in mssql:// format, with the format `mssql://{Username}:{Password}@{Host}:{Port}` |
| `JdbcConnectionString` | JDBC-format connection string, with the format `jdbc:sqlserver://{Host}:{Port};trustServerCertificate=true`. User and password credentials are provided as separate `Username` and `Password` properties. |

### SQL Server database

The SQL Server database resource inherits all properties from its parent `SqlServerServerResource` and adds:

| Property Name | Description |
|---------------|-------------|
| `Uri` | The connection URI in mssql:// format, with the format `mssql://{Username}:{Password}@{Host}:{Port}/{DatabaseName}` |
| `JdbcConnectionString` | JDBC connection string with database name, with the format `jdbc:sqlserver://{Host}:{Port};trustServerCertificate=true;databaseName={DatabaseName}`. User and password credentials are provided as separate `Username` and `Password` properties. |
| `DatabaseName` | The name of the database |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

## Additional documentation

https://aspire.dev/integrations/gallery/
https://aspire.dev/integrations/databases/sql-server/sql-server-host/

## Feedback & contributing

https://github.com/microsoft/aspire
