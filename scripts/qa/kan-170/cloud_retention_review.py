"""Preflight AWS sin red. Evalúa evidencia exportada y no escribe en AWS."""
import argparse
from collections import Counter, defaultdict
from datetime import timedelta
from hashlib import sha256
import json
import re
import sys

from retention_review import instant, read_json


def timestamp(value):
    return value.isoformat().replace("+00:00", "Z")


def rounded_expiry(successor):
    cutoff = successor + timedelta(days=30)
    return cutoff.replace(hour=0, minute=0, second=0, microsecond=0) + timedelta(days=1)


def indexed(rows, key):
    result = {}
    for row in rows:
        marker = row.get(key)
        if not isinstance(marker, str) or not marker or marker in result:
            raise ValueError("duplicate_or_missing_identifier")
        result[marker] = row
    return result


def object_inventory(bucket, inventory, now, status):
    if any(inventory.get(field) for field in ("NextToken", "IsTruncated", "NextKeyMarker", "NextVersionIdMarker")):
        raise ValueError("incomplete_object_inventory")
    by_key = defaultdict(list)
    seen = set()
    counts = Counter(currentVersions=0, deleteMarkers=0)
    for field, is_marker in (("Versions", False), ("DeleteMarkers", True)):
        for item in inventory.get(field, []):
            key, version = item["Key"], item["VersionId"]
            latest, modified = item["IsLatest"], instant(item["LastModified"])
            if not key or not version or type(latest) is not bool or modified > now or (key, version) in seen:
                raise ValueError("invalid_version_metadata")
            seen.add((key, version))
            by_key[key].append((modified, version, latest, is_marker))
            if is_marker:
                counts["deleteMarkers"] += 1
            elif latest:
                counts["currentVersions"] += 1

    results = []
    for key, history in by_key.items():
        history.sort(reverse=True)
        ordered = history[0][2] and sum(row[2] for row in history) == 1 and len({row[0] for row in history}) == len(history)
        for i, (_, version, latest, is_marker) in enumerate(history):
            if latest or is_marker:
                continue
            # Las claves y los IDs de versión pueden ser sensibles. Solo sale un
            # identificador reproducible, no datos de un objeto ni un comando.
            digest = sha256(json.dumps([bucket, key, version], ensure_ascii=False).encode()).hexdigest()
            resource = "s3:" + digest
            row = {"resource": resource, "bucket": bucket, "status": "unverified_order"}
            if ordered and i > 0 and version != "null":
                successor = history[i - 1][0]
                cutoff = rounded_expiry(successor)
                row.update(noncurrentSince=timestamp(successor), eligibleOn=timestamp(cutoff),
                           status=status(resource, cutoff <= now))
            results.append(row)
    return results, counts


def assess(evidence, account):
    if not isinstance(account, str) or not re.fullmatch(r"[0-9]{12}", account) or evidence.get("account") != account:
        raise ValueError("unverified_account")
    now = instant(evidence["observedAt"])
    reviews = evidence.get("reviews", {})
    protected = evidence["protectedSnapshots"]
    if not isinstance(protected, dict) or not all(valid_reference(value) for value in protected.values()):
        raise ValueError("invalid_protected_inventory")
    for review in reviews.values():
        if review.get("decision") not in ("eligible", "retain") or not valid_reference(review.get("reference")):
            raise ValueError("invalid_review")

    def status(resource, due):
        decision = reviews.get(resource, {}).get("decision")
        if decision == "retain":
            return "protected"
        if not due:
            return "not_due"
        return decision or "requires_review"

    objects, counts = [], Counter(currentVersions=0, deleteMarkers=0)
    if not evidence["buckets"]:
        raise ValueError("bucket_scope_missing")
    for bucket, inventory in evidence["buckets"].items():
        rows, bucket_counts = object_inventory(bucket, inventory, now, status)
        objects.extend(rows)
        counts.update(bucket_counts)
    counts["noncurrentVersions"] = len(objects)

    snapshots = []
    observed_snapshots = indexed(evidence["snapshots"], "DBSnapshotIdentifier")
    for name, snapshot in sorted(observed_snapshots.items()):
        created = instant(snapshot["SnapshotCreateTime"])
        if created > now:
            raise ValueError("future_snapshot")
        due = created + timedelta(days=30)
        resource = "rds:" + name
        decision = ("protected" if name in protected else
                    "unverified_state" if snapshot.get("Status") != "available" else status(resource, due <= now))
        snapshots.append({"resource": resource, "eligibleOn": timestamp(due), "status": decision})
    for name in sorted(set(protected) - observed_snapshots.keys()):
        snapshots.append({"resource": "rds:" + name, "status": "protected_not_observed"})

    groups = indexed(evidence["logGroups"], "logGroupName")
    overrides = evidence["logPolicyOverrides"]
    if overrides is not None and not isinstance(overrides, dict):
        raise ValueError("invalid_log_policy_coverage")
    active = evidence["activeGroups"]
    if not isinstance(active, list) or not active or len(set(active)) != len(active):
        raise ValueError("invalid_active_log_scope")
    logs = []
    for name in sorted(active):
        resource = "logs:" + name
        days = groups.get(name, {}).get("retentionInDays")
        if days is not None and (type(days) is not int or days < 1):
            raise ValueError("invalid_log_retention")
        if name not in groups:
            decision = "unverified_group"
        elif overrides is None:
            decision = "override_unverified"
        elif name in overrides:
            decision = "managed_by_account"
        elif days == 30:
            decision = "at_policy"
        elif days is not None and days < 30:
            decision = "shorter_than_policy"
        else:
            decision = status(resource, True)
        logs.append({"resource": resource, "retentionInDays": days, "targetDays": 30, "status": decision})

    return {"account": account, "observedAt": timestamp(now), "counts": dict(counts),
            "objectVersions": sorted(objects, key=lambda row: row["resource"]), "snapshots": snapshots, "logs": logs}


def valid_reference(value):
    return isinstance(value, str) and re.fullmatch(r"[A-Za-z0-9._:/-]{1,128}", value)


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--account", required=True, help="Cuenta esperada, independiente del manifiesto")
    cli.add_argument("--evidence", required=True, help="JSON del inventario y de la revisión")
    args = cli.parse_args()
    try:
        report = assess(read_json(args.evidence), args.account)
    except (KeyError, ValueError, OSError, TypeError, AttributeError, OverflowError):
        print(json.dumps({"error": "invalid_or_incomplete_evidence"}))
        return 2
    print(json.dumps(report, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
