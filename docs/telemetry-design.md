# OpenFreq diagnostic telemetry design

Status: Implemented prototype for review
Target: opt-in UOAF pilot after operator approvals
Initial deployment: UOAF-operated multiplayer servers

## Summary

OpenFreq needs post-event diagnostic telemetry that can reconstruct failures spanning clients, the OpenFreq server, BMS shared memory, Tacview/ACMI, audio devices, RTP transport, and terrain propagation.

The proposed system is explicitly opt-in and offline-first. A client writes allowlisted structured events to a bounded local spool, then uploads compressed batches without blocking startup, connection, audio processing, or shutdown. Previously queued batches are retried on the next launch. Users can also export a diagnostic bundle manually.

The initial hosted pipeline is:

```text
OpenFreq client/server
        |
        v
HTTPS ingestion API
        |
        v
schema validation and server-side redaction
        |
        v
partitioned object storage
        |
        +--> session index
        +--> post-event queries and dashboards
```

For an AWS deployment, the intended components are API Gateway, Lambda, S3, Athena/Glue, and optionally DynamoDB for a session/report index. Clients must never receive credentials that permit direct database or object-storage access.

## Goals

- Reconstruct an event timeline across the server and participating clients.
- Diagnose silent or asymmetric audio failures after the event has ended.
- Diagnose BMS radio/shared-memory state, IVC conflicts, device changes, Tacview state, volume mapping, and terrain/heightmap problems.
- Preserve diagnostic evidence through application crashes and temporary network loss.
- Make collection understandable, voluntary, bounded, and reversible.
- Prevent telemetry transport from affecting the real-time audio path.
- Keep the client event format backend-neutral and versioned.

## Non-goals

- Recording or analyzing voice audio.
- General user analytics, engagement tracking, advertising, or profiling.
- Remotely controlling or reconfiguring clients.
- Automatically uploading existing text logs, configuration files, or recordings.
- Replacing local application logs.
- Guaranteeing delivery of every low-value diagnostic event.

## Terminology and correlation

The following identifiers have different lifetimes and must not be conflated:

| Field | Created by | Lifetime | Purpose |
| --- | --- | --- | --- |
| `installation_id` | Client | Until local telemetry data is reset | Correlate recurring failures on one installation without using machine identity |
| `app_session_id` | Client | One application launch | Correlate startup, shutdown, and crash events |
| `server_run_id` | OpenFreq server | One server process lifetime | Join all participating clients to the same server timeline |
| `connection_id` | OpenFreq server | One authenticated client connection | Join client and server events for a connection; the existing ephemeral peer ID can fulfill this role |
| `event_id` | Server operator | One organized multiplayer event, optional | Correlate across planned server restarts or multiple OpenFreq server instances |
| `batch_id` | Telemetry client | One upload batch | Idempotent ingestion and retry deduplication |
| `event_record_id` | Event producer | One record | Event-level deduplication and support investigation references |

All IDs are UUIDs unless an operator-defined `event_id` uses a constrained string format. The server returns `server_run_id`, `connection_id`, and optional `event_id` in the successful authentication response. Older servers may omit these fields; clients must remain compatible and upload uncorrelated client telemetry when consented.

`connection_id` is ephemeral and must not be used as a permanent user identity. `installation_id` is random; it must not be derived from a username, hostname, hardware identifier, audio-device GUID, IP address, or callsign.

## Consent and user controls

### First-run decision

No structured diagnostic telemetry is collected or uploaded before an affirmative choice. Existing local application logging remains unchanged. After configuration has loaded and the main window is usable, a first-run dialog displays:

> Help improve OpenFreq by sending diagnostic telemetry after sessions. Diagnostic telemetry includes your in-game callsign, simulated aircraft position, aircraft type, radio state, connection quality, terrain/signal calculations, device health, and application errors. It does not include voice audio, passwords, your real name, or your computer account information.

Actions:

