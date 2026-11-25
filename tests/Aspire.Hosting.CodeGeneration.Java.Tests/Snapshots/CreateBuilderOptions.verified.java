// CreateBuilderOptions.java - GENERATED CODE - DO NOT EDIT

package aspire;

import java.util.HashMap;
import java.util.Map;

/** CreateBuilderOptions DTO. */
@SuppressWarnings({"all", "unchecked", "serial"})
public class CreateBuilderOptions implements JsonSerializable {
    private String[] args;
    private String projectDirectory;
    private String appHostFilePath;
    private String containerRegistryOverride;
    private boolean disableDashboard;
    private String dashboardApplicationName;
    private boolean allowUnsecuredTransport;
    private boolean enableResourceLogging;
    private Boolean defaultResourceWatchEnabled;

    public String[] getArgs() { return args; }
    public void setArgs(String[] value) { this.args = value; }
    public String getProjectDirectory() { return projectDirectory; }
    public void setProjectDirectory(String value) { this.projectDirectory = value; }
    public String getAppHostFilePath() { return appHostFilePath; }
    public void setAppHostFilePath(String value) { this.appHostFilePath = value; }
    public String getContainerRegistryOverride() { return containerRegistryOverride; }
    public void setContainerRegistryOverride(String value) { this.containerRegistryOverride = value; }
    public boolean getDisableDashboard() { return disableDashboard; }
    public void setDisableDashboard(boolean value) { this.disableDashboard = value; }
    public String getDashboardApplicationName() { return dashboardApplicationName; }
    public void setDashboardApplicationName(String value) { this.dashboardApplicationName = value; }
    public boolean getAllowUnsecuredTransport() { return allowUnsecuredTransport; }
    public void setAllowUnsecuredTransport(boolean value) { this.allowUnsecuredTransport = value; }
    public boolean getEnableResourceLogging() { return enableResourceLogging; }
    public void setEnableResourceLogging(boolean value) { this.enableResourceLogging = value; }
    public Boolean getDefaultResourceWatchEnabled() { return defaultResourceWatchEnabled; }
    public void setDefaultResourceWatchEnabled(Boolean value) { this.defaultResourceWatchEnabled = value; }

    @SuppressWarnings("unchecked")
    public static CreateBuilderOptions fromMap(Map<String, Object> map) {
        var value = new CreateBuilderOptions();
        var argsValue = map.get("args");
        value.setArgs(argsValue == null ? null : (String[]) AspireClient.convertArray(argsValue, String[].class.getComponentType(), item0 -> (String) item0));
        var projectDirectoryValue = map.get("projectDirectory");
        value.setProjectDirectory(projectDirectoryValue == null ? null : (String) projectDirectoryValue);
        var appHostFilePathValue = map.get("appHostFilePath");
        value.setAppHostFilePath(appHostFilePathValue == null ? null : (String) appHostFilePathValue);
        var containerRegistryOverrideValue = map.get("containerRegistryOverride");
        value.setContainerRegistryOverride(containerRegistryOverrideValue == null ? null : (String) containerRegistryOverrideValue);
        var disableDashboardValue = map.get("disableDashboard");
        value.setDisableDashboard((Boolean) disableDashboardValue);
        var dashboardApplicationNameValue = map.get("dashboardApplicationName");
        value.setDashboardApplicationName(dashboardApplicationNameValue == null ? null : (String) dashboardApplicationNameValue);
        var allowUnsecuredTransportValue = map.get("allowUnsecuredTransport");
        value.setAllowUnsecuredTransport((Boolean) allowUnsecuredTransportValue);
        var enableResourceLoggingValue = map.get("enableResourceLogging");
        value.setEnableResourceLogging((Boolean) enableResourceLoggingValue);
        var defaultResourceWatchEnabledValue = map.get("defaultResourceWatchEnabled");
        value.setDefaultResourceWatchEnabled(defaultResourceWatchEnabledValue == null ? null : (Boolean) defaultResourceWatchEnabledValue);
        return value;
    }

    public Map<String, Object> toMap() {
        Map<String, Object> map = new HashMap<>();
        map.put("args", AspireClient.serializeValue(args));
        map.put("projectDirectory", AspireClient.serializeValue(projectDirectory));
        map.put("appHostFilePath", AspireClient.serializeValue(appHostFilePath));
        map.put("containerRegistryOverride", AspireClient.serializeValue(containerRegistryOverride));
        map.put("disableDashboard", AspireClient.serializeValue(disableDashboard));
        map.put("dashboardApplicationName", AspireClient.serializeValue(dashboardApplicationName));
        map.put("allowUnsecuredTransport", AspireClient.serializeValue(allowUnsecuredTransport));
        map.put("enableResourceLogging", AspireClient.serializeValue(enableResourceLogging));
        map.put("defaultResourceWatchEnabled", AspireClient.serializeValue(defaultResourceWatchEnabled));
        return map;
    }
}
