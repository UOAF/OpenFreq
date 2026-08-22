"""Validated, idempotent ingestion for OpenFreq diagnostic telemetry batches."""

from __future__ import annotations

import base64
import datetime as dt
import gzip
import hashlib
import hmac
import json
import os
import re
import uuid
import zlib
from typing import Any

try:
    import boto3
    from botocore.exceptions import ClientError
except ModuleNotFoundError:  # Lambda provides boto3; local unit tests inject fake clients.
    boto3 = None

    class ClientError(Exception):
        pass

MAX_COMPRESSED_BYTES = 1_048_576
MAX_DECOMPRESSED_BYTES = 8 * 1_048_576
MAX_RECORDS = 1_000
MAX_RECORD_BYTES = 65_536
MAX_STRING_LENGTH = 2_048
SEVERITIES = {"Debug", "Info", "Warning", "Error", "Critical"}
SOURCES = {"client", "server"}

ENVELOPE_FIELDS = {
    "schema_version", "event_record_id", "event_name", "timestamp_utc",
    "monotonic_milliseconds", "severity", "source", "service_version",
    "platform", "correlation", "context", "attributes",
}
PLATFORM_FIELDS = {"os_family", "os_version", "architecture", "runtime_version"}
CORRELATION_FIELDS = {
    "installation_id", "app_session_id", "server_run_id", "connection_id", "event_id",
}
CONTEXT_FIELDS = {"callsign", "mode", "theater_id", "aircraft_type"}

EVENT_FIELDS = {
    "app.session.started": {"distribution"},
    "app.session.ended": {"clean"},
    "telemetry.consent.changed": {"status", "policy_version"},
    "connection.state.changed": {"state", "reason"},
    "ivc.process.state.changed": {"running"},
    "bms.radio_mutex.conflict": {"active", "duration_ms"},
    "bms.shared_memory.state.changed": {"old_state", "new_state"},
    "bms.radio_memory.state.changed": {"old_state", "new_state"},
    "bms.flying_state.changed": {"old_state", "new_state"},
    "radio.state.changed": {"property", "radio", "old_value", "new_value"},
    "radio.state.snapshot": {"radio", "frequency_khz", "raw_volume", "ptt", "power"},
    "audio.device.inventory.changed": {
        "direction", "enabled_device_count", "old_index", "new_index",
        "default_index", "selected_is_default", "device_removed",
    },
    "audio.device.error": {"component"},
    "audio.capture.state.changed": {"state", "device_index", "error_code"},
    "audio.capture.health": {
        "callbacks", "samples", "rms_dbfs", "peak_dbfs", "active_frequency_count",
    },
    "radio.transmission.state.changed": {
        "transmission_id", "state", "frequency_khz", "slot_id", "is_3d",
    },
    "radio.transmission.health": {
        "transmission_id", "frequency_khz", "duration_ms", "capture_callbacks",
        "captured_samples", "send_calls",
    },
    "acmi.connection.state.changed": {"status"},
    "acmi.frame.health": {"aircraft_count", "newest_frame_age_seconds"},
    "position.sample": {
        "source", "bms_x_ft", "bms_y_ft", "bms_z_down_ft", "altitude_msl_ft",
        "terrain_elevation_ft", "altitude_agl_ft",
    },
    "propagation.calculation.sample": {
        "peer_id", "frequency_khz", "tx_x_m", "tx_y_m", "tx_z_m",
        "rx_x_m", "rx_y_m", "rx_z_m", "received_db", "snr_db",
        "dropout_rate", "deep_fade_rate",
    },
    "heightmap.load.result": {
        "success", "theater_id", "width", "height", "bytes_per_sample", "error_code",
    },
    "server.session.started": {"websocket_port", "audio_port"},
    "server.session.ended": {"clean", "client_count"},
    "server.client.lifecycle": {"state", "reason"},
    "server.channel.state.changed": {"state", "frequency_khz", "peer_count"},
    "server.transmission.state.changed": {"state", "frequency_khz", "peer_count", "is_3d"},
    "server.audio.endpoint.mapped": {"address_family"},
    "server.audio.relay.summary": {
        "window_duration_ms", "packets_received", "packets_forwarded", "candidate_receivers",
        "receivers_without_endpoint", "rejected_frequencies", "max_frequency_count",
    },
}

