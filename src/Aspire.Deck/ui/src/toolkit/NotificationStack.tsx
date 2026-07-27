import type { Key, ReactNode } from "react";
import { Button, IconButton } from "./Button";
import {
  CloseIcon,
  ErrorIcon,
  ExternalIcon,
  InfoIcon,
  SuccessIcon,
  WarningIcon,
} from "./Icons";

export type NotificationIntent = "error" | "warning" | "success" | "info";

export interface NotificationAction {
  label: string;
  onClick: () => void;
}

export interface NotificationItem {
  id: Key;
  intent?: NotificationIntent;
  title?: ReactNode;
  message?: ReactNode;
  link?: NotificationAction;
  primaryAction?: NotificationAction;
  secondaryAction?: NotificationAction;
  onDismiss?: () => void;
  dismissLabel?: string;
}

export function NotificationStack({
  notifications,
  ariaLabel = "Notifications",
}: {
  notifications: NotificationItem[];
  ariaLabel?: string;
}) {
  if (notifications.length === 0) {
    return null;
  }

  return (
    <div className="notif-stack" role="region" aria-label={ariaLabel}>
      {notifications.map((notification) => (
        <Notification key={notification.id} notification={notification} />
      ))}
    </div>
  );
}

export function Notification({ notification }: { notification: NotificationItem }) {
  const intent = notification.intent ?? "info";
  return (
    <div className={`notif notif--${intent}`} role="alert">
      <span className="notif__icon" aria-hidden="true">
        <IntentIcon intent={intent} />
      </span>
      <div className="notif__body">
        {notification.title ? <div className="notif__title">{notification.title}</div> : null}
        {notification.message ? <div className="notif__message">{notification.message}</div> : null}

        {notification.link ? (
          <button className="notif__link" type="button" onClick={notification.link.onClick}>
            {notification.link.label}
            <ExternalIcon size={13} />
          </button>
        ) : null}

        {notification.primaryAction || notification.secondaryAction ? (
          <div className="notif__actions">
            {notification.secondaryAction ? (
              <Button size="small" onClick={notification.secondaryAction.onClick}>
                {notification.secondaryAction.label}
              </Button>
            ) : null}
            {notification.primaryAction ? (
              <Button size="small" variant="primary" onClick={notification.primaryAction.onClick}>
                {notification.primaryAction.label}
              </Button>
            ) : null}
          </div>
        ) : null}
      </div>

      {notification.onDismiss ? (
        <IconButton
          className="notif__dismiss"
          label={notification.dismissLabel ?? "Dismiss notification"}
          icon={<CloseIcon size={15} />}
          onClick={notification.onDismiss}
        />
      ) : null}
    </div>
  );
}

function IntentIcon({ intent }: { intent: NotificationIntent }) {
  switch (intent) {
    case "success":
      return <SuccessIcon size={16} />;
    case "warning":
      return <WarningIcon size={16} />;
    case "error":
      return <ErrorIcon size={16} />;
    default:
      return <InfoIcon size={16} />;
  }
}
