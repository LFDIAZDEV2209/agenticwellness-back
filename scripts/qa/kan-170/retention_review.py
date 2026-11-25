"""Preflight de disposición y restauración. Solo lee evidencia local y produce conteos."""
import argparse
from collections import Counter
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import re
import sys

CATEGORIES = ("profile", "wellness", "vitals", "clinical", "ai", "service_logs", "audit",
              "archives", "rds_backups", "object_versions")
ACTIONS = {"retain", "delete", "anonymize", "archive_then_delete"}


def instant(value):
    if not isinstance(value, str):
        raise ValueError("timestamp_missing")
    parsed = datetime.fromisoformat(value)
    if parsed.utcoffset() is None:
        raise ValueError("timestamp_requires_offset")
    return parsed.astimezone(timezone.utc)


def disposition(policy, records, now):
    """La evidencia aprobada se aporta por canal administrativo, no por el titular."""
    now = instant(now)
    approval = policy.get("approval")
    def halted(reason):
        return {"ready": False, "eligible": {}, "blocked": {reason: 1}}

    if not isinstance(approval, dict) or not str(approval.get("id") or "").strip():
        return halted("unapproved_policy")
    try:
        if instant(approval.get("effectiveAt")) > now:
            return halted("policy_not_effective")
    except (ValueError, OverflowError):
        return halted("unapproved_policy")

    rules = policy.get("rules")
    if not isinstance(rules, dict) or set(rules) != set(CATEGORIES):
        return halted("incomplete_policy")
    for rule in rules.values():
        if (not isinstance(rule, dict) or rule.get("action") not in ACTIONS or
                type(rule.get("days")) is not int or rule["days"] < 0):
            return halted("incomplete_policy")

    eligible, blocked = Counter(), Counter()
    for row in records:
        category = row.get("category")
        if category not in rules:
            blocked["unmapped_category"] += 1
            continue
        rule = rules[category]
        if rule["action"] == "retain":
            continue
        if row.get("hold") is not False:
            blocked["by_hold"] += 1
            continue
        try:
            due = instant(row.get("anchorAt")) + timedelta(days=rule["days"])
        except (ValueError, OverflowError):
            blocked["invalid_anchor"] += 1
            continue
        if due > now:
            blocked["not_due"] += 1
            continue
        if rule["action"] == "archive_then_delete" or category == "archives":
            proof = row.get("archiveEvidence")
            if (not isinstance(proof, dict) or not proof.get("reference") or
                    not re.fullmatch(r"[0-9a-fA-F]{64}", str(proof.get("sha256", ""))) or
                    proof.get("restoreVerified") is not True):
                blocked["archive_not_verified"] += 1
                continue
        eligible[category] += 1
    return {"ready": not blocked, "eligible": dict(eligible), "blocked": dict(blocked)}


def reconcile_restore(snapshot_at, ledger, findings):
    """Cruzar borrados completados desde el punto de restauración contra lecturas verificadas."""
    snapshot = instant(snapshot_at)

    def key(row):
        receipt_id, category = row.get("receiptId"), row.get("category")
        if not isinstance(receipt_id, str) or not receipt_id or category not in CATEGORIES:
            raise ValueError("invalid_receipt_or_category")
        return receipt_id, category

    observed = {}
    for row in findings:
        pair, count = key(row), row.get("matchingRecords")
        if pair in observed or type(count) is not int or count < 0:
            raise ValueError("invalid_or_duplicate_finding")
        observed[pair] = count

    required = {key(row) for row in ledger if instant(row.get("completedAt")) >= snapshot}
    reappeared, unverified = Counter(), Counter()
    for pair in required:
        count = observed.get(pair)
        if count is None:
            unverified[pair[1]] += 1
        elif count:
            reappeared[pair[1]] += count

    return {"ready": not reappeared and not unverified, "requiredChecks": len(required),
            "reappeared": dict(reappeared), "unverified": dict(unverified)}


def read_json(path):
    with Path(path).open(encoding="utf-8") as f:
        return json.load(f)


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    sub = cli.add_subparsers(dest="command", required=True)
    plan = sub.add_parser("disposition")
    plan.add_argument("--policy", required=True)
    plan.add_argument("--records", required=True)
    plan.add_argument("--now", required=True)
    restore = sub.add_parser("reconcile-restore")
    restore.add_argument("--snapshot-at", required=True)
    restore.add_argument("--ledger", required=True)
    restore.add_argument("--findings", required=True)
    args = cli.parse_args()
    try:
        if args.command == "disposition":
            summary = disposition(read_json(args.policy), read_json(args.records), args.now)
        else:
            summary = reconcile_restore(args.snapshot_at, read_json(args.ledger), read_json(args.findings))
    except (OSError, ValueError, TypeError, AttributeError, OverflowError):
        print(json.dumps({"ready": False, "error": "invalid_or_unreadable_evidence"}))
        return 2
    print(json.dumps(summary, sort_keys=True))
    return 0 if summary["ready"] else 2


if __name__ == "__main__":
    sys.exit(main())
