import type { InteractionInfo } from "../api/types";
import { respondInteraction, openExternal } from "../api/deck";
import { CloseIcon, ExternalIcon } from "../toolkit";

// Non-blocking notification toasts for notification-kind interactions (errors, the
// "parameters required" prompt, status messages). Each toast is backed by a live
// server interaction, so it stays until the user acts on it or dismisses it — there
// is no auto-dismiss. This mirrors the dashboard, which routes notifications to
// stacked message bars while keeping dialogs (inputs/message box) modal.
export function NotificationStack({ notifications }: { notifications: InteractionInfo[] }) {
  if (notifications.length === 0) {
    return null;
  }

  return (
    <div className="notif-stack" role="region" aria-label="Notifications">
      {notifications.map((n) => (
        <NotificationToast key={n.interactionId} notification={n} />
      ))}
    </div>
  );
}

function NotificationToast({ notification }: { notification: InteractionInfo }) {
  const intent = toIntentClass(notification.intent);
  const dismiss = () => respondInteraction(notification.interactionId, "cancel", {});

  return (
    <div className={`notif notif--${intent}`} role="alert">
      <span className="notif__icon" aria-hidden="true">
        <IntentGlyph intent={intent} />
      </span>
      <div className="notif__body">
        {notification.title ? <div className="notif__title">{notification.title}</div> : null}
        {notification.message ? <div className="notif__message">{notification.message}</div> : null}

        {notification.linkUrl ? (
          <button className="notif__link" type="button" onClick={() => void openExternal(notification.linkUrl)}>
            {notification.linkText || notification.linkUrl}
            <ExternalIcon size={13} />
          </button>
        ) : null}

        {notification.primaryButtonText || notification.showSecondaryButton ? (
          <div className="notif__actions">
            {notification.showSecondaryButton ? (
              <button
                className="btn btn--sm"
                type="button"
                onClick={() => respondInteraction(notification.interactionId, "secondary", {})}
              >
                {notification.secondaryButtonText || "No"}
              </button>
            ) : null}
            {notification.primaryButtonText ? (
              <button
                className="btn btn--sm btn--primary"
                type="button"
                onClick={() => respondInteraction(notification.interactionId, "primary", {})}
              >
                {notification.primaryButtonText}
              </button>
            ) : null}
          </div>
        ) : null}
      </div>

      {notification.showDismiss !== false ? (
        <button className="icon-btn notif__dismiss" type="button" onClick={dismiss} aria-label="Dismiss notification">
          <CloseIcon size={15} />
        </button>
      ) : null}
    </div>
  );
}

// Aspire intents map onto the four semantic colors. "confirmation" and "none" fall
// back to the neutral information style.
function toIntentClass(intent: InteractionInfo["intent"]): "error" | "warning" | "success" | "info" {
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

function IntentGlyph({ intent }: { intent: "error" | "warning" | "success" | "info" }) {
  const common = { width: 16, height: 16, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 2, strokeLinecap: "round" as const, strokeLinejoin: "round" as const };
  switch (intent) {
    case "success":
      return (
        <svg {...common}>
          <path d="M20 6 9 17l-5-5" />
        </svg>
      );
    case "warning":
      return (
        <svg {...common}>
          <path d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z" />
          <path d="M12 9v4" />
          <path d="M12 17h.01" />
        </svg>
      );
    case "error":
      return (
        <svg {...common}>
          <circle cx="12" cy="12" r="10" />
          <path d="m15 9-6 6" />
          <path d="m9 9 6 6" />
        </svg>
      );
    default:
      return (
        <svg {...common}>
          <circle cx="12" cy="12" r="10" />
          <path d="M12 16v-4" />
          <path d="M12 8h.01" />
        </svg>
      );
  }
}
