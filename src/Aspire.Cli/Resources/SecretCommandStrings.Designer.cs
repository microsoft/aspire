// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Resources;

internal static class SecretCommandStrings
{
    private static readonly System.Resources.ResourceManager s_resourceManager = new("Aspire.Cli.Resources.SecretCommandStrings", typeof(SecretCommandStrings).Assembly);

    internal static string Description => s_resourceManager.GetString("Description", System.Globalization.CultureInfo.CurrentUICulture) ?? "Manage apphost user secrets";
    internal static string SetDescription => s_resourceManager.GetString("SetDescription", System.Globalization.CultureInfo.CurrentUICulture) ?? "Set a secret value";
    internal static string GetDescription => s_resourceManager.GetString("GetDescription", System.Globalization.CultureInfo.CurrentUICulture) ?? "Get a secret value";
    internal static string ListDescription => s_resourceManager.GetString("ListDescription", System.Globalization.CultureInfo.CurrentUICulture) ?? "List all secrets";
    internal static string PathDescription => s_resourceManager.GetString("PathDescription", System.Globalization.CultureInfo.CurrentUICulture) ?? "Show the secrets file path.";
    internal static string DeleteDescription => s_resourceManager.GetString("DeleteDescription", System.Globalization.CultureInfo.CurrentUICulture) ?? "Delete a secret";
    internal static string KeyArgumentDescription => s_resourceManager.GetString("KeyArgumentDescription", System.Globalization.CultureInfo.CurrentUICulture) ?? "The secret key";
    internal static string KeyRetrieveArgumentDescription => s_resourceManager.GetString("KeyRetrieveArgumentDescription", System.Globalization.CultureInfo.CurrentUICulture) ?? "The secret key to retrieve";
    internal static string KeyDeleteArgumentDescription => s_resourceManager.GetString("KeyDeleteArgumentDescription", System.Globalization.CultureInfo.CurrentUICulture) ?? "The secret key to delete";
    internal static string ValueArgumentDescription => s_resourceManager.GetString("ValueArgumentDescription", System.Globalization.CultureInfo.CurrentUICulture) ?? "The secret value to set";
    internal static string FormatOptionDescription => s_resourceManager.GetString("FormatOptionDescription", System.Globalization.CultureInfo.CurrentUICulture) ?? "Output format";
    internal static string CouldNotFindAppHost => s_resourceManager.GetString("CouldNotFindAppHost", System.Globalization.CultureInfo.CurrentUICulture) ?? "Could not find an apphost project.";
    internal static string SecretNotFound => s_resourceManager.GetString("SecretNotFound", System.Globalization.CultureInfo.CurrentUICulture) ?? "Secret '{0}' not found.";
    internal static string SecretSetSuccess => s_resourceManager.GetString("SecretSetSuccess", System.Globalization.CultureInfo.CurrentUICulture) ?? "Secret '{0}' set successfully.";
    internal static string SecretDeleteSuccess => s_resourceManager.GetString("SecretDeleteSuccess", System.Globalization.CultureInfo.CurrentUICulture) ?? "Secret '{0}' deleted successfully.";
    internal static string NoSecretsConfigured => s_resourceManager.GetString("NoSecretsConfigured", System.Globalization.CultureInfo.CurrentUICulture) ?? "No secrets configured.";
    internal static string KeyColumnHeader => s_resourceManager.GetString("KeyColumnHeader", System.Globalization.CultureInfo.CurrentUICulture) ?? "Key";
    internal static string ValueColumnHeader => s_resourceManager.GetString("ValueColumnHeader", System.Globalization.CultureInfo.CurrentUICulture) ?? "Value";
}