- **Allow diagnostics**: set consent to `granted` and begin the structured telemetry session.
- **Not now**: set consent to `declined`; do not reprompt for the same policy version.
- **View details**: open a complete collection and retention description without changing consent.

Consent state is stored as:

```json
{
  "status": "unknown | granted | declined",
  "policyVersion": 1,
  "recordedAtUtc": "2026-08-22T12:34:56.789Z"
}
```

A new prompt is permitted only if the policy version changes because collected data categories, purpose, recipients, or retention materially change. Application upgrades alone do not trigger another prompt.

### Settings

The settings UI provides:

- An **Allow diagnostic telemetry** toggle.
- A link to **What is collected?**
- **Send queued diagnostics now**.
- **Export diagnostic bundle**.
- **Delete queued diagnostics**.
- The current queue size, oldest queued event date, and last upload result.

Disabling telemetry immediately stops new structured telemetry collection and cancels uploads. The UI asks whether already queued telemetry should also be deleted. Disabling telemetry does not delete ordinary local application logs.

### Manual diagnostic export

Manual export is available regardless of telemetry consent. Before writing the bundle, the UI lists its categories and destination. A bundle contains:

- A manifest with the application version and export time.
- Selected structured telemetry records, if present.
- A redacted configuration summary.
- Optional local log files only when the user explicitly selects them.

The bundle never contains recordings or passwords. The user decides how and where to share the exported file.

## Data policy

Telemetry uses an explicit allowlist. Adding a field requires updating this document, the consent details when applicable, the schema, and redaction tests. Arbitrary `ILogger` properties, exception objects, configuration objects, and files must not be passed through automatically.

### Collected

| Category | Examples | Reason |
| --- | --- | --- |
| Build/runtime | OpenFreq version, schema version, OS family/version, architecture, runtime version, portable/installed distribution | Find version- and platform-specific faults |
| Session | Random correlation IDs, start/end time, clean/unclean end, connection state and reason | Reconstruct timelines |
| In-game identity | BMS-provided callsign | Identify a participant consistently within an event |
| Aircraft | Aircraft type/preset and 3D/lobby state | Diagnose radio mapping and propagation behavior |
| Simulated position | Exact BMS coordinates, calculated latitude/longitude when available, MSL altitude, AGL altitude | Reproduce propagation and terrain failures |
| Terrain | Theater ID, heightmap fingerprint and dimensions, sampled elevation, coordinate transform result | Detect stale/wrong maps and underground radios |
| Radio state | Radio type, frequency, power, PTT, squelch, raw BMS volume, mapped gain, OpenFreq channel/slot mapping | Diagnose reversed radios and volume faults |
| Audio health | Backend/error codes, enabled device count, selected/default relationship, capture/playback state, callback and sample counters, RMS/peak/silence measurements | Diagnose device corruption and silent capture/playback |
| Network health | Signaling states, reconnects, bytes/packets sent and received, loss, jitter, late/duplicate packets, FEC/PLC, queue depth, relay outcome | Diagnose asymmetric communication |
| Tacview/ACMI | Connection states, frame age, parser result, discovered/eligible/tracked object counts | Diagnose connected-with-no-aircraft failures |
| IVC/shared memory | IVC process state, mutex state, acquisition attempts/duration, RCC/RCS state and transitions | Diagnose startup conflicts |
| Performance | Operation durations, bounded queue sizes, dropped telemetry count, non-audio CPU/memory health at low frequency | Detect resource exhaustion and telemetry impact |
| Errors | Stable error code, component, operation, sanitized exception type and stack frames from OpenFreq assemblies | Locate failures without uploading arbitrary exception text |

### Not collected automatically

- Voice audio, decoded PCM, encoded audio payloads, or recordings.
- OpenFreq, Tacview, or other passwords and authentication material.
- The user's real name.
- Windows/macOS/Linux account name, computer name, domain, or email address.
- IP addresses in the central telemetry dataset.
- Discord or other chat content.
- Unrelated files, directory listings, screenshots, or environment variables.
- Full filesystem paths.
- Raw configuration files or ordinary text logs.
- Raw audio-device, joystick, or hardware GUIDs.

