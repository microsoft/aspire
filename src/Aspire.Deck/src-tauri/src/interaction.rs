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

/// Emits the active session's open interactions (an empty list clears the UI when
/// there are none). Used on session switch and on every interaction change.
pub fn emit_active_interaction(app: &AppHandle, session: &Session) {
    let _ = app.emit("deck://interactions", &current_interaction_events(session));
}

/// Snapshots the session's open interactions as UI events, in arrival order.
fn current_interaction_events(session: &Session) -> Vec<InteractionEvent> {
    session
        .interactions
        .lock()
        .unwrap()
        .values()
        .map(InteractionEvent::from_response)
        .collect()
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
                    // A `complete` (or empty) update removes the interaction; any other
                    // kind adds or replaces it (the server re-sends the same id with
                    // validation errors during live inputs-dialog validation).
                    let is_complete = matches!(
                        &update.kind,
                        Some(pb::watch_interactions_response_update::Kind::Complete(_)) | None
                    );
                    let id = update.interaction_id;
                    {
                        let mut interactions = session.interactions.lock().unwrap();
                        if is_complete {
                            interactions.shift_remove(&id);
                        } else {
                            interactions.insert(id, update);
                        }
                    }

                    if state.is_active(&session.id) {
                        let _ = app.emit("deck://interactions", &current_interaction_events(&session));
                    }
                }
                Ok(None) | Err(_) => break,
            }
        }

        *session.interaction_tx.lock().unwrap() = None;
        session.interactions.lock().unwrap().clear();
        if state.is_active(&session.id) {
            let _ = app.emit("deck://interactions", &Vec::<InteractionEvent>::new());
        }
        tokio::time::sleep(RECONNECT_DELAY).await;
    }
}

/// Replies to a specific interaction on the given session.
///
/// `action`:
/// - `"submit"`  — send the inputs and complete the dialog.
/// - `"update"`  — send the inputs for live re-validation (does not complete).
/// - `"primary"` / `"secondary"` — message-box or notification button results.
/// - anything else (`"cancel"` / `"dismiss"`) — complete/dismiss the interaction.
pub fn respond(session: &Session, interaction_id: i32, action: &str, values: HashMap<String, String>) {
    let pending = match session.interactions.lock().unwrap().get(&interaction_id).cloned() {
        Some(pending) => pending,
        None => return,
    };

    let request = build_request(&pending, action, values);

    // Optimistically remove for terminal actions so a stale reply isn't reused.
    if action != "update" {
        session.interactions.lock().unwrap().shift_remove(&interaction_id);
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
        "primary" | "secondary" => {
            // The button result; the AppHost re-runs/advances based on true vs false
            // (e.g. "Enter values" on the unresolved-parameters notification = true).
            let result = action == "primary";
            match &pending.kind {
                // Reply in the same kind the AppHost sent so it routes the result
                // correctly. Notifications echo their intent/link back with the result,
                // matching the dashboard's behavior.
                Some(pb::watch_interactions_response_update::Kind::Notification(n)) => {
                    request.kind = Some(Kind::Notification(pb::InteractionNotification {
                        intent: n.intent,
                        result: Some(result),
                        link_text: n.link_text.clone(),
                        link_url: n.link_url.clone(),
                    }));
                }
                _ => {
                    request.kind = Some(Kind::MessageBox(pb::InteractionMessageBox { intent: 0, result: Some(result) }));
                }
            }
        }
        _ => {
            request.kind = Some(Kind::Complete(pb::InteractionComplete {}));
        }
    }

    request
}
