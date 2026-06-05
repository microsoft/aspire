# Running Aspire applications inside an integrated developer environment (IDE)

## Application host, DCP, and IDE
When Aspire application host program is run, it does not launch application service programs or supporting emulators/containers directly. Instead, the application host relies on another program ("app orchestrator") called `DCP`.

When Visual Studio (or another IDE) starts the Aspire application host, Aspire-specific application model is created from user code in app host project. This model is then converted to another, language-agnostic model that DCP understands, and is submitted by app host to DCP for execution. The models are quite similar: for example, Aspire project (application services) are modeled as `Executable` objects on DCP side; containers become `Container` objects in DCP world.

By default, `Executable` objects that are part of DCP workload are run as ordinary operating system processes. That means, DCP uses appropriate OS call to start a child process that executes the program specified by `Executable` spec. For debugging application services this is often not convenient or useful: it is difficult to debug service startup code this way, or restart the process automatically when source code changes. This is why DCP supports an alternative method of running `Executable` object, which is called **IDE execution**.

With IDE execution DCP delegates the task of running the program represented by `Executable` object to an external entity. The entity can be an IDE such as Visual Studio or Visual Studio Code, or a CLI tool like `dotnet watch`, but in the rest of this document we will refer to it as **IDE**.

When IDE execution is enabled for an `Executable` object, DCP will go through normal execution preparation steps (such as computing the values of environment variables for the `Executable`), but instead of creating an OS process, DCP will issue a request to the IDE to run the `Executable` program, passing all relevant information in the request. DCP then relies on the IDE to start the program, creating a **run session**. The IDE is expected to inform DCP about changes to the session (e.g. program finished execution) and DCP reflects these changes in the workload model.

## Enabling IDE execution

For IDE execution to work, two conditions need to be fulfilled:

1. DCP needs to be told how to contact the IDE (what is the **IDE session endpoint**, specifically).
1. The `ExecutionType` property for the `Executable` object needs to be set to `IDE` (default is `Process`, which indicates OS process-based execution).

Only one IDE (one IDE session endpoint) is supported per DCP instance. The IDE session endpoint is configured via environment variables:

| Environment variable | Value |
| ----- | ----- |
| `DEBUG_SESSION_PORT` | The port DCP should use to talk to the IDE session endpoint. DCP will use `http://localhost:<value of DEBUG_SESSION_PORT>` as the IDE session endpoint base URL. Required. |
| `DEBUG_SESSION_TOKEN` | Security (bearer) token for talking to the IDE session endpoint. This token will be attached to every request via Authorization HTTP header. Required. |
| `DEBUG_SESSION_SERVER_CERTIFICATE` | If present, provides base64-encoded server certificate used for authenticating IDE endpoint and securing the communication via TLS. <br/> The certificate can be self-signed, but it must include subject alternative name, set to "localhost". Setting canonical name (`cn`) is not sufficient. <br/> If the certificate is provided, all communication with the IDE will occur via `https` and `wss` (the latter for the session change notifications). There will be NO fallback to `http` or `ws` or un-authenticated mode. Using `https` and `wss` is optional but strongly recommended. |
| `DEBUG_SESSION_INFO`               | If present, the same JSON document as returned by the `/info` request, defined below, used to determine which executable types are supported by the IDE |

> Note: the most important use case for the IDE execution is to facilitate application services debugging. The word "debug" appears in environment variable names that DCP uses to connect to IDE session endpoint, but IDE execution does not always mean that the service is running under a debugger.

### Using multiple execution types in the same workload

It is possible to have a workload that uses both IDE execution for some `Executable` objects, and process execution for other `Executable` objects. Every `Executable` object is treated separately according to the `ExecutionType` spec property.

If multiple replicas are used (created via `ExecutableReplicaSet` objects), all replicas belonging to the same replica set will use the same execution type.

## IDE session endpoint requests

The IDE session endpoint accepts requests delivered via HTTP protocol. All request use JSON as the request/response payload format, as appropriate per request type.

### Mandatory request headers

Every request must contain the following headers:

| Header | Value | Description |
| --- | --- | ------ |
| `Authorization` | `Bearer <security token>` | Security (bearer) token, as read from `DEBUG_SESSION_TOKEN` environment variable. Authenticates the DCP instance to IDE session endpoint. |
| `Microsoft-Developer-DCP-Instance-ID` | `<unique ID>` | A unique ID generated by DCP for every DCP program execution. Can be used by the IDE to distinguish between multiple DCP instances running in parallel. It is a random string of exactly 12 characters, consisting of lowercase ASCII letters and numbers. <br/> If `DCP_INSTANCE_ID_PREFIX` environment variable is set, the contents of this variable will be prepended to the unique instance ID DCP generates. |

