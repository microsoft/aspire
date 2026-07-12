import { useState } from "react";
import type { DeckConfig } from "../api/types";
import { openExternal } from "../api/deck";
import { NotificationStack, type NotificationItem } from "../toolkit";

const UNSECURED_ENDPOINT_DISMISSED_KEY = "Aspire_Security_UnsecuredEndpointMessageDismissed";

export function SystemNotifications({ config }: { config: DeckConfig | null }) {
  const [unsecuredDismissed, setUnsecuredDismissed] = useState(
    () => window.localStorage.getItem(UNSECURED_ENDPOINT_DISMISSED_KEY) === "true",
  );
  const messages: string[] = [];

  if (config?.isTelemetryEndpointUnsecured) {
    messages.push("Untrusted apps can send telemetry to the dashboard.");
  }
  if (config?.isApiEndpointUnsecured) {
    messages.push("Untrusted apps can access telemetry data via the API.");
  }

  const notifications: NotificationItem[] = messages.length > 0 && !unsecuredDismissed
    ? [{
        id: "unsecured-endpoints",
        intent: "warning",
        title: "Endpoint is unsecured",
        message: messages.join(" "),
        link: {
          label: "More information",
          onClick: () => void openExternal("https://aka.ms/aspire/api-endpoint-unsecured"),
        },
        onDismiss: () => {
          window.localStorage.setItem(UNSECURED_ENDPOINT_DISMISSED_KEY, "true");
          setUnsecuredDismissed(true);
        },
      }]
    : [];

  return <NotificationStack notifications={notifications} ariaLabel="System notifications" />;
}
