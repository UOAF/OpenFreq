import base64
import datetime as dt
import gzip
import hashlib
import hmac
import json
import os
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parents[1] / "src"))
import handler


class FakeS3:
    def __init__(self):
        self.objects = []

    def put_object(self, **kwargs):
        self.objects.append(kwargs)


class HandlerTests(unittest.TestCase):
    def setUp(self):
        self.secret = "test-signing-key-with-enough-entropy"
        self.run_id = "11111111-1111-1111-1111-111111111111"
        handler._signing_secret = self.secret
        handler._s3 = FakeS3()
        os.environ["TELEMETRY_BUCKET"] = "test-bucket"

    def test_accepts_valid_gzip_batch(self):
        event = self._event(self._batch())
        response = handler.lambda_handler(event, None)
        self.assertEqual(202, response["statusCode"])
        self.assertEqual(1, len(handler._s3.objects))
        self.assertIn("authorized-event=UOAF-TEST", handler._s3.objects[0]["Key"])

    def test_rejects_unknown_event(self):
        batch = self._batch()
        batch["records"][0]["event_name"] = "arbitrary.raw.log"
        response = handler.lambda_handler(self._event(batch), None)
        self.assertEqual(400, response["statusCode"])
        self.assertEqual([], handler._s3.objects)

    def test_accepts_offline_record_from_prior_server_run(self):
        batch = self._batch()
        batch["records"][0]["correlation"]["server_run_id"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        response = handler.lambda_handler(self._event(batch), None)
        self.assertEqual(202, response["statusCode"])

    def test_rejects_invalid_signature(self):
        event = self._event(self._batch())
        event["headers"]["authorization"] += "bad"
        response = handler.lambda_handler(event, None)
        self.assertEqual(401, response["statusCode"])

    def test_rejects_unallowlisted_nested_context(self):
        batch = self._batch()
        batch["records"][0]["context"]["pilot_name"] = "must never be accepted"
        response = handler.lambda_handler(self._event(batch), None)
        self.assertEqual(400, response["statusCode"])

    def test_rejects_invalid_envelope_value_type(self):
        batch = self._batch()
        batch["records"][0]["source"] = ["client"]
        response = handler.lambda_handler(self._event(batch), None)
        self.assertEqual(400, response["statusCode"])

    def test_rejects_decompression_over_limit(self):
        batch = self._batch()
        batch["records"][0]["context"]["callsign"] = "X" * (handler.MAX_DECOMPRESSED_BYTES + 1)
        response = handler.lambda_handler(self._event(batch), None)
        self.assertEqual(413, response["statusCode"])

    def _batch(self):
        return {
            "protocol_version": 1,
            "batch_id": "22222222-2222-2222-2222-222222222222",
            "records": [{
                "schema_version": 1,
                "event_record_id": "33333333-3333-3333-3333-333333333333",
                "event_name": "position.sample",
                "timestamp_utc": "2026-08-22T19:14:32.125Z",
                "monotonic_milliseconds": 100,
                "severity": "Info",
                "source": "client",
                "service_version": "1.1.0",
                "platform": {
                    "os_family": "windows", "os_version": "10.0.0",
                    "architecture": "x64", "runtime_version": "10.0.0",
                },
                "correlation": {
                    "installation_id": "44444444-4444-4444-4444-444444444444",
                    "app_session_id": "55555555-5555-5555-5555-555555555555",
                    "server_run_id": self.run_id,
                    "connection_id": "peer-1",
                    "event_id": "UOAF-TEST",
                },
                "context": {"callsign": "VIPR11", "theater_id": "balkans"},
                "attributes": {
                    "source": "bms", "bms_x_ft": 1, "bms_y_ft": 2,
                    "bms_z_down_ft": -1000, "altitude_msl_ft": 1000,
                    "terrain_elevation_ft": 100, "altitude_agl_ft": 900,
                },
            }],
        }

    def _event(self, batch):
        payload = {
            "ServerRunId": self.run_id,
            "EventId": "UOAF-TEST",
            "IssuedAtUnixSeconds": int(dt.datetime.now(dt.UTC).timestamp()) - 10,
            "ExpiresAtUnixSeconds": int(dt.datetime.now(dt.UTC).timestamp()) + 3600,
        }
        encoded = self._b64(json.dumps(payload, separators=(",", ":")).encode())
        signature = self._b64(hmac.new(self.secret.encode(), encoded.encode(), hashlib.sha256).digest())
        body = gzip.compress(json.dumps(batch, separators=(",", ":")).encode())
        return {
            "headers": {
                "authorization": f"Bearer {encoded}.{signature}",
                "content-encoding": "gzip",
                "x-openfreq-batch-id": batch["batch_id"],
            },
            "isBase64Encoded": True,
            "body": base64.b64encode(body).decode(),
        }

    @staticmethod
    def _b64(value):
        return base64.urlsafe_b64encode(value).decode().rstrip("=")


if __name__ == "__main__":
    unittest.main()