### Create session request

Used to create a new run session for particular `Executable`.

**HTTP verb and path** <br/>
`PUT /run_session`

**Headers** <br/>
`Authorization: Bearer <security token>` <br/>
`Microsoft-Developer-DCP-Instance-ID <unique ID>` <br/>
`Content-Type: application/json`

**Payload** <br/>

The payload is best explained using an example:

```jsonc
{
    "launch_configurations": [
        {
            // Indicates the type of the launch configuration.
            // This is a required property for all kinds of launch configurations.
            // The value "project" indicates this is a service that has an associated Visual Studio project file.
            "type": "project",

            "project_path": "(Path to Visual Studio project file for the program)",

            // ... other launch configuration properties
        }

        // ... other launch configurations may be included, as appropriate for the service being launched
    ]

    // Environment variable settings (added on top of those inherited from IDE/user environment,
    // and those read from the launch profile). Optional.
    "env" : [
        // Environment variables are modeled as objects, with 'name' and 'value' property, for example:
        { "name": "NO_COLOR", "value": "1" },
        { "name": "EMPTY_VALUE_VAR", "value": "" }
    ],

    // Invocation arguments for the program (modeled as array of strings). Optional.
    "args": [
        "-v",
        "1"
    ]
}
```

**Response** <br/>
If the execution session is created successfully, the return status code should be 200 OK or 201 Created. The response should include the created run session identifier in the `Location` header:

`Location: https://localhost:<IDE endpoint port>/run_session/<new run ID>`