_s3 = None
_secrets = None
_signing_secret = None


class RequestError(Exception):
    def __init__(self, status: int, message: str):
        self.status = status
        self.message = message
        super().__init__(message)


def lambda_handler(event: dict[str, Any], _context: Any) -> dict[str, Any]:
    try:
        headers = {key.lower(): value for key, value in (event.get("headers") or {}).items()}
        token = _bearer_token(headers.get("authorization"))
        capability = _verify_token(token)
        compressed = _request_body(event)
        batch = _decode_batch(compressed, headers.get("content-encoding"))
        _validate_batch(batch, headers, capability)
        key = _storage_key(batch, capability)
        try:
            _s3_client().put_object(
                Bucket=os.environ["TELEMETRY_BUCKET"],
                Key=key,
                Body=gzip.compress(json.dumps(batch, separators=(",", ":")).encode("utf-8")),
                ContentType="application/json",
                ContentEncoding="gzip",
                ServerSideEncryption="AES256",
                IfNoneMatch="*",
            )
        except ClientError as error:
            code = error.response.get("Error", {}).get("Code")
            if code in {"PreconditionFailed", "ConditionalRequestConflict"}:
                return _response(409, {"status": "duplicate", "batch_id": batch["batch_id"]})
            raise
        return _response(202, {"status": "accepted", "batch_id": batch["batch_id"]})
    except RequestError as error:
        return _response(error.status, {"error": error.message})
    except Exception:
        # Request bodies and exception strings are deliberately not logged here.
        return _response(500, {"error": "internal ingestion error"})


def _bearer_token(value: str | None) -> str:
    if not value or not value.startswith("Bearer "):
        raise RequestError(401, "missing telemetry capability")
    return value[7:]


def _verify_token(token: str) -> dict[str, Any]:
    try:
        payload_encoded, signature_encoded = token.split(".", 1)
        expected = hmac.new(
            _get_signing_secret().encode("utf-8"), payload_encoded.encode("ascii"), hashlib.sha256
        ).digest()
        supplied = _base64url_decode(signature_encoded)
        if not hmac.compare_digest(expected, supplied):
            raise RequestError(401, "invalid telemetry capability")
        payload = json.loads(_base64url_decode(payload_encoded))
        if int(payload["ExpiresAtUnixSeconds"]) <= int(dt.datetime.now(dt.UTC).timestamp()):
            raise RequestError(401, "expired telemetry capability")
        return payload
    except RequestError:
        raise
    except (ValueError, KeyError, json.JSONDecodeError):
        raise RequestError(401, "invalid telemetry capability") from None


def _request_body(event: dict[str, Any]) -> bytes:
    body = event.get("body") or ""
    try:
        raw = base64.b64decode(body, validate=True) if event.get("isBase64Encoded") else body.encode("utf-8")
    except (ValueError, UnicodeEncodeError):
        raise RequestError(400, "invalid request body") from None
    if len(raw) > MAX_COMPRESSED_BYTES:
        raise RequestError(413, "compressed batch is too large")
    return raw


