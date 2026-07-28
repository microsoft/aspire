import type { Key, ReactNode } from "react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
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
    <Alert className={`notif notif--${intent}`} variant={toAlertVariant(intent)}>
      <span className="notif__icon" aria-hidden="true">
        <IntentIcon intent={intent} />
      </span>
      <div className="notif__body">
        {notification.title ? <AlertTitle className="notif__title">{notification.title}</AlertTitle> : null}
        {notification.message ? <AlertDescription className="notif__message">{notification.message}</AlertDescription> : null}

        {notification.link ? (
          <Button className="notif__link" variant="ghost" size="small" onClick={notification.link.onClick}>
            {notification.link.label}
            <ExternalIcon size={13} />
          </Button>
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
    </Alert>
  );
}

function toAlertVariant(intent: NotificationIntent): "default" | "destructive" | "warning" | "success" {
  switch (intent) {
    case "error":
      return "destructive";
    case "warning":
      return "warning";
    case "success":
      return "success";
    default:
      return "default";
  }
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
