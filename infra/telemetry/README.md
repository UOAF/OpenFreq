# OpenFreq telemetry ingestion

This AWS SAM application deploys the opt-in OpenFreq diagnostic telemetry ingestion boundary described in [`docs/telemetry-design.md`](../../docs/telemetry-design.md).

It provides:

- `POST /v1/batches` through API Gateway HTTP API.
- HMAC-signed, server-issued upload capability validation.
- Strict batch, envelope, event-name, and event-field allowlists.
- Compressed/decompressed size limits and edge throttling.
- Idempotent batch storage in a private, encrypted S3 bucket.
- Automatic deletion of raw telemetry after 30 days.

## Deploy

Create a Secrets Manager secret containing a high-entropy signing key of at least 32 bytes. Configure the same key as `telemetrySigningKey` only on approved OpenFreq servers, then deploy:

```shell
sam build
sam deploy --guided --parameter-overrides TelemetrySigningSecretArn=<secret-arn>
```

Copy the `IngestionEndpoint` output into the server's `telemetryEndpoint` setting and set an optional organized-event value in `telemetryEventId`.

The signing key is never distributed in the client. After authentication, the OpenFreq server issues a six-hour signed upload capability. A capability authorizes only the strict allowlisted schema; records keep their original correlation so queued data from earlier offline runs can still be uploaded.

## Query layout

Validated batches use this partitionable key structure:

```text
schema=1/date=YYYY-MM-DD/authorized-event=<event-id>/authorized-server=<server-run-id>/batch=<batch-id>.json.gz
```

The `authorized-*` partitions identify the server that issued the upload capability. Query each record's `correlation.event_id` and `correlation.server_run_id` for its actual historical attribution.

Add an AWS Glue table/Athena view for the JSON envelope after the pilot schema is stable. Do not point ad-hoc tooling at unvalidated request logs; API request bodies are intentionally not logged.

## Tests

From this directory, with `boto3` available:

```shell
python -m unittest discover -s tests -v
```
