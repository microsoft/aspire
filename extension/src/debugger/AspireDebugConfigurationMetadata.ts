export const appHostTelemetryTargetPathConfigKey = '__aspireAppHostTelemetryTargetPath';
export const appHostLaunchReservationIdConfigKey = '__aspireAppHostLaunchReservationId';
export const appHostLaunchTokenConfigKey = '__aspireAppHostLaunchToken';
export const appHostRestartSourceSessionIdConfigKey = '__aspireAppHostRestartSourceSessionId';

// This internal field survives VS Code's two debug-configuration resolver stages so the
// eventual CLI process can distinguish a launch.json-owned target from a persisted default.
export const appHostSelectionOriginConfigKey = '__aspireAppHostSelectionOrigin';

/** Who chose the AppHost this session launches. */
export type AppHostSelectionOrigin = 'explicit-launch-configuration' | 'default-discovery' | 'user-selection';
