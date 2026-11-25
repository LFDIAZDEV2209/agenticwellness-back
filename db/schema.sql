-- KAN-134. DDL generado por EF Core de 20261123190000_AddMedicationPhotoStorageKeys a 20261125193825_AddMotor365Persistence.
-- Referencia del delta. El historial y la ejecución pertenecen a las migraciones de EF.

START TRANSACTION;
CREATE TABLE app.engine_params (
    version integer NOT NULL,
    engine_ver character varying(32) NOT NULL,
    parameters_json jsonb NOT NULL,
    changed_by uuid NOT NULL,
    reason text NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT "PK_engine_params" PRIMARY KEY (version),
    CONSTRAINT "AK_engine_params_version_engine_ver" UNIQUE (version, engine_ver),
    CONSTRAINT ck_motor365_engine_params_parameters CHECK (jsonb_typeof(parameters_json) = 'object'),
    CONSTRAINT ck_motor365_engine_params_reason CHECK (btrim(reason) <> ''),
    CONSTRAINT ck_motor365_engine_params_version CHECK (version > 0 AND btrim(engine_ver) <> '')
);

CREATE TABLE app.person (
    patient_id uuid NOT NULL,
    sex character varying(12) NOT NULL,
    birth_year integer NOT NULL,
    height_cm numeric(5,2) NOT NULL,
    initial_weight_kg numeric(6,2) NOT NULL,
    initial_waist_cm numeric(5,2) NOT NULL,
    target_weight_kg numeric(6,2),
    activity_level integer NOT NULL,
    goal character varying(16) NOT NULL,
    pace character varying(12) NOT NULL,
    vegetarian boolean NOT NULL,
    lactose_free boolean NOT NULL,
    fitness_level integer NOT NULL,
    start_date date NOT NULL,
    engine_ver character varying(32) NOT NULL,
    plan_version integer NOT NULL,
    CONSTRAINT "PK_person" PRIMARY KEY (patient_id),
    CONSTRAINT ck_motor365_person_activity CHECK (activity_level BETWEEN 1 AND 4),
    CONSTRAINT ck_motor365_person_birth_year CHECK (birth_year BETWEEN 1900 AND EXTRACT(YEAR FROM start_date)),
    CONSTRAINT ck_motor365_person_fitness CHECK (fitness_level BETWEEN 0 AND 3),
    CONSTRAINT ck_motor365_person_goal CHECK (goal IN ('LoseWeight', 'GainMuscle', 'Tone')),
    CONSTRAINT ck_motor365_person_height CHECK (height_cm > 0 AND height_cm <= 300),
    CONSTRAINT ck_motor365_person_initial_waist CHECK (initial_waist_cm > 0 AND initial_waist_cm <= 500),
    CONSTRAINT ck_motor365_person_initial_weight CHECK (initial_weight_kg > 0 AND initial_weight_kg <= 1000),
    CONSTRAINT ck_motor365_person_pace CHECK (pace IN ('Slow', 'Moderate', 'Maximum')),
    CONSTRAINT ck_motor365_person_sex CHECK (sex IN ('Female', 'Male')),
    CONSTRAINT ck_motor365_person_target_weight CHECK (target_weight_kg > 0 AND target_weight_kg <= 1000),
    CONSTRAINT ck_motor365_person_versions CHECK (plan_version > 0 AND btrim(engine_ver) <> ''),
    CONSTRAINT "FK_person_patient_profiles_patient_id" FOREIGN KEY (patient_id) REFERENCES app.patient_profiles (id) ON DELETE RESTRICT
);

CREATE TABLE app.audit_log (
    id uuid NOT NULL DEFAULT (gen_random_uuid()),
    person_id uuid NOT NULL,
    actor_user_id uuid DEFAULT (NULLIF(current_setting('audit.user_id', true), '')::uuid),
    action character varying(80) NOT NULL,
    occurred_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT "PK_audit_log" PRIMARY KEY (id),
    CONSTRAINT ck_motor365_audit_log_action CHECK (btrim(action) <> ''),
    CONSTRAINT "FK_audit_log_person_person_id" FOREIGN KEY (person_id) REFERENCES app.person (patient_id) ON DELETE RESTRICT
);

