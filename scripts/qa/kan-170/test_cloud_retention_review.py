"""Contrato del preflight AWS. No usa credenciales ni llamadas a la nube."""
import copy
import json
import unittest

from cloud_retention_review import assess


def entry(key, version, latest, stamp):
    return {"Key": key, "VersionId": version, "IsLatest": latest, "LastModified": stamp}


def evidence():
    older = "2026-10-11T12:00:00+00:00"
    return {
        "account": "999999999999",
        "observedAt": "2026-11-27T20:00:00Z",
        "buckets": {"synthetic-bucket": {
            "Versions": [
                entry("PRIVATE_A", "raw-A-1", False, older),
                entry("PRIVATE_A", "raw-A-2", True, "2026-11-26T02:00:00Z"),
                entry("PRIVATE_B", "raw-B-1", False, older),
                entry("PRIVATE_B", "raw-B-2", False, "2026-10-18T01:00:00Z"),
                entry("PRIVATE_C", "raw-C-1", True, older),
            ],
            "DeleteMarkers": [entry("PRIVATE_B", "raw-marker", True, "2026-10-23T01:00:00Z")],
        }},
        "snapshots": [
            {"DBSnapshotIdentifier": "manual-a", "SnapshotCreateTime": older, "Status": "available"},
            {"DBSnapshotIdentifier": "recovery-cut", "SnapshotCreateTime": older, "Status": "available"},
        ],
        "protectedSnapshots": {"recovery-cut": "jira:legacy-recovery"},
        "activeGroups": ["/ecs/app", "/ecs/mixed"],
        "logGroups": [{"logGroupName": "/ecs/app"}, {"logGroupName": "/ecs/mixed", "retentionInDays": 30},
                      {"logGroupName": "/ecs/inactive", "retentionInDays": 14}],
        "logPolicyOverrides": {},
        "reviews": {},
    }


class CloudRetentionReviewTests(unittest.TestCase):
    def test_successor_including_delete_marker_anchors_expiration_without_exposing_keys(self):
        report = assess(evidence(), "999999999999")
        rows = report["objectVersions"]
        self.assertEqual(len(rows), 3)
        self.assertEqual(report["counts"]["currentVersions"], 2)
        self.assertEqual(report["counts"]["deleteMarkers"], 1)
        by_anchor = {row["noncurrentSince"]: row for row in rows}
        self.assertEqual(by_anchor["2026-11-26T02:00:00Z"]["status"], "not_due")
        self.assertEqual(by_anchor["2026-10-23T01:00:00Z"]["eligibleOn"], "2026-11-23T00:00:00Z")
        self.assertEqual(by_anchor["2026-10-18T01:00:00Z"]["status"], "requires_review")
        self.assertNotIn("PRIVATE_", json.dumps(report))
        self.assertNotIn("raw-", json.dumps(report))

    def test_release_requires_review_and_never_overrides_the_archive_exception(self):
        data = evidence()
        report = assess(data, "999999999999")
        approved_version = next(row["resource"] for row in report["objectVersions"] if row["status"] == "requires_review")
        for target in [approved_version, "rds:manual-a", "rds:recovery-cut", "logs:/ecs/app"]:
            data["reviews"][target] = {"decision": "eligible", "reference": "evidence:signed-review"}
        report = assess(data, "999999999999")
        self.assertEqual(sum(row["status"] == "eligible" for row in report["objectVersions"]), 1)
        self.assertEqual({row["resource"]: row["status"] for row in report["snapshots"]},
                         {"rds:manual-a": "eligible", "rds:recovery-cut": "protected"})
        self.assertEqual({row["resource"]: row["status"] for row in report["logs"]},
                         {"logs:/ecs/app": "eligible", "logs:/ecs/mixed": "at_policy"})

    def test_ambiguous_chain_does_not_guess_even_with_a_review(self):
        data = evidence()
        data["buckets"]["synthetic-bucket"]["Versions"][3]["LastModified"] = "2026-10-11T12:00:00Z"
        rows = assess(data, "999999999999")["objectVersions"]
        self.assertEqual(sum(row["status"] == "unverified_order" for row in rows), 2)
        row = next(row for row in rows if row["status"] == "unverified_order")
        data["reviews"][row["resource"]] = {"decision": "eligible", "reference": "evidence:operator"}
        self.assertEqual(sum(row["status"] == "unverified_order"
                             for row in assess(data, "999999999999")["objectVersions"]), 2)

    def test_scope_and_pagination_fail_closed(self):
        data = evidence()
        with self.assertRaises(ValueError):
            assess(data, "123456789012")
        for token in ("NextToken", "IsTruncated"):
            partial = copy.deepcopy(data)
            partial["buckets"]["synthetic-bucket"][token] = True
            with self.assertRaises(ValueError):
                assess(partial, "999999999999")

    def test_missing_protected_snapshot_remains_visible(self):
        data = evidence()
        data["snapshots"].pop()
        row = next(row for row in assess(data, "999999999999")["snapshots"]
                   if row["resource"] == "rds:recovery-cut")
        self.assertEqual(row["status"], "protected_not_observed")

    def test_unknown_or_overridden_logging_policy_is_not_ready_to_apply(self):
        data = evidence()
        data["reviews"]["logs:/ecs/app"] = {"decision": "eligible", "reference": "evidence:logs-ordinary"}
        for unknown, expected in [(None, "override_unverified"), ({"/ecs/app": 90}, "managed_by_account")]:
            data["logPolicyOverrides"] = unknown
            row = next(row for row in assess(data, "999999999999")["logs"] if row["resource"] == "logs:/ecs/app")
            self.assertEqual(row["status"], expected)


if __name__ == "__main__":
    unittest.main()
