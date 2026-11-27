-- Proyección mínima de los esquemas de App para el E2E de Auth.
\set ON_ERROR_STOP on
BEGIN;
DO $$ BEGIN
    IF current_database() <> 'copp_review_kan170' THEN
        RAISE EXCEPTION 'Solo se admite la base descartable copp_review_kan170';
    END IF;
END $$;
CREATE SCHEMA app;
CREATE TABLE app.patient_profiles (
    id uuid PRIMARY KEY, user_id uuid UNIQUE, first_name text, middle_name text, last_name text,
    date_of_birth date, email text, phone_country_code text, phone_number text, gender text, timezone text
);
CREATE TABLE app.device_tokens (
    id uuid PRIMARY KEY, user_id uuid NOT NULL, token text NOT NULL, UNIQUE(user_id, token)
);
CREATE TABLE app.vital_signs (
    id uuid PRIMARY KEY, patient_id uuid, measured_at timestamptz, systolic integer, diastolic integer,
    heart_rate integer, temperature_c numeric, o2_saturation integer, height_cm numeric, weight_kg numeric
);
CREATE TABLE app.measurement_metrics (id uuid PRIMARY KEY, code text, category text);
CREATE TABLE app.unit_of_measures (id uuid PRIMARY KEY, code text);
CREATE TABLE app.clinical_measurements (
    id uuid PRIMARY KEY, patient_id uuid, metric_id uuid, unit_id uuid, value numeric,
    observed_at timestamptz, recorded_at timestamptz, status text
);
INSERT INTO app.patient_profiles (id,user_id,first_name,last_name,timezone) VALUES
('17010000-0000-4000-8000-000000000001','17000000-0000-4000-8000-000000000001','=QA, Export','Synthetic','America/Bogota'),
('17010000-0000-4000-8000-000000000002','17000000-0000-4000-8000-000000000002','OTHER-SUBJECT','Synthetic','America/Bogota');
INSERT INTO app.vital_signs (id,patient_id,measured_at,heart_rate) VALUES
('17020000-0000-4000-8000-000000000001','17010000-0000-4000-8000-000000000001',now(),71),
('17020000-0000-4000-8000-000000000002','17010000-0000-4000-8000-000000000002',now(),83);
INSERT INTO app.measurement_metrics VALUES ('17030000-0000-4000-8000-000000000001','test_temperature','vital');
INSERT INTO app.unit_of_measures VALUES ('17040000-0000-4000-8000-000000000001','test_celsius');
INSERT INTO app.clinical_measurements VALUES
('17050000-0000-4000-8000-000000000001','17010000-0000-4000-8000-000000000001',
'17030000-0000-4000-8000-000000000001','17040000-0000-4000-8000-000000000001',36.5,now(),now(),'confirmed');
COMMIT;
