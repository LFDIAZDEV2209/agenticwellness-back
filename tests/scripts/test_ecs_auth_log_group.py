"""El despliegue de Auth cambia solo el grupo ordinario, no las tareas ajenas."""
import copy
import json
from pathlib import Path
import subprocess
import unittest


ROOT = Path(__file__).resolve().parents[2]
OLD = "/ecs/cooppadresd-auth"
NEW = "/ecs/cooppadresd-auth-ordinary"


def task(group=OLD):
    return {
        "family": "agenticwellness-auth",
        "executionRoleArn": "synthetic",
        "containerDefinitions": [
            {"name": "auth", "image": "auth:test", "environment": [{"name": "feature", "value": "false"}],
             "logConfiguration": {"logDriver": "awslogs", "options": {
                 "awslogs-group": group, "awslogs-stream-prefix": "ecs", "awslogs-region": "us-east-2"}}},
            {"name": "sidecar", "logConfiguration": {"logDriver": "awslogs",
                                                       "options": {"awslogs-group": "/ecs/other"}}},
        ],
    }


def apply(definition, enabled="true"):
    return subprocess.run(["jq", "-ce", "--arg", "enabled", enabled, "-f", str(ROOT / "scripts/ecs-auth-ordinary-logs.jq")],
                          input=json.dumps(definition), capture_output=True, text=True)


class AuthLogGroupTests(unittest.TestCase):
    def test_only_auth_log_group_changes(self):
        original = task()
        expected = copy.deepcopy(original)
        expected["containerDefinitions"][0]["logConfiguration"]["options"]["awslogs-group"] = NEW
        result = apply(original)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout), expected)

    def test_already_routed_is_idempotent(self):
        original = task(NEW)
        result = apply(original)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout), original)

    def test_rollback_restores_historical_destination_without_touching_other_fields(self):
        result = apply(task(NEW), "false")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout), task())
        self.assertNotEqual(apply(task(), "unknown").returncode, 0)

    def test_refuses_unknown_group_or_driver(self):
        invalid = task("/ecs/protected")
        driver = task()
        driver["containerDefinitions"][0]["logConfiguration"]["logDriver"] = "awsfirelens"
        for definition in (invalid, driver):
            result = apply(definition)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(result.stdout, "")

    def test_refuses_wrong_family_and_duplicate_auth(self):
        wrong = task()
        wrong["family"] = "agenticwellness-api"
        duplicate = task()
        duplicate["containerDefinitions"].append(duplicate["containerDefinitions"][0])
        for definition in (wrong, duplicate):
            result = apply(definition)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(result.stdout, "")


if __name__ == "__main__":
    unittest.main()