def _decode_batch(raw: bytes, content_encoding: str | None) -> dict[str, Any]:
    if content_encoding and content_encoding.lower() != "gzip":
        raise RequestError(415, "unsupported content encoding")
    try:
        if content_encoding:
            decompressor = zlib.decompressobj(16 + zlib.MAX_WBITS)
            decoded = decompressor.decompress(raw, MAX_DECOMPRESSED_BYTES + 1)
            if decompressor.unconsumed_tail or len(decoded) > MAX_DECOMPRESSED_BYTES:
                raise RequestError(413, "decompressed batch is too large")
            decoded += decompressor.flush()
            if not decompressor.eof:
                raise RequestError(400, "invalid gzip body")
        else:
            decoded = raw
    except RequestError:
        raise
    except zlib.error:
        raise RequestError(400, "invalid gzip body") from None
    if len(decoded) > MAX_DECOMPRESSED_BYTES:
        raise RequestError(413, "decompressed batch is too large")
    try:
        value = json.loads(decoded)
    except (UnicodeDecodeError, json.JSONDecodeError):
        raise RequestError(400, "invalid JSON body") from None
    if not isinstance(value, dict):
        raise RequestError(400, "batch must be an object")
    return value


def _validate_batch(batch: dict[str, Any], headers: dict[str, str], capability: dict[str, Any]) -> None:
    if set(batch) != {"protocol_version", "batch_id", "records"}:
        raise RequestError(400, "invalid batch fields")
    if not isinstance(batch["protocol_version"], int) or isinstance(batch["protocol_version"], bool) or \
            batch["protocol_version"] != 1:
        raise RequestError(400, "unsupported protocol version")
    batch_id = str(batch["batch_id"])
    try:
        parsed_batch_id = uuid.UUID(batch_id)
    except (ValueError, AttributeError):
        raise RequestError(400, "invalid batch ID")
    if str(parsed_batch_id).lower() != batch_id.lower():
        raise RequestError(400, "invalid batch ID")
    if headers.get("x-openfreq-batch-id", "").lower() != batch_id.lower():
        raise RequestError(400, "batch ID header mismatch")
    records = batch["records"]
    if not isinstance(records, list) or not 1 <= len(records) <= MAX_RECORDS:
        raise RequestError(400, "invalid record count")

    for record in records:
        if not isinstance(record, dict) or set(record) != ENVELOPE_FIELDS:
            raise RequestError(400, "invalid telemetry envelope")
        if len(json.dumps(record, separators=(",", ":")).encode("utf-8")) > MAX_RECORD_BYTES:
            raise RequestError(413, "telemetry record is too large")
        _validate_envelope_values(record)
        event_name = record.get("event_name")
        if event_name not in EVENT_FIELDS:
            raise RequestError(400, "unknown telemetry event")
        attributes = record.get("attributes")
        if not isinstance(attributes, dict) or set(attributes) - EVENT_FIELDS[event_name]:
            raise RequestError(400, "invalid telemetry attributes")
        platform = record.get("platform")
        correlation = record.get("correlation")
        context = record.get("context")
        if not isinstance(platform, dict) or set(platform) != PLATFORM_FIELDS:
            raise RequestError(400, "invalid telemetry platform")
        if not isinstance(correlation, dict) or set(correlation) - CORRELATION_FIELDS:
            raise RequestError(400, "invalid telemetry correlation")
        if not isinstance(context, dict) or set(context) - CONTEXT_FIELDS:
            raise RequestError(400, "invalid telemetry context")
        _validate_value_lengths(record)


