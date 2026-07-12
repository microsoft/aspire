import type { InteractionInfo } from "../api/types";
import { respondInteraction, openExternal } from "../api/deck";
import {
  NotificationStack as DeckNotificationStack,
  type NotificationIntent,
  type NotificationItem,
} from "../toolkit";

// Non-blocking notification toasts for notification-kind interactions (errors, the
// "parameters required" prompt, status messages). Each toast is backed by a live
// server interaction, so it stays until the user acts on it or dismisses it — there
// is no auto-dismiss. This mirrors the dashboard, which routes notifications to
// stacked message bars while keeping dialogs (inputs/message box) modal.
export function NotificationStack({
  notifications,
  onPrimaryAction,
}: {
  notifications: InteractionInfo[];
  onPrimaryAction?: (notification: InteractionInfo) => void;
}) {
  const items = notifications.map<NotificationItem>((notification) => ({
    id: notification.interactionId,
    intent: toIntent(notification.intent),
    title: notification.title,
    message: notification.message,
    link: notification.linkUrl
      ? {
          label: notification.linkText || notification.linkUrl,
          onClick: () => void openExternal(notification.linkUrl),
        }
      : undefined,
    primaryAction: notification.primaryButtonText
      ? {
          label: notification.primaryButtonText,
          onClick: () => {
            respondInteraction(notification.interactionId, "primary", {});
            onPrimaryAction?.(notification);
          },
        }
      : undefined,
    secondaryAction: notification.showSecondaryButton
      ? {
          label: notification.secondaryButtonText || "No",
          onClick: () => respondInteraction(notification.interactionId, "secondary", {}),
        }
      : undefined,
    onDismiss:
      notification.showDismiss !== false
        ? () => respondInteraction(notification.interactionId, "cancel", {})
        : undefined,
  }));

  return <DeckNotificationStack notifications={items} />;
}

// Aspire intents map onto the four semantic colors. "confirmation" and "none" fall
// back to the neutral information style.
function toIntent(intent: InteractionInfo["intent"]): NotificationIntent {
  switch (intent) {
    case "error":
      return "error";
    case "warning":
      return "warning";
    case "success":
      return "success";
    default:
      return "info";
  }
}
