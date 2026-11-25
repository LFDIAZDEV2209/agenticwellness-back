import unittest

from retention_review import CATEGORIES, disposition, reconcile_restore


class RetentionReviewTests(unittest.TestCase):
    def test_unsigned_policy_cannot_allow_action(self):
        draft = {"approval": None, "rules": {}}
        result = disposition(draft, [{"category": "profile"}], "2026-11-25T15:00:00Z")
        self.assertFalse(result["ready"])
        self.assertEqual(result["blocked"], {"unapproved_policy": 1})

    def test_holds_dates_and_unverified_archives_prevent_action(self):
        rules = {name: {"action": "retain", "days": 30} for name in CATEGORIES}
        rules["profile"] = {"action": "delete", "days": 30}
        rules["archives"] = {"action": "archive_then_delete", "days": 30}
        policy = {"approval": {"id": "synthetic", "effectiveAt": "2026-11-01T00:00:00Z"}, "rules": rules}
        old = {"category": "profile", "anchorAt": "2026-10-01T00:00:00Z", "hold": False}
        records = [old, dict(old, hold=True), dict(old, anchorAt="2026-11-24T00:00:00Z"),
                   dict(old, category="archives"), dict(old, category="archives",
                        archiveEvidence={"reference": "synthetic:verified", "sha256": "a" * 64, "restoreVerified": True})]
        result = disposition(policy, records, "2026-11-25T00:00:00Z")
        self.assertFalse(result["ready"])
        self.assertEqual(result["eligible"], {"profile": 1, "archives": 1})
        self.assertEqual(result["blocked"], {"by_hold": 1, "not_due": 1, "archive_not_verified": 1})

    def test_restore_detects_erased_records_and_unchecked_stores(self):
        snapshot = "2026-11-18T00:00:00Z"
        ledger = [{"receiptId": "receipt-1", "category": "profile", "completedAt": "2026-11-19T00:00:00Z"},
                  {"receiptId": "receipt-2", "category": "archives", "completedAt": "2026-11-20T00:00:00Z"},
                  {"receiptId": "receipt-3", "category": "ai", "completedAt": "2026-11-17T00:00:00Z"}]
        findings = [{"receiptId": "receipt-1", "category": "profile", "matchingRecords": 2}]
        result = reconcile_restore(snapshot, ledger, findings)
        self.assertFalse(result["ready"])
        self.assertEqual(result["reappeared"], {"profile": 2})
        self.assertEqual(result["unverified"], {"archives": 1})
        findings[0]["matchingRecords"] = 0
        findings.append({"receiptId": "receipt-2", "category": "archives", "matchingRecords": 0})
        self.assertTrue(reconcile_restore(snapshot, ledger, findings)["ready"])


if __name__ == "__main__":
    unittest.main()
