# Aspire VS Code usefulness survey

## Status

The extension includes a **disabled-by-default** usefulness pilot. It will not prompt or collect survey data until the campaign is enabled after privacy and reporting review.

## Question and metric

When enabled, usage-telemetry-enabled users can receive one non-modal question: **"Does Aspire improve your development experience?"** The choices are **Yes**, **No**, and **Don't ask again**. Choosing Yes or No sends the answer to Microsoft through the extension's existing telemetry pipeline; there is no text box or follow-up survey. Closing the notification is not a No.

The metric is the percentage of Yes answers among Yes and No answers, not NSAT and not proof of a productivity increase. It represents responding, opted-in VS Code users, not all Aspire users.

## Eligibility and frequency

The first completed user-facing Aspire action qualifies, including failed or canceled attempts. A two-minute quiet delay restarts on further qualifying actions. There is no minimum age, active-day counting, sampling, campaign history, or recurring cooldown. Activation, discovery, refresh, settings, and copy commands do not qualify.

The prompt is one-time within the extension-host/profile storage scope. Every answer and dismissal retires it permanently, across extension updates and campaign changes. Suppression does not synchronize across devices, profiles, or remote hosts.

## Consent and data handling

No survey is shown when usage telemetry is disabled, or when VS Code's `telemetry.feedback.enabled` preference is false. Disabling either cancels pending survey work and prevents sending an answer from an already-open invitation. Survey events exclude employee alias/domain and AppHost details, but use VS Code's telemetry infrastructure and standard metadata; they are **not described as anonymous**. Local eligibility and suppression state does not contain your answer.

Answers use the same `sendTelemetryEvent` helper, VS Code telemetry logger, `@vscode/extension-telemetry` reporter, and configured `package.json` telemetry key as ordinary extension events. There is no survey-specific backend, key, or routing tag. The event names identify survey records within that existing pipeline; downstream dataset access and receipt still need verification before launch.

## Implementation and validation

`extension/src/services/UsefulnessSurveyService.ts` observes selected command completions and checks focus, consent, and tracked launch/deployment activity after the quiet delay. If the window is unfocused or busy, it skips that opportunity; a fresh qualifying action can schedule another. Focus changes alone do not schedule a prompt.

The service stores only `aspire.usefulnessSurvey.shown = true` in VS Code `globalState`, without Settings Sync. No activity history, timestamps, sampling decisions, or answers are stored. Consent changes cancel pending work and invalidate reporting from an open prompt, without clearing suppression.

Suppression is saved before display, preventing re-prompting after reload even if the host crashes or the notification is dismissed. A focus/consent change during that write can consume the opportunity without displaying it. Cross-window deduplication is best effort: `globalState` is not an atomic lock, so simultaneous windows can occasionally both invite. Corrupt state or persistence failures stop prompting and log a bounded warning rather than resetting suppression.

The events are `aspire/vscode/survey/invitation` and `aspire/vscode/survey/result`, with fixed `campaign_id` and `question_id`. Results add `outcome`: `yes`, `no`, `dismissed`, or `never_again`. Only the coarse `is_microsoft_internal` flag is included from Aspire's common-property bag; missing means unknown, not external. Standard platform metadata still requires review.

Unit coverage is in `usefulnessSurvey.test.ts` and the telemetry tests. It covers every response, consent changes, the quiet delay, and one-time suppression using an in-memory VS Code state adapter. Two E2E smoke cases in `extension/src/test-e2e/usefulnessSurvey.e2e.test.ts` exercise Yes and Don't ask again, including suppression after reload. They share the existing Linux and Windows `cli-path-rejection` notification jobs and use an isolated state key and a local-only event sink; there are no dedicated survey jobs.

## Launch and reporting

Before enabling the campaign, confirm classification, retention, dataset access, backend receipt, and a reporting owner. Set a finite expiry. There is no remote kill switch; rollback requires an extension update.

Calculate `100 * yes / (yes + no)` from approved, deduplicated results for a specific campaign, question version, cohort, and period. Zero answers means no data, not 0%. Show the answer count and uncertainty alongside the rate; keep dismissals and permanent suppressions separate. Exclude test traffic and separate internal, external, and unknown cohorts. Invitation events represent dispatch, not confirmed visibility, and delivery is best effort.
