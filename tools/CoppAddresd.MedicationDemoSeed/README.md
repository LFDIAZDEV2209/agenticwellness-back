# Explicit synthetic medication demo

Maintenance executable only. It is never started by the API, migrations, or a normal push deployment. It creates one reserved synthetic patient (`900006702`, Demo Medicamentos PROD), app-only identity, enrollment, module opt-in, two clearly labeled demonstration treatments and one persisted demonstration intake. It does not prescribe or send messages.

Execution requires `COPP_MEDICATION_DEMO_CONFIRM=SYNTHETIC_ACCOUNT_ONLY` plus `--production` (only database `coppaddresd` on RDS) or `--isolated-test` (only loopback and a `copp_review_medications_` database). The connection is supplied through the existing secret reference. No migrations, configuration files, credentials, tokens or passwords are bundled or printed. Name/document/ID collisions abort before writes. Replays retain password hashes and do not duplicate treatments or the initial intake. Existing patients and passwords are untouched.

The initial synthetic password is derived with HMAC-SHA256 from the injected connection secret and this reserved user ID; only its salted Identity hash is stored. An operator with existing access to that secret can deliver the generated login privately. Rerunning never resets a changed password.

## Production operation

The existing backend workflow renders `Medications__Enabled` from the `MEDICATIONS_ENABLED_DEV` repository variable (default false); patient access still requires opt-in. With `MEDICATION_DEMO_SEED_DEV=true`, an explicit `workflow_dispatch` runs this executable in a separate ECS task after migrations. Normal pushes never execute it. The task has no task role, HTTP listener, health check, or messaging secrets; it inherits only the DB secret reference, execution role, log destination and API network. No IAM policy or firewall changes are needed. Set the one-shot seed variable back to false after success.

A failed maintenance task blocks the new rollout and reports a safe error code. It may have committed some of this synthetic account's setup; a replay completes it without clearing history or resetting passwords. Clinical history is never cleaned up automatically.

## Local evidence

Validated against the isolated medication PostgreSQL fixture: missing confirmation and production-on-loopback are rejected; two successful runs keep two treatments and exactly one initial dose/intake event, with an unchanged Identity password hash. UI/API persistence coverage is documented in `docs/modules/medications` and the real cross-app E2E host.