### Callsign rules

- In BMS mode, `callsign` comes only from BMS radio/shared-memory data.
- A manually entered GCI display name is not automatically treated as a callsign because it may contain a real name.
- If GCI correlation requires a callsign, the UI must expose a field explicitly labeled as a public in-game callsign and explain that it is sent with telemetry.
- Callsigns are stored in clear text for event reconstruction and are subject to the same retention period as the rest of the event telemetry.

### Device rules

Raw device names can contain personal text and must not be uploaded automatically. Device events contain:

- Audio backend and sanitized driver/API type.
- Enabled device count.
- Selected index and default index.
- Whether the selected device is the default.
- Whether a saved device was found or fallback occurred.
- BASS error/result codes.
- A locally salted stable hash when it is necessary to determine whether the selected device changed during an event.

The salt is installation-local and regenerated when telemetry data is reset. Hardware GUIDs and raw names remain available only in an explicitly selected manual log export.

### Position and terrain rules

Positions represent simulated aircraft in a video game and are intentionally retained at diagnostic precision. A position sample can include:

```text
theater_id
heightmap_fingerprint
bms_x
bms_y
latitude_deg
longitude_deg
altitude_msl_ft
terrain_elevation_ft
altitude_agl_ft
aircraft_type
```

Propagation events can additionally include transmitter and receiver positions, path distance, line-of-sight/obstruction result, radio horizon, computed path loss, signal strength, and the final audio parameters applied by the receiver.

Position sampling must not occur per audio packet. It occurs:

- At transmission start and stop.
- At most once per second while actively transmitting or receiving a relevant transmission.
- When theater, heightmap, tracked aircraft, aircraft type, or flight state changes.
- When a terrain or propagation calculation is anomalous.
- In a low-frequency health snapshot while connected, initially every ten seconds.

## Event envelope

Every record follows a common envelope. Event-specific data lives under `attributes` and is validated against the registered event type.

```json
{
  "schema_version": 1,
  "event_record_id": "0191...",
  "event_name": "radio.transmission.health",
  "timestamp_utc": "2026-08-22T19:14:32.125Z",
  "severity": "info",
  "source": "client",
  "service_version": "1.1.0+abc123",
  "platform": {
    "os_family": "windows",
    "os_version": "...",
    "architecture": "x64",
    "runtime_version": ".NET 10.0"
  },
  "correlation": {
    "installation_id": "...",
    "app_session_id": "...",
    "server_run_id": "...",
    "connection_id": "...",
    "event_id": "UOAF-2026-08-22"
  },
  "context": {
    "callsign": "VIPR11",
    "mode": "bms",
    "theater_id": "balkans",
    "aircraft_type": "F-16CM-50"
  },
  "attributes": {
    "frequency_khz": 305000,
    "radio": "uhf",
    "capture_callbacks": 50,
    "encoded_packets": 50,
    "udp_packets_sent": 50,
    "rms_dbfs": -18.4,
    "peak_dbfs": -4.2
  }
}
```

Timestamps use UTC and millisecond precision. Producers also retain a monotonic elapsed time within the application/server process so timelines remain useful if a system clock changes.

Unknown fields are rejected at ingestion for the initial schema. A producer can send an older supported schema version. Breaking changes require a new schema version; additive event types or optional fields may remain within the same version.

## Initial event catalog

### Lifecycle and consent

| Event | Trigger |
| --- | --- |
| `app.session.started` | Telemetry starts after consent is known |
| `app.session.ended` | Clean shutdown |
| `app.previous_session.unclean` | A prior session lacks a clean-end marker |
| `telemetry.consent.changed` | Consent changes; contains state and policy version only |
| `telemetry.queue.health` | Low-frequency queue size, dropped count, and oldest record age |
| `telemetry.upload.result` | Batch success/failure category and duration; never includes response bodies |