If the session cannot be created, appropriate 4xx or 5xx status code should be returned. The response might also return a description of the problem as part of the status line, [or in the response body](#error-reporting).

### Stop session request

Used to stop an in-progress run session

**HTTP verb and path** <br/>
`DELETE /run_session/<run session ID>`

**Headers** <br/>
`Microsoft-Developer-DCP-Instance-ID <unique ID>` <br/>
`Authorization: Bearer <security token>`

**Response** <br/>
If the session exists and can be stopped, the IDE should reply with 200 OK status code.

If the session does not exist, the IDE should reply with 204 No Content.

If the session cannot be stopped, appropriate 4xx or 5xx status code should be returned. The response might also return a description of the problem as part of the status line, [or in the response body](#error-reporting).

### Subscribe to session change notifications request

Used by DCP to subscribe to run session change notification.

**HTTP verb and path** <br/>
`GET /run_session/notify`

**Headers** <br/>
`Authorization: Bearer <security token>` <br/>
`Microsoft-Developer-DCP-Instance-ID <unique ID>` <br/>
(+ WebSocket connection upgrade headers)

**Response** <br/>
If successful, the connection should be upgraded to a WebSocket connection, which will be then used by the IDE to stream run session change notifications to DCP. See next paragraph for description of possible change notifications.

### IDE endpoint information request

Used by DCP to get information about capabilities of the IDE run session endpoint.

**HTTP verb and path** <br/>
`GET /info`

**Headers** <br/>
`Microsoft-Developer-DCP-Instance-ID <unique ID>` <br/>
`Authorization: Bearer <security token>`

**Response** <br/>
A JSON document describing the capabilities of the IDE run session endpoint. For example:
```jsonc
{
    "protocols_supported": [ "2024-04-23", "2025-10-01" ],
    "supported_launch_configurations": [ "project", "python" ]
}
```

The properties of the IDE endpoint information document are:

| Property | Description | Type |
| --- | --------- | --- |
| `protocols_supported` | List of protocols supported by the IDE endpoint. See [protocol versioning](#protocol-versioning) for more information. | `string[]` |
| `supported_launch_configurations` | List of launch configurations supported by the IDE endpoint. This property is optional; if omitted, DCP will assume that only `project` launch configuration is supported. | `string[]` |

## Launch configurations (run session requests)

The run session creation request contains one or more launch configurations for the session. The following launch configuration types are well-known:

### Project launch configuration (type: `project`)

Project launch configuration contains details for launching programs that have project files compatible with Visual Studio IDE.

**Project launch configuration properties**

| Property | Description | Required? |
| --- | --------- | --- |
| `type` | Launch configuration type indicator; must be `project`. | Required |
| `project_path` | Path to the project file for the program that is being launched. | Required |
| `mode` | Specifies the launch mode. Currently supported modes are `Debug` (run the project under the debugger) and `NoDebug` (run the project without debugging). | Optional, defaults to `Debug`. |
| `launch_profile` | The name of the launch profile to be used for project execution. See below for more details on how the launch profile should be processed. | Optional |
| `disable_launch_profile` | If set to `true`, the project will be launched without a launch profile and the value of "launch_profile" parameter is disregarded. | Optional |

**Launch profile processing**

Launch profiles should be applied to service run sessions according to the following rules:

1. The values of `launch_profile` and `disable_launch_profile` properties determine the **base profile** used for the service run session. The base profile may be nonexistent (empty), or it might be that one of the launch profiles defined for the service project serves as the base profile, see point 3 below.

2. Environment variable values (`env` property) and invocation arguments (`args` property) specified by the run session request always take precedence over settings present in the launch profile. Specifically:

    a. Environment variable values **override** (are applied on top of) the environment variable values from the base profile.

    b. **If present**, invocation arguments from the run session request **completely replace** invocation arguments from the base profile. In particular, an empty array (`[]`) specified in the request means no invocation arguments should be used at all, even if base profile is present and has some invocation arguments specified. On the other hand, if the `args` run session request property is absent, or set to `null`, it means the run session request does not specify any invocation arguments for the service, and thus if the base profile exists and contains invocation arguments, those from the base profile should be used.

3. The base profile is determined according to following rules:

    a. If `disable_launch_profile` property is set to `true` in project launch configuration, there is no base profile, regardless of the value of `launch_profile` property.

    b. If the `launch_profile` property is set, the IDE should check whether the service project has a launch profile with the name equal to the value of `launch_profile` property. If such profile is found, it should serve as the base profile. If not, there is no base profile.

    c. If `launch_profile` property is absent, the IDE should check whether the service project has a launch profile with the same name as the profile used to launch Aspire application host project. If such profile is found, it should serve as the base profile. Otherwise there is no base profile.

**Working folder for project execution**

Unless the launch profile specifies otherwise (via `WorkingDirectory` property), each project should be launched using its own folder as the working folder (working directory).

### Python launch configuration (type: `python`)

Python launch configuration contains details for launching python scripts.

**Python launch configuration properties**

| Property | Description | Required? |
|----------------|--------|-------|
| `type` | Launch configuration type indicator; must be `python`.  | Required |
| `program_path` | Path to the python startup file, or if `module` is specified a path to the working directory for the app. | Required |
| `mode` | Specifies the launch mode. Currently supported modes are `Debug` (run the project under the debugger) and `NoDebug` (run the project without debugging). | Optional, defaults to `Debug`. |
| `interpreter_path` | Path to the python interpreter to use. If omitted, the global python installation will be used. | Optional |
| `module` | The name of the module to execute, equivalent to python -m. | Optional |

## Run session change notifications

The run session change notifications are delivered from IDE to DCP via a WebSocket connection. The format of notification is JSON Lines (one JSON object per line of text).

All run sessions share the same notification stream. Notifications are delivered in near-real-time as sessions change--there is no memory/reply of notifications for sessions that occurred in the past. DCP subscribes to run session change notifications before it makes the first request to create a run session; this ensures that it will receive all change notifications for all sessions it creates.

### Connection control

If the IDE endpoint needs to shut down, it should send the [WebSocket Close message](https://www.rfc-editor.org/rfc/rfc6455#section-5.5.1) before terminating the WebSocket connection. Similarly, when DCP instance is shutting down, it will send the Close message before terminating its connection.

### Common notification properties

Every run change notification has the following properties:

| Property | Description | Type |
| --- | --------- | --- |
| `notification_type` | One of `processRestarted`, `sessionTerminated`, `serviceLogs`, or `sessionMessage`: indicates the type of notification. | `string` (limited set of values) |
| `session_id` | The ID of the run session that the notification is related to. | `string` |

### Process restarted notification

The process (re)started notification in emitted when the run is started, and whenever the IDE restarts the service (upon developer request). Properties specific to this notification are:

| Property | Description | Type |
| --- | --------- | --- |
| `notification_type` | Must be `processRestarted` | `string` |
| `pid` | The process ID of the service process associated with the run session. | `number` (representing unsigned 32-bit integer) |

This notification may be omitted if the PID associated with the run session is unknown.

### Session terminated notification

Session terminated notification is emitted when the session is terminated (the program ends, or is terminated by the developer). Properties specific to this notification are:

| Property | Description | Type |
| --- | --------- | --- |
| `notification_type` | Must be `sessionTerminated` | `string` |
| `exit_code` | The exit code of the process associated with the run session. <br/><br/> Can be omitted, indicating exit code could not be captured, or is not applicable, e.g. when a debug session ended for a reason other than program exit. | `number` (representing unsigned 32-bit integer) |

### Log notification

The log notification is emitted when the service program writes something to standard output stream (`stdout`) or standard error (`stderr`). Properties specific to this notification are:

| Property | Description | Type |
| --- | --------- | --- |
| `notification_type` | Must be `serviceLogs` | `string` |
| `is_std_err` | True if the output comes from standard error stream, otherwise false (implying standard output stream). | `boolean` |
| `log_message` | The text written by the service program. | `string` |

### Session message notification

The session message notification is emitted when the IDE needs to notify the client (and the Aspire developer) about asynchronous events related to a debug session. Session messages have 3 levels: error, info, and debug. By default errors and info messages are displayed to the developer; debug messages are suppressed unless the client was started in debug mode.

Properties specific to session message notification are:

| Property | Description | Type |
| --- | --------- | --- |
| `notification_type` | Must be `sessionMessage` | `string` |
| `level` | Must be `error`, `info`, or `debug` | `string` |
| `message` | The content of the message. | `string` |
| `code` | The error code. See [error reporting](#error-reporting) for more information. Only valid for error messages (`level == error`). | `string` (required for error messages) |
| `details` | Error details. See [error reporting](#error-reporting) for more information. Only valid for error messages (`level == error`). | `ErrorDetail[]` (optional) |

## Error reporting

When the IDE encounters an error during request processing, the request response should include a body in JSON format, with a single property named "error", for example:

```jsonc
{
    "error": {
        // An "error detail" object
        "code": "ProjectNotFound",
        "message": "The project 'C:\nonexistent\path\frontend.csproj' was not found",
        "details": []
    }
}
```

The value of the `error` property is an `ErrorDetail` object with the following properties:

| Property | Description | Type, required? |
| --- | --------- | --- |
| `code` | A machine-readable code that corresponds to distinctive error condition. If the cause of an error can be narrowed down reliably (e.g. file referenced by launch configuration was not found, or the request body does not parse as valid JSON), the corresponding error should be unique. <br/><br/> There will be cases when the cause for the error cannot be pinpointed, and in these cases it is OK to return a catch-all error code e.g. `UnexpectedError`. | `string` (required) |
| `message` | A human-readable message explaining the nature of the error, and providing suggestions for resolution. DCP will display this message as part of the Aspire application host execution log. | `string` (required) |
| `details` | An array of `ErrorDetail` objects providing additional information about the error. | `ErrorDetail[]` (optional) |

## Protocol versioning

When making a request to the IDE, DCP will include an `api-version` parameter to indicate the version of the protocol used, for example:

`PUT /run_session?api-version=2024-03-03`

The version always follows `YYYY-mm-dd` format and allows for older/equal/newer comparison.

If the protocol version is old (no longer supported by the IDE), the IDE should return a 400 Bad Request response with the message indicating that the developer should consider upgrading the Aspire libraries and tooling used by their application.

If the protocol version is newer than the latest the IDE supports, the IDE should make an attempt to parse the request according to its latest supported version. If that fails, the IDE should return `400 Bad Request` error.

> The `api-version` parameter will be attached to all requests except the `/info` request (which is designed to facilitate protocol version negotiation).

### Well-known protocol versions

**`2024-03-03`** <br/>
Applicable Aspire versions: `9.0` up to and including `9.5`. <br/>
Changes: none (baseline version)

**`2024-04-23`** <br/>
Applicable Aspire versions: `9.0` up to and including `9.5`. <br/>
Changes:
- Expects the IDE endpoint to be able to respond to WebSocket `ping` messages with a proper `pong` message. Used for detecting notification connection failures.

**`2025-10-01`** <br/>
Applicable Aspire versions: `13.0` and above (DCP will downgrade to `2024-03-03` or `2024-04-23` if necessary). <br/>
Changes:
- Adds [session message notification](#session-message-notification) as one of the run session change notification types.
- Adds `supported_launch_configurations` property to [IDE endpoint information request](#ide-endpoint-information-request).
