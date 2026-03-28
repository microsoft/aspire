// Resource state values returned by the Aspire runtime
export const ResourceState = {
    Running: 'Running',
    Active: 'Active',
    Starting: 'Starting',
    Building: 'Building',
    Stopping: 'Stopping',
    Stopped: 'Stopped',
    Waiting: 'Waiting',
    NotStarted: 'NotStarted',
    Finished: 'Finished',
    Exited: 'Exited',
    FailedToStart: 'FailedToStart',
    RuntimeUnhealthy: 'RuntimeUnhealthy',
} as const;

// Health status values returned by the Aspire runtime
export const HealthStatus = {
    Healthy: 'Healthy',
    Unhealthy: 'Unhealthy',
    Degraded: 'Degraded',
} as const;

// State style values used for visual presentation
export const StateStyle = {
    Error: 'error',
    Warning: 'warning',
} as const;

// Resource type values
export const ResourceType = {
    Project: 'Project',
    Container: 'Container',
    Executable: 'Executable',
    Parameter: 'Parameter',
} as const;

export type ResourceStateValue = typeof ResourceState[keyof typeof ResourceState];
export type HealthStatusValue = typeof HealthStatus[keyof typeof HealthStatus];
export type StateStyleValue = typeof StateStyle[keyof typeof StateStyle];
export type ResourceTypeValue = typeof ResourceType[keyof typeof ResourceType];