### OpenFreq signaling and server

| Event | Trigger |
| --- | --- |
| `connection.state.changed` | WebSocket/authentication/reconnect transition |
| `connection.frequency.changed` | Join or leave request/result |
| `connection.peer.changed` | Ephemeral peer join/leave under a connection ID |
| `server.client.lifecycle` | Server connection/authentication/disconnection |
| `server.audio.endpoint.mapped` | Authenticated client UDP endpoint becomes usable; endpoint value excluded |
| `server.audio.relay.summary` | Per-connection/per-frequency aggregate relay counters |
| `server.audio.relay.rejected` | Invalid, unauthenticated, unjoined, malformed, or rate-limited audio |

### BMS, IVC, and radio shared memory

| Event | Trigger |
| --- | --- |
| `bms.process.state.changed` | BMS starts/exits or process monitoring fails |
| `bms.shared_memory.state.changed` | Falcon shared-memory lifecycle transition |
| `bms.radio_memory.state.changed` | RCC/RCS lifecycle transition |
| `bms.radio_mutex.conflict` | Conflict begins, retry milestone, resolution, or cancellation |
| `ivc.process.state.changed` | IVC process begins/stops |
| `radio.mapping.changed` | Raw BMS radio-to-OpenFreq slot mapping changes |
| `radio.state.changed` | Frequency, power, PTT, squelch, or volume changes |
| `radio.volume.applied` | Raw value is mapped to an applied gain |

### Audio and RTP

| Event | Trigger |
| --- | --- |
| `audio.device.inventory.changed` | Enabled playback/capture device set changes |
| `audio.device.selection.changed` | Selected/default relationship or fallback changes |
| `audio.capture.state.changed` | Capture initializes, starts, stops, restarts, or fails |
| `audio.playback.state.changed` | Playback initializes, changes device, stops, or fails |
| `audio.capture.health` | Per-transmission or periodic aggregate input measurements |
| `audio.playback.health` | Per-source aggregate pushed/played/silent/underrun counters |
| `rtp.sender.health` | Periodic sent/drop/error counters |
| `rtp.receiver.health` | Periodic received/lost/late/duplicate/jitter/buffer/FEC/PLC counters |
| `radio.transmission.health` | End-to-end client summary for one transmission interval |

### Tacview, theater, and propagation

| Event | Trigger |
| --- | --- |
| `acmi.connection.state.changed` | Connection and retry transitions |
| `acmi.frame.health` | Low-frequency frame age and object-count summary |
| `acmi.tracking.changed` | Tracking selection succeeds, clears, or fails |
| `theater.selection.changed` | Selected theater changes |
| `heightmap.load.result` | Load success/failure with fingerprint and dimensions |
| `position.sample` | Bounded sampling rule described above |
| `propagation.calculation.sample` | Active transmission sample or anomalous calculation |
| `propagation.anomaly` | Below-terrain, invalid transform, missing heightmap, or invalid result |

## End-to-end transmission reconstruction

A successful diagnostic chain for a transmission should answer:

1. Did BMS report the expected PTT and radio?
2. Which OpenFreq frequency/slot did the client map it to?
3. Did capture callbacks contain a non-silent signal?
4. Did encoding produce packets?
5. Did the client send them without a socket error?
6. Did the server authenticate and accept the UDP source?
7. Did the server relay packets to each expected connection?
8. Did each receiver receive, decode, and push audio to playback?
9. Which device and gain were active at playback?
10. Which transmitter/receiver positions, heightmaps, and propagation result were used?

Events use aggregate counters at boundaries rather than a record for every packet. A transmission interval carries a locally generated `transmission_id`; the server and receivers associate it with `connection_id`, frequency, RTP source/SSRC, and time window. If adding `transmission_id` to packet metadata would create a compatibility or bandwidth problem, reconstruction can initially use connection, frequency, SSRC, RTP timestamp, and bounded time windows.

