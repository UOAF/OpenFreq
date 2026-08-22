# Diagnostic telemetry

OpenFreq diagnostic telemetry is explicit opt-in and designed for reconstructing failures after multiplayer events. Declining does not affect radio functionality, and older servers remain compatible.

## User controls and data

On first launch after this feature is introduced, the client asks whether to allow diagnostics. The choice can be changed under **Settings → Diagnostics**. The same panel shows the local queue size and provides **Send now**, **Export diagnostics**, and **Delete queued data** actions. Deletion requires confirmation.

When allowed, records are written to a bounded local queue first and uploaded only after a consented authentication returns a short-lived capability. Upload is HTTPS-only, compressed, retried with backoff, and never performed on an audio callback. Revoking consent cancels an active attempt; unsent records remain available for export or deletion.

The approved schema includes:

- BMS in-game callsign, never a manually entered display or account/machine name.
- Exact simulated aircraft positions and derived terrain/altitude values.
- Radio state and frequency, IVC/mutex state, and aggregate capture health.
- Connection, transmission, propagation, heightmap, and ACMI health.
- Non-identifying platform/runtime versions and pseudonymous correlation IDs.
- For consented sessions, server signaling, UDP endpoint-mapping state without the address, and one-second relay summaries.

It never includes voice audio or recordings, passwords, IP addresses, raw device names or GUIDs, filesystem paths, machine/account names, or the unrestricted application log.

The client queue is capped at 50 MiB and seven days. The bundled UOAF ingestion deployment expires hosted raw batches after 30 days. A manual ZIP export remains on the user's computer and is shared only when the user chooses to share it. When a connected server provides an upload service, its destination hostname appears in the Diagnostics status.

## Server configuration

Telemetry is disabled when the endpoint or signing key is omitted. A UOAF-operated server enables it with these optional fields in `OpenFreq.Server.json`:

```json
{
  "telemetryEventId": "UOAF-2026-08-22",
  "telemetryEndpoint": "https://example.execute-api.eu-central-1.amazonaws.com/v1/batches",
  "telemetrySigningKey": "a-high-entropy-secret"
}
```

Use HTTPS; plaintext HTTP is accepted only for loopback development. Operators must disclose who controls the configured endpoint and its retention policy. Use at least 32 bytes of entropy for the signing key, treat it as a server secret, rotate it through the normal secret-management process, and use the same value in the ingestion deployment. The key is never sent to a client. The server issues a six-hour HMAC capability only to a client whose authentication request contains current consent.

The server writes its own bounded retry spool under `logs/telemetry`. Client-specific server records are produced only for consented sessions and use connection IDs rather than display names or network addresses.

## Hosted ingestion

The AWS SAM application in [`infra/telemetry`](../infra/telemetry/README.md) deploys API Gateway, a strict validation Lambda, and a private encrypted S3 bucket with a 30-day lifecycle. It enforces compressed and decompressed limits, exact envelope/nested-field allowlists, event-specific attribute allowlists, token expiry, edge throttling, and idempotent `batch_id` writes.

The infrastructure is not deployed automatically by this repository. The AWS account owner, data-access group, public privacy/contact URL, and event-ID operations process must be approved before a production pilot.

For the full rationale, schema, threat model, and staged rollout plan, see [`telemetry-design.md`](telemetry-design.md).
