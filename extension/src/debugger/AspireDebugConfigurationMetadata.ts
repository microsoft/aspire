export const appHostTelemetryTargetPathConfigKey = '__aspireAppHostTelemetryTargetPath';

// This internal field survives VS Code's two debug-configuration resolver stages so the
// eventual CLI process can distinguish a launch.json-owned target from a persisted default.
export const appHostSelectionOriginConfigKey = '__aspireAppHostSelectionOrigin';

/**
 * Who chose the AppHost this session launches.
 *
 * The CLI decides from this value whether the target may become the workspace default recorded in
 * `aspire.config.json`. `user-selection` and `default-discovery` are statements about the project;
 * `explicit-launch-configuration` (a `launch.json` entry naming a specific target) and
 * `agent-selection` (a language-model tool picking a target on the user's behalf) are scoped to the
 * one invocation and must never replace a default the user already has. Adding a value here without
 * teaching `ProjectLocator.s_sessionScopedSelectionOrigins` about it makes the CLI treat it as a
 * user selection.
 */
export type AppHostSelectionOrigin = 'explicit-launch-configuration' | 'default-discovery' | 'user-selection' | 'agent-selection';