## Local spool

### Requirements

- Append-only writes on a background worker.
- Crash-tolerant records and atomic batch state changes.
- No synchronous disk or network I/O from audio callbacks.
- Bounded size and age.
- Safe concurrent startup/shutdown behavior.
- Idempotent uploads.
- Inspectable/exportable format.

Initial limits:

- Maximum local age: 7 days.
- Maximum total size: 50 MiB.
- Maximum individual record: 64 KiB.
- Maximum upload batch: 1 MiB compressed or 1,000 records, whichever occurs first.
- When full, remove the oldest acknowledged/low-severity data first and preserve recent errors and session boundaries where possible.
- Emit a dropped-record counter instead of recursively logging a telemetry event for every drop.

The implementation may use SQLite or segmented newline-delimited JSON. The selection must be justified with crash-recovery and packaging tests. Raw text logs are not part of the automatic spool.

The queue belongs under the platform's per-user local application-data directory, not beside the executable. Portable distributions may reside in read-only or synchronized folders.

## Upload behavior

Uploads occur only while consent is granted:

- Shortly after a consented application session starts, to retry prior batches.
- Approximately every 60 seconds while a server connection is active and queued data exists.
- After a connection/session ends.
- When the user selects **Send queued diagnostics now**.

Upload behavior:

- HTTPS only.
- Compression enabled.
- Short connection and request timeouts.
- Exponential backoff with jitter for retryable errors.
- Honor `Retry-After` for throttling.
- Do not retry permanent schema/authentication errors indefinitely.
- Never block application startup, connection, the audio thread, or shutdown.
- Cancel promptly on consent withdrawal.
- Delete a batch only after an idempotent success response for its `batch_id`.

An API key embedded in a distributed client is not a secret. The OpenFreq server provides a short-lived telemetry upload capability only when the authentication request reports current explicit consent. The ingestion service validates the capability and records its issuing `server_run_id`/`event_id` as authorization metadata. Each record retains its own historical correlation, so pre-authentication and prior-run records can upload later without being falsely attributed to the current event. Manual export remains available when a client never authenticates.

Community servers can omit the telemetry capability. A future design may allow operators to configure their own compatible endpoint, but a server must not silently redirect clients to an undisclosed telemetry recipient.

## Ingestion and storage

### API contract

The ingestion endpoint accepts a compressed batch containing:

- `batch_id`
- Batch schema/protocol version.
- Producer metadata.
- Ordered event records.
- Optional client-side checksum.

It performs:

1. TLS termination and request-size enforcement.
2. Token issuer, expiry, and rate-limit validation.
3. Decompression limits to prevent zip bombs.
4. Strict schema validation.
5. Server-side field redaction as a second line of defense.
6. `batch_id` deduplication.
7. Durable write before acknowledging success.
8. Operational metrics without copying sensitive request bodies into infrastructure logs.

### Storage layout

Raw validated batches are immutable and partitioned for bounded queries, for example:

```text
s3://<bucket>/schema=1/date=2026-08-22/authorized-event=UOAF-2026-08-22/authorized-server=<uuid>/batch=<uuid>.json.gz
```

The authorization partitions identify the server that admitted an uploader. Historical attribution remains in each record's `correlation.event_id` and `correlation.server_run_id`; a queued batch may legitimately span application or server runs.

An optional DynamoDB index stores small session-level records such as event ID, server run ID, callsign, connection ID, first/last timestamps, client version, upload completeness, and anomaly flags. It does not need to store every position or packet-health event.

Athena/Glue or an equivalent analytical system reads the immutable event batches. Saved investigations should include:

- PTT/capture active but zero UDP packets sent.
- Client sent packets but server received none.
- Server relayed to one connection but not another.
- Receiver got packets but decoded/played none.
- Audio device fallback or inventory change near a failure.
- Radio mapping disagreeing with BMS PTT/radio type.
- Volume mapping discontinuity after a cockpit knob change.
- ACMI connected with zero eligible aircraft or stale frames.
- Theater/heightmap mismatch within the same event.
- Aircraft below sampled terrain or invalid propagation results.

