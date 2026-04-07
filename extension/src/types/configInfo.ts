/**
 * Shared type definitions for Aspire configuration information.
 * These types are used across multiple files to avoid duplication.
 */

export interface FeatureInfo {
    Name: string;
    Description: string;
    DefaultValue: boolean;
}

export interface PropertyInfo {
    Name: string;
    Type: string;
    Description: string;
    Required: boolean;
    SubProperties?: PropertyInfo[];
    AdditionalPropertiesType?: string;
}

export interface SettingsSchema {
    Properties: PropertyInfo[];
}

export interface ConfigInfo {
    LocalSettingsPath: string;
    GlobalSettingsPath: string;
    AvailableFeatures: FeatureInfo[];
    LocalSettingsSchema: SettingsSchema;
    GlobalSettingsSchema: SettingsSchema;
    ConfigFileSchema?: SettingsSchema;
    Capabilities?: string[];
}
