//! Client for the AppHost interaction stream (`WatchInteractions`).
//!
//! Interactions are how the AppHost prompts the dashboard: a command that needs
//! parameters, a confirmation message box, or a notification. The protocol is a
//! bidirectional gRPC stream — the AppHost pushes `WatchInteractionsResponseUpdate`
//! messages, and the client replies with `WatchInteractionsRequestUpdate` (the
//! filled inputs, a button result, or a "complete" to dismiss). Input dialogs are
//! re-validated live by replying with `response_update = true`, which makes the
//! AppHost send back the same dialog with `validation_errors` populated.
//!
//! Deck opens one interaction stream per attached AppHost (Session) and surfaces
//! the current interaction to the UI via `deck://interaction` while the session is
//! active. The UI replies through the `deck_respond_interaction` command.

use std::collections::HashMap;
use std::sync::Arc;
use std::time::Duration;

use tauri::{AppHandle, Emitter};
use tokio::sync::mpsc;
use tokio_stream::wrappers::ReceiverStream;

use crate::model::InteractionEvent;
use crate::proto::aspire::v1 as pb;
use crate::proto::aspire::v1::dashboard_service_client::DashboardServiceClient;
use crate::proto::aspire::v1::WatchInteractionsRequestUpdate;
use crate::resource_client::{build_interceptor, connect_channel};
use crate::state::{AppState, Session};

const RECONNECT_DELAY: Duration = Duration::from_secs(3);

/// Emits the active session's current interaction (or a synthetic "complete" to
/// clear the UI when there is none). Used on session switch.
pub fn emit_active_interaction(app: &AppHandle, session: &Session) {
    let event = match session.pending_interaction.lock().unwrap().as_ref() {
        Some(update) => InteractionEvent::from_response(update),
        None => InteractionEvent::complete(),
    };
    let _ = app.emit("deck://interaction", &event);
}

/// Long-running loop that maintains the interaction stream for one session and
/// pushes prompts to the UI while the session is active. Reconnects on drop.
pub async fn run_interaction_loop(app: AppHandle, state: Arc<AppState>, session: Arc<Session>) {
    let url = session.resource_service_url.clone();

    loop {
        let channel = match connect_channel(&url).await {
            Ok(channel) => channel,
            Err(_) => {
                tokio::time::sleep(RECONNECT_DELAY).await;
                continue;
            }
        };

        let mut client =
            DashboardServiceClient::with_interceptor(channel, build_interceptor(&session));

        // The outbound (client -> server) half of the bidi stream is driven by a
        // channel; the session keeps the sender so command replies can be written.
        let (tx, rx) = mpsc::channel::<WatchInteractionsRequestUpdate>(16);
        *session.interaction_tx.lock().unwrap() = Some(tx);

        let response = match client.watch_interactions(ReceiverStream::new(rx)).await {
            Ok(response) => response,
            Err(_) => {
                *session.interaction_tx.lock().unwrap() = None;
                tokio::time::sleep(RECONNECT_DELAY).await;
                continue;
            }
        };

        let mut inbound = response.into_inner();
        loop {
            match inbound.message().await {
                Ok(Some(update)) => {
                    let is_complete = matches!(
                        update.kind,
                        Some(pb::watch_interactions_response_update::Kind::Complete(_)) | None
                    );

                    if is_complete {
                        // Clear the pending interaction if this completes it.
                        let mut pending = session.pending_interaction.lock().unwrap();
                        if pending.as_ref().map(|p| p.interaction_id) == Some(update.interaction_id) {
                            *pending = None;
                        }
                    } else {
                        *session.pending_interaction.lock().unwrap() = Some(update.clone());
                    }

                    if state.is_active(&session.id) {
                        let _ = app.emit("deck://interaction", &InteractionEvent::from_response(&update));
                    }
                }
                Ok(None) | Err(_) => break,
            }
        }

        *session.interaction_tx.lock().unwrap() = None;
        *session.pending_interaction.lock().unwrap() = None;
        if state.is_active(&session.id) {
            let _ = app.emit("deck://interaction", &InteractionEvent::complete());
        }
        tokio::time::sleep(RECONNECT_DELAY).await;
    }
}

/// Replies to the current interaction on the given session.
///
/// `action`:
/// - `"submit"`  — send the inputs and complete the dialog.
/// - `"update"`  — send the inputs for live re-validation (does not complete).
/// - `"primary"` / `"secondary"` — message-box button results.
/// - anything else (`"cancel"` / `"dismiss"`) — complete/dismiss the interaction.
pub fn respond(session: &Session, action: &str, values: HashMap<String, String>) {
    let pending = match session.pending_interaction.lock().unwrap().clone() {
        Some(pending) => pending,
        None => return,
    };

    let request = build_request(&pending, action, values);

    // Optimistically clear pending for terminal actions so a stale reply isn't reused.
    if !matches!(action, "update") {
        let mut current = session.pending_interaction.lock().unwrap();
        if current.as_ref().map(|p| p.interaction_id) == Some(pending.interaction_id) {
            *current = None;
        }
    }

    if let Some(tx) = session.interaction_tx.lock().unwrap().clone() {
        let _ = tx.try_send(request);
    }
}

fn build_request(
    pending: &pb::WatchInteractionsResponseUpdate,
    action: &str,
    values: HashMap<String, String>,
) -> WatchInteractionsRequestUpdate {
    use pb::watch_interactions_request_update::Kind;

    let mut request = WatchInteractionsRequestUpdate {
        interaction_id: pending.interaction_id,
        kind: None,
        response_update: None,
    };

    match action {
        "submit" | "update" => {
            if let Some(pb::watch_interactions_response_update::Kind::InputsDialog(dialog)) = &pending.kind {
                // Rebuild the dialog's inputs from the server's last copy, applying the
                // user's values and clearing prior validation errors (the server re-adds them).
                let mut items = dialog.input_items.clone();
                for item in items.iter_mut() {
                    if let Some(value) = values.get(&item.name) {
                        item.value = value.clone();
                    }
                    item.validation_errors.clear();
                }
                request.kind = Some(Kind::InputsDialog(pb::InteractionInputsDialog { input_items: items }));
                request.response_update = Some(action == "update");
            } else {
                request.kind = Some(Kind::Complete(pb::InteractionComplete {}));
            }
        }
        "primary" => {
            request.kind = Some(Kind::MessageBox(pb::InteractionMessageBox { intent: 0, result: Some(true) }));
        }
        "secondary" => {
            request.kind = Some(Kind::MessageBox(pb::InteractionMessageBox { intent: 0, result: Some(false) }));
        }
        _ => {
            request.kind = Some(Kind::Complete(pb::InteractionComplete {}));
        }
    }

    request
}