## Retention and access

Initial defaults:

- Client spool: 7 days or 50 MiB.
- Hosted raw event telemetry: 30 days.
- Aggregated issue statistics without callsigns or exact positions: up to 12 months.
- Manual exports: controlled entirely by the user after creation.

Hosted access is limited to designated OpenFreq maintainers with a diagnostic need. Storage access and saved-query access are audited. Production telemetry is not copied to developer laptops by default.

Deletion mechanisms must support:

- Immediate deletion of a client's local queue.
- Deletion of hosted data by `installation_id` when the user supplies it.
- Deletion by `event_id` or `server_run_id` for operator incident handling.
- Automatic lifecycle deletion at the retention boundary.

## Reliability and performance constraints

- Audio callbacks perform only non-blocking counter updates or a bounded enqueue; no serialization, file I/O, locks with unbounded wait, DNS, or network I/O.
- Telemetry queues use explicit capacity limits.
- High-frequency measurements are aggregated before enqueue.
- Telemetry failures never change OpenFreq connection or audio state.
- The uploader has its own cancellation token and time budget.
- Shutdown may leave a valid queued batch for the next launch rather than waiting for the network.
- The telemetry service must be safely disabled at runtime.
- Telemetry records its own aggregate dropped-count and queue health, without recursive self-reporting.

Initial performance acceptance criteria:

- No measurable additional audio callback underruns in the load test.
- Less than 1% average CPU overhead during a representative event on the reference system.
- Less than 50 MiB bounded disk use.
- No startup delay attributable to upload attempts.
- A network-disabled client continues normal radio operation and uploads later.

## Security considerations

- Treat all client fields as untrusted even when the token is valid.
- Strictly cap request, decompressed batch, event, string, array, and nesting sizes.
- Validate enum and numeric ranges, including simulated coordinates.
- Rate-limit by token/server and at the edge.
- Do not place AWS or database credentials in clients.
- Do not use an embedded application API key as authentication.
- Reject unknown fields in the initial schema.
- Prevent exception messages, paths, headers, or request bodies from leaking into infrastructure logs.
- Encrypt hosted data in transit and at rest.
- Separate ingestion permission from query/read permission.
- Rotate signing/ingestion credentials without requiring a client release.

## Implementation boundaries

Suggested client interfaces:

```csharp
public interface ITelemetryService
{
    bool IsEnabled { get; }
    void Track<TEvent>(TEvent telemetryEvent) where TEvent : ITelemetryEvent;
    Task FlushAsync(CancellationToken cancellationToken);
    Task DeleteQueuedAsync(CancellationToken cancellationToken);
    Task ExportBundleAsync(string destination, ExportOptions options, CancellationToken cancellationToken);
}

public interface ITelemetrySpool
{
    ValueTask EnqueueAsync(TelemetryEnvelope record, CancellationToken cancellationToken);
    Task<TelemetryBatch?> ClaimBatchAsync(CancellationToken cancellationToken);
    Task AcknowledgeAsync(Guid batchId, CancellationToken cancellationToken);
    Task ReleaseAsync(Guid batchId, CancellationToken cancellationToken);
}
```

Domain components emit typed, allowlisted events. They do not know about HTTP, AWS, or storage. The uploader does not receive application configuration or an unrestricted logger stream.

OpenTelemetry may be used as an interchange/export format and for metrics/traces, but its normal in-memory batch queue does not replace the durable spool. The initial implementation should avoid routing the existing unrestricted Serilog stream into hosted telemetry.

## Testing strategy

### Unit tests