CREATE TABLE app.consent (
    person_id uuid NOT NULL,
    acceptance_id uuid NOT NULL,
    recorded_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT "PK_consent" PRIMARY KEY (person_id, acceptance_id),
    CONSTRAINT "FK_consent_legal_document_acceptances_acceptance_id" FOREIGN KEY (acceptance_id) REFERENCES app.legal_document_acceptances (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_consent_person_person_id" FOREIGN KEY (person_id) REFERENCES app.person (patient_id) ON DELETE RESTRICT
);

CREATE TABLE app.daily_log (
    person_id uuid NOT NULL,
    day integer NOT NULL,
    log_date date NOT NULL,
    adherence numeric(5,4) NOT NULL,
    sleep_hours numeric(4,2) NOT NULL,
    energy integer NOT NULL,
    pain integer NOT NULL,
    symptoms text[] NOT NULL,
    weight_kg numeric(6,2),
    waist_cm numeric(5,2),
    completed_exercise boolean NOT NULL,
    CONSTRAINT "PK_daily_log" PRIMARY KEY (person_id, day),
    CONSTRAINT ck_motor365_daily_log_adherence CHECK (adherence BETWEEN 0 AND 1),
    CONSTRAINT ck_motor365_daily_log_day CHECK (day BETWEEN 1 AND 365),
    CONSTRAINT ck_motor365_daily_log_energy CHECK (energy BETWEEN 1 AND 5),
    CONSTRAINT ck_motor365_daily_log_pain CHECK (pain BETWEEN 0 AND 10),
    CONSTRAINT ck_motor365_daily_log_sleep CHECK (sleep_hours BETWEEN 0 AND 24),
    CONSTRAINT ck_motor365_daily_log_symptoms CHECK (symptoms <@ ARRAY['mareo','hambre','fatiga','insomnio']::text[]),
    CONSTRAINT ck_motor365_daily_log_waist CHECK (waist_cm > 0 AND waist_cm <= 500),
    CONSTRAINT ck_motor365_daily_log_weight CHECK (weight_kg > 0 AND weight_kg <= 1000),
    CONSTRAINT "FK_daily_log_person_person_id" FOREIGN KEY (person_id) REFERENCES app.person (patient_id) ON DELETE RESTRICT
);

CREATE TABLE app.engine_event (
    id uuid NOT NULL DEFAULT (gen_random_uuid()),
    person_id uuid NOT NULL,
    day integer NOT NULL,
    plan_version integer NOT NULL,
    params_version integer NOT NULL,
    engine_ver character varying(32) NOT NULL,
    kind character varying(12) NOT NULL,
    calorie_delta numeric(8,2),
    rate_percent numeric(10,2),
    message_es text NOT NULL,
    message_en text NOT NULL,
    occurred_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT "PK_engine_event" PRIMARY KEY (id),
    CONSTRAINT ck_motor365_engine_event_day CHECK (day BETWEEN 1 AND 365),
    CONSTRAINT ck_motor365_engine_event_kind CHECK (kind IN ('meta', 'modo', 'sin', 'ajuste')),
    CONSTRAINT ck_motor365_engine_event_version CHECK (plan_version > 0),
    CONSTRAINT "FK_engine_event_engine_params_params_version_engine_ver" FOREIGN KEY (params_version, engine_ver) REFERENCES app.engine_params (version, engine_ver) ON DELETE RESTRICT,
    CONSTRAINT "FK_engine_event_person_person_id" FOREIGN KEY (person_id) REFERENCES app.person (patient_id) ON DELETE RESTRICT
);

CREATE TABLE app.plan_snapshot (
    person_id uuid NOT NULL,
    plan_version integer NOT NULL,
    params_version integer NOT NULL,
    submission_id uuid NOT NULL,
    engine_ver character varying(32) NOT NULL,
    plan_json jsonb NOT NULL,
    generated_at timestamp with time zone NOT NULL DEFAULT (now()),
    screening_complete boolean NOT NULL DEFAULT TRUE,
    CONSTRAINT "PK_plan_snapshot" PRIMARY KEY (person_id, plan_version, params_version),
    CONSTRAINT ck_motor365_plan_snapshot_plan CHECK (jsonb_typeof(plan_json) = 'object'),
    CONSTRAINT ck_motor365_plan_snapshot_screening CHECK (screening_complete),
    CONSTRAINT ck_motor365_plan_snapshot_version CHECK (plan_version > 0),
    CONSTRAINT "FK_plan_snapshot_engine_params_params_version_engine_ver" FOREIGN KEY (params_version, engine_ver) REFERENCES app.engine_params (version, engine_ver) ON DELETE RESTRICT,
    CONSTRAINT "FK_plan_snapshot_person_person_id" FOREIGN KEY (person_id) REFERENCES app.person (patient_id) ON DELETE CASCADE
);

CREATE TABLE app.screening (
    person_id uuid NOT NULL,
    submission_id uuid NOT NULL,
    recorded_at timestamp with time zone NOT NULL DEFAULT (now()),
    par_q_1 smallint,
    par_q_2 smallint,
    par_q_3 smallint,
    par_q_4 smallint,
    par_q_5 smallint,
    par_q_6 smallint,
    par_q_7 smallint,
    nqs_1 smallint,
    nqs_2 smallint,
    nqs_3 smallint,
    nqs_4 smallint,
    nqs_5 smallint,
    nqs_6 smallint,
    answers_complete boolean GENERATED ALWAYS AS (par_q_1 IS NOT NULL AND par_q_2 IS NOT NULL AND par_q_3 IS NOT NULL AND par_q_4 IS NOT NULL AND par_q_5 IS NOT NULL AND par_q_6 IS NOT NULL AND par_q_7 IS NOT NULL AND nqs_1 IS NOT NULL AND nqs_2 IS NOT NULL AND nqs_3 IS NOT NULL AND nqs_4 IS NOT NULL AND nqs_5 IS NOT NULL AND nqs_6 IS NOT NULL) STORED NOT NULL,
    CONSTRAINT "PK_screening" PRIMARY KEY (person_id, submission_id),
    CONSTRAINT ck_motor365_screening_nqs_1 CHECK (nqs_1 IN (0, 1)),
    CONSTRAINT ck_motor365_screening_nqs_2 CHECK (nqs_2 IN (0, 1)),
    CONSTRAINT ck_motor365_screening_nqs_3 CHECK (nqs_3 IN (0, 1)),
    CONSTRAINT ck_motor365_screening_nqs_4 CHECK (nqs_4 IN (0, 1)),
    CONSTRAINT ck_motor365_screening_nqs_5 CHECK (nqs_5 IN (0, 1)),
    CONSTRAINT ck_motor365_screening_nqs_6 CHECK (nqs_6 IN (0, 1)),
    CONSTRAINT ck_motor365_screening_par_q_1 CHECK (par_q_1 IN (0, 1)),
    CONSTRAINT ck_motor365_screening_par_q_2 CHECK (par_q_2 IN (0, 1)),
    CONSTRAINT ck_motor365_screening_par_q_3 CHECK (par_q_3 IN (0, 1)),
    CONSTRAINT ck_motor365_screening_par_q_4 CHECK (par_q_4 IN (0, 1)),
    CONSTRAINT ck_motor365_screening_par_q_5 CHECK (par_q_5 IN (0, 1)),
    CONSTRAINT ck_motor365_screening_par_q_6 CHECK (par_q_6 IN (0, 1)),
    CONSTRAINT ck_motor365_screening_par_q_7 CHECK (par_q_7 IN (0, 1)),
    CONSTRAINT "FK_screening_person_person_id" FOREIGN KEY (person_id) REFERENCES app.person (patient_id) ON DELETE RESTRICT
);

CREATE TABLE app.professional_review (
    id uuid NOT NULL DEFAULT (gen_random_uuid()),
    person_id uuid NOT NULL,
    submission_id uuid NOT NULL,
    professional_id uuid NOT NULL,
    decision character varying(16) NOT NULL,
    notes text,
    reviewed_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT "PK_professional_review" PRIMARY KEY (id),
    CONSTRAINT ck_motor365_professional_review_decision CHECK (decision IN ('Approved', 'Rejected', 'NeedsChanges')),
    CONSTRAINT "FK_professional_review_professionals_professional_id" FOREIGN KEY (professional_id) REFERENCES erp.professionals (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_professional_review_screening_person_id_submission_id" FOREIGN KEY (person_id, submission_id) REFERENCES app.screening (person_id, submission_id) ON DELETE RESTRICT
);

CREATE INDEX "IX_audit_log_occurred_at" ON app.audit_log (occurred_at);

CREATE INDEX "IX_audit_log_person_id_occurred_at" ON app.audit_log (person_id, occurred_at);

CREATE UNIQUE INDEX "IX_consent_acceptance_id" ON app.consent (acceptance_id);

CREATE INDEX "IX_consent_person_id_recorded_at" ON app.consent (person_id, recorded_at);

CREATE INDEX "IX_daily_log_log_date" ON app.daily_log (log_date);

CREATE UNIQUE INDEX "IX_daily_log_person_id_log_date" ON app.daily_log (person_id, log_date);

CREATE INDEX "IX_engine_event_occurred_at" ON app.engine_event (occurred_at);

CREATE INDEX "IX_engine_event_params_version_engine_ver" ON app.engine_event (params_version, engine_ver);

CREATE INDEX "IX_engine_event_person_id_day" ON app.engine_event (person_id, day);

CREATE INDEX "IX_engine_params_created_at" ON app.engine_params (created_at);

CREATE INDEX "IX_plan_snapshot_generated_at" ON app.plan_snapshot (generated_at);

CREATE INDEX "IX_plan_snapshot_params_version_engine_ver" ON app.plan_snapshot (params_version, engine_ver);

CREATE INDEX "IX_plan_snapshot_person_id_submission_id" ON app.plan_snapshot (person_id, submission_id);

CREATE INDEX "IX_professional_review_person_id_reviewed_at" ON app.professional_review (person_id, reviewed_at);

CREATE INDEX "IX_professional_review_person_id_submission_id" ON app.professional_review (person_id, submission_id);

CREATE INDEX "IX_professional_review_professional_id" ON app.professional_review (professional_id);

CREATE INDEX "IX_screening_person_id_recorded_at" ON app.screening (person_id, recorded_at);

CREATE UNIQUE INDEX ux_motor365_screening_completion ON app.screening (person_id, submission_id, answers_complete);

ALTER TABLE app.plan_snapshot ADD CONSTRAINT fk_motor365_snapshot_complete_screening FOREIGN KEY (person_id, submission_id, screening_complete) REFERENCES app.screening (person_id, submission_id, answers_complete) ON UPDATE RESTRICT ON DELETE RESTRICT;

CREATE FUNCTION app.motor365_reject_audit_mutation()
RETURNS trigger LANGUAGE plpgsql AS $motor365$
BEGIN
    RAISE EXCEPTION 'La bitácora del Motor 365 solo admite inserciones'
        USING ERRCODE = '55000';
END;
$motor365$;

CREATE TRIGGER motor365_audit_log_append_only
BEFORE UPDATE OR DELETE OR TRUNCATE ON app.audit_log
FOR EACH STATEMENT EXECUTE FUNCTION app.motor365_reject_audit_mutation();

REVOKE UPDATE, DELETE, TRUNCATE ON TABLE app.audit_log FROM PUBLIC;
DO $motor365_acl$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_user') THEN
        REVOKE UPDATE, DELETE, TRUNCATE ON TABLE app.audit_log FROM app_user;
        GRANT SELECT, INSERT ON TABLE app.audit_log TO app_user;
    END IF;
END;
$motor365_acl$;

COMMIT;
