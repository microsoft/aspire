import type { InteractionInfo } from "../api/types";
import { formatTimeWithMillis } from "../lib/format";
import { Button, Dialog, MarkdownContent } from "../toolkit";

export interface NotificationHistoryItem {
  interactionId: number;
  title: string;
  message: string;
  intent: InteractionInfo["intent"];
  enableMessageMarkdown: boolean;
  receivedAt: string;
}

export function NotificationCenter({
  open,
  notifications,
  onClear,
  onClose,
}: {
  open: boolean;
  notifications: NotificationHistoryItem[];
  onClear: () => void;
  onClose: () => void;
}) {
  return (
    <Dialog
      open={open}
      title="Notification center"
      onClose={onClose}
      className="shell-dialog notification-center"
      actions={(
        <>
          <Button onClick={onClear} disabled={notifications.length === 0}>Clear history</Button>
          <Button variant="primary" onClick={onClose}>Close</Button>
        </>
      )}
    >
      {notifications.length === 0 ? (
        <p className="notification-center__empty">No notifications.</p>
      ) : (
        <ol className="notification-center__list">
          {[...notifications].reverse().map((notification) => (
            <li key={`${notification.interactionId}-${notification.receivedAt}`} className={`notification-center__item notification-center__item--${notification.intent}`}>
              <div className="notification-center__heading">
                <strong>{notification.title || "Notification"}</strong>
                <time dateTime={notification.receivedAt}>{formatTimeWithMillis(notification.receivedAt)}</time>
              </div>
              <MarkdownContent markdown={notification.message} enabled={notification.enableMessageMarkdown} />
            </li>
          ))}
        </ol>
      )}
    </Dialog>
  );
}