def _validate_envelope_values(record: dict[str, Any]) -> None:
    if not isinstance(record.get("schema_version"), int) or isinstance(record.get("schema_version"), bool) or \
            record["schema_version"] != 1:
        raise RequestError(400, "unsupported telemetry schema")
    _require_uuid(record.get("event_record_id"), "event record ID")
    if not isinstance(record.get("event_name"), str):
        raise RequestError(400, "invalid telemetry event name")
    severity = record.get("severity")
    source = record.get("source")
    if not isinstance(severity, str) or not isinstance(source, str) or \
            severity not in SEVERITIES or source not in SOURCES:
        raise RequestError(400, "invalid telemetry source or severity")
    if not isinstance(record.get("service_version"), str):
        raise RequestError(400, "invalid service version")
    monotonic = record.get("monotonic_milliseconds")
    if not isinstance(monotonic, int) or isinstance(monotonic, bool) or monotonic < 0:
        raise RequestError(400, "invalid monotonic timestamp")
    try:
        parsed = dt.datetime.fromisoformat(str(record.get("timestamp_utc", "")).replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            raise ValueError
    except ValueError:
        raise RequestError(400, "invalid event timestamp") from None

    platform = record["platform"]
    if any(not isinstance(platform.get(field), str) or not platform[field] for field in PLATFORM_FIELDS):
        raise RequestError(400, "invalid telemetry platform value")
    correlation = record["correlation"]
    for field in ("installation_id", "app_session_id", "server_run_id"):
        if field in correlation:
            _require_uuid(correlation[field], field)
    for field in ("connection_id", "event_id"):
        if field in correlation and not isinstance(correlation[field], str):
            raise RequestError(400, "invalid telemetry correlation value")
    context = record["context"]
    if any(not isinstance(value, str) for value in context.values()):
        raise RequestError(400, "invalid telemetry context value")
    attributes = record["attributes"]
    if any(isinstance(value, (dict, list)) for value in attributes.values()):
        raise RequestError(400, "nested telemetry attributes are not allowed")


def _require_uuid(value: Any, label: str) -> None:
    try:
        if str(uuid.UUID(str(value))).lower() != str(value).lower():
            raise ValueError
    except (ValueError, AttributeError):
        raise RequestError(400, f"invalid {label}") from None


def _validate_value_lengths(value: Any) -> None:
    if isinstance(value, str) and len(value) > MAX_STRING_LENGTH:
        raise RequestError(400, "string value is too long")
    if isinstance(value, dict):
        for key, child in value.items():
            if not isinstance(key, str) or len(key) > 128:
                raise RequestError(400, "invalid object key")
            _validate_value_lengths(child)
    elif isinstance(value, list):
        for child in value:
            _validate_value_lengths(child)


def _storage_key(batch: dict[str, Any], capability: dict[str, Any]) -> str:
    first_timestamp = str(batch["records"][0].get("timestamp_utc", ""))
    try:
        date = dt.datetime.fromisoformat(first_timestamp.replace("Z", "+00:00")).date().isoformat()
    except ValueError:
        raise RequestError(400, "invalid event timestamp") from None
    # These partitions describe who authorized this upload. Individual records retain
    # their own correlation, which may refer to an earlier offline server run/event.
    event_id = _safe_partition(str(capability.get("EventId") or "unassigned"))
    server_run = _safe_partition(str(capability["ServerRunId"]))
    batch_id = _safe_partition(str(batch["batch_id"]))
    return (f"schema=1/date={date}/authorized-event={event_id}/"
            f"authorized-server={server_run}/batch={batch_id}.json.gz")


def _safe_partition(value: str) -> str:
    return re.sub(r"[^A-Za-z0-9._-]", "_", value)[:128]


def _base64url_decode(value: str) -> bytes:
    return base64.urlsafe_b64decode(value + "=" * (-len(value) % 4))


def _get_signing_secret() -> str:
    global _signing_secret
    if _signing_secret is None:
        secret = _secrets_client().get_secret_value(SecretId=os.environ["SIGNING_SECRET_ARN"])
        _signing_secret = secret["SecretString"]
    return _signing_secret


def _s3_client():
    global _s3
    if _s3 is None:
        if boto3 is None:
            raise RuntimeError("boto3 is required outside the local unit-test harness")
        _s3 = boto3.client("s3")
    return _s3


def _secrets_client():
    global _secrets
    if _secrets is None:
        if boto3 is None:
            raise RuntimeError("boto3 is required outside the local unit-test harness")
        _secrets = boto3.client("secretsmanager")
    return _secrets


def _response(status: int, body: dict[str, Any]) -> dict[str, Any]:
    return {
        "statusCode": status,
        "headers": {"content-type": "application/json"},
        "body": json.dumps(body, separators=(",", ":")),
    }