- Every registered event serializes to the approved schema.
- Unknown fields and oversized values are rejected.
- Redaction removes passwords, raw device names/GUIDs, paths, IP addresses, and account/machine names.
- Consent states and policy upgrades behave correctly.
- Callsign originates from an approved BMS/public-callsign field.
- Spool limits, eviction priorities, crash recovery, deduplication, and acknowledgement work.
- Upload retry classification and `Retry-After` handling work.
- Disabling consent cancels collection/upload and optionally deletes the queue.

### Integration tests

- A client/server pair shares `server_run_id`, `connection_id`, and `event_id`.
- A transmission can be joined across sender, server, and receiver records.
- A killed client leaves recoverable queued telemetry.
- An offline client does not block or fail application behavior and uploads on a later run.
- Older clients interoperate with authentication responses containing the new optional fields.
- Older servers interoperate with new clients without correlation fields.
- Ingestion rejects malformed, oversized, unauthorized, and duplicate batches.

### Performance and event simulations

- Multiple simultaneous speakers and frequencies.
- Packet loss, jitter, UDP mapping delay, and asymmetric routing.
- Capture and playback devices disappearing/reappearing.
- Saved device missing at startup and fallback behavior.
- IVC mutex conflict that later resolves.
- Tacview connected with stale frames or zero eligible aircraft.
- Korea-to-Balkans theater change with a stale heightmap.
- BMS radio reversal/mapping and cockpit volume transitions.
- Consent withdrawal during an active upload.

## Delivery plan

### PR 1: contract, consent, and local diagnostics

- Add the typed event envelope and schema tests.
- Add consent state and policy version to settings.
- Add the first-run dialog and settings controls.
- Add a bounded durable local spool.
- Add manual structured diagnostic export.
- Do not enable any remote upload.

Acceptance: nothing is transmitted; users can inspect/delete/export structured data; redaction and spool recovery tests pass.

### PR 2: critical client instrumentation

- Instrument application and connection lifecycle.
- Instrument IVC, RCC/RCS, BMS radio mapping, and volume mapping.
- Instrument audio device selection and capture/playback health.
- Instrument RTP aggregate health.
- Instrument Tacview/ACMI health.
- Instrument theater, heightmap, position, and propagation calculations.

Acceptance: a local export can distinguish the major Discord-reported failure classes without audio or prohibited fields.

### PR 3: server correlation and instrumentation

- Generate `server_run_id` and accept optional operator `event_id`.
- Extend authentication success with optional correlation/capability fields.
- Instrument server signaling, UDP mapping, rejection, and relay summaries.
- Add compatibility and multi-client reconstruction tests.

Acceptance: sender, server, and receiver events form a coherent event timeline.

### PR 4: hosted ingestion and uploader

- Add infrastructure-as-code for the ingestion API and storage.
- Add token issuance/validation and rate limits.
- Add the client uploader and retry behavior.
- Add retention/deletion automation and saved diagnostic queries.
- Conduct an opt-in UOAF pilot before general availability.

Acceptance: an offline-first end-to-end event reaches storage exactly once per batch, is queryable by event/server/connection/callsign, and expires automatically.

## Decisions recorded

- Telemetry is explicit opt-in.
- The client uses a local durable spool plus batched upload.
- Manual export remains available.
- Raw application logs are not uploaded automatically.
- BMS callsigns are collected; real names are not.
- Exact simulated aircraft positions are collected for diagnostic reconstruction.
- Voice audio and recordings are never telemetry.
- The first hosted pilot targets UOAF-operated servers.
- The backend starts with an AWS serverless ingestion pattern; DynamoDB is optional session indexing rather than the raw event store.
- Hosted raw telemetry retention starts at 30 days.

## Deployment decisions still required

- Which AWS account and maintainers own the hosted data.
- Whether infrastructure-as-code lives in this repository or a separately controlled infrastructure repository.
- The public privacy/contact URL shown in the consent details.
- The exact operator workflow and allowed format for `event_id`.
- Whether community servers may later provide independent endpoints and, if so, how recipients are disclosed to users.
