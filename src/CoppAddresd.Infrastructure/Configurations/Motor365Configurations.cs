using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Wellness365;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoppAddresd.Infrastructure.Configurations;

internal sealed class Motor365PersonConfiguration : IEntityTypeConfiguration<Motor365Person>
{
    public void Configure(EntityTypeBuilder<Motor365Person> builder)
    {
        builder.ToTable("person", "app", table =>
        {
            table.HasCheckConstraint("ck_motor365_person_sex", "sex IN ('Female', 'Male')");
            table.HasCheckConstraint("ck_motor365_person_birth_year", "birth_year BETWEEN 1900 AND EXTRACT(YEAR FROM start_date)");
            table.HasCheckConstraint("ck_motor365_person_height", "height_cm > 0 AND height_cm <= 300");
            table.HasCheckConstraint("ck_motor365_person_initial_weight", "initial_weight_kg > 0 AND initial_weight_kg <= 1000");
            table.HasCheckConstraint("ck_motor365_person_initial_waist", "initial_waist_cm > 0 AND initial_waist_cm <= 500");
            table.HasCheckConstraint("ck_motor365_person_target_weight", "target_weight_kg > 0 AND target_weight_kg <= 1000");
            table.HasCheckConstraint("ck_motor365_person_activity", "activity_level BETWEEN 1 AND 4");
            table.HasCheckConstraint("ck_motor365_person_goal", "goal IN ('LoseWeight', 'GainMuscle', 'Tone')");
            table.HasCheckConstraint("ck_motor365_person_pace", "pace IN ('Slow', 'Moderate', 'Maximum')");
            table.HasCheckConstraint("ck_motor365_person_fitness", "fitness_level BETWEEN 0 AND 3");
            table.HasCheckConstraint("ck_motor365_person_versions", "plan_version > 0 AND btrim(engine_ver) <> ''");
            table.HasCheckConstraint("ck_motor365_person_calendar",
                "(calendar_timezone_id IS NULL AND calendar_anchor_date IS NULL AND calendar_anchor_day IS NULL) OR " +
                "(calendar_timezone_id IS NOT NULL AND btrim(calendar_timezone_id) <> '' AND " +
                "calendar_anchor_date IS NOT NULL AND calendar_anchor_day IS NOT NULL AND calendar_anchor_day BETWEEN 1 AND 365)");
        });

        builder.HasKey(x => x.PatientId);
        builder.Property(x => x.PatientId).HasColumnName("patient_id").ValueGeneratedNever();
        builder.Property(x => x.Sex).HasColumnName("sex").HasConversion<string>().HasMaxLength(12);
        builder.Property(x => x.BirthYear).HasColumnName("birth_year");
        builder.Property(x => x.HeightCm).HasColumnName("height_cm").HasPrecision(5, 2);
        builder.Property(x => x.InitialWeightKg).HasColumnName("initial_weight_kg").HasPrecision(6, 2);
        builder.Property(x => x.InitialWaistCm).HasColumnName("initial_waist_cm").HasPrecision(5, 2);
        builder.Property(x => x.TargetWeightKg).HasColumnName("target_weight_kg").HasPrecision(6, 2);
        builder.Property(x => x.ActivityLevel).HasColumnName("activity_level");
        builder.Property(x => x.Goal).HasColumnName("goal").HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.Pace).HasColumnName("pace").HasConversion<string>().HasMaxLength(12);
        builder.Property(x => x.Vegetarian).HasColumnName("vegetarian");
        builder.Property(x => x.LactoseFree).HasColumnName("lactose_free");
        builder.Property(x => x.FitnessLevel).HasColumnName("fitness_level");
        builder.Property(x => x.StartDate).HasColumnName("start_date");
        builder.Property(x => x.CalendarTimeZoneId).HasColumnName("calendar_timezone_id").HasMaxLength(64);
        builder.Property(x => x.CalendarAnchorDate).HasColumnName("calendar_anchor_date");
        builder.Property(x => x.CalendarAnchorDay).HasColumnName("calendar_anchor_day");
        builder.Property(x => x.EngineVer).HasColumnName("engine_ver").HasMaxLength(32);
        builder.Property(x => x.PlanVersion).HasColumnName("plan_version").IsConcurrencyToken();

        builder.HasOne<PatientProfile>().WithOne().HasForeignKey<Motor365Person>(x => x.PatientId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class Motor365ConsentConfiguration : IEntityTypeConfiguration<Motor365Consent>
{
    public void Configure(EntityTypeBuilder<Motor365Consent> builder)
    {
        builder.ToTable("consent", "app");

        builder.HasKey(x => new { x.PersonId, x.AcceptanceId });
        builder.Property(x => x.PersonId).HasColumnName("person_id");
        builder.Property(x => x.AcceptanceId).HasColumnName("acceptance_id");
        builder.Property(x => x.RecordedAt).HasColumnName("recorded_at").HasDefaultValueSql("now()");

        builder.HasOne<Motor365Person>().WithMany().HasForeignKey(x => x.PersonId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<LegalDocumentAcceptance>().WithMany().HasForeignKey(x => x.AcceptanceId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.AcceptanceId).IsUnique();
        builder.HasIndex(x => new { x.PersonId, x.RecordedAt });
    }
}

internal sealed class Motor365ScreeningConfiguration : IEntityTypeConfiguration<Motor365Screening>
{
    public void Configure(EntityTypeBuilder<Motor365Screening> builder)
    {
        builder.ToTable("screening", "app", table =>
        {
            table.HasCheckConstraint("ck_motor365_screening_par_q_1", "par_q_1 IN (0, 1)");
            table.HasCheckConstraint("ck_motor365_screening_par_q_2", "par_q_2 IN (0, 1)");
            table.HasCheckConstraint("ck_motor365_screening_par_q_3", "par_q_3 IN (0, 1)");
            table.HasCheckConstraint("ck_motor365_screening_par_q_4", "par_q_4 IN (0, 1)");
            table.HasCheckConstraint("ck_motor365_screening_par_q_5", "par_q_5 IN (0, 1)");
            table.HasCheckConstraint("ck_motor365_screening_par_q_6", "par_q_6 IN (0, 1)");
            table.HasCheckConstraint("ck_motor365_screening_par_q_7", "par_q_7 IN (0, 1)");
            table.HasCheckConstraint("ck_motor365_screening_nqs_1", "nqs_1 IN (0, 1)");
            table.HasCheckConstraint("ck_motor365_screening_nqs_2", "nqs_2 IN (0, 1)");
            table.HasCheckConstraint("ck_motor365_screening_nqs_3", "nqs_3 IN (0, 1)");
            table.HasCheckConstraint("ck_motor365_screening_nqs_4", "nqs_4 IN (0, 1)");
            table.HasCheckConstraint("ck_motor365_screening_nqs_5", "nqs_5 IN (0, 1)");
            table.HasCheckConstraint("ck_motor365_screening_nqs_6", "nqs_6 IN (0, 1)");
        });

        builder.HasKey(x => new { x.PersonId, x.SubmissionId });
        builder.Property(x => x.PersonId).HasColumnName("person_id");
        builder.Property(x => x.SubmissionId).HasColumnName("submission_id");
        builder.Property(x => x.RecordedAt).HasColumnName("recorded_at").HasDefaultValueSql("now()");
        builder.Property(x => x.ParQ1).HasColumnName("par_q_1");
        builder.Property(x => x.ParQ2).HasColumnName("par_q_2");
        builder.Property(x => x.ParQ3).HasColumnName("par_q_3");
        builder.Property(x => x.ParQ4).HasColumnName("par_q_4");
        builder.Property(x => x.ParQ5).HasColumnName("par_q_5");
        builder.Property(x => x.ParQ6).HasColumnName("par_q_6");
        builder.Property(x => x.ParQ7).HasColumnName("par_q_7");
        builder.Property(x => x.Nqs1).HasColumnName("nqs_1");
        builder.Property(x => x.Nqs2).HasColumnName("nqs_2");
        builder.Property(x => x.Nqs3).HasColumnName("nqs_3");
        builder.Property(x => x.Nqs4).HasColumnName("nqs_4");
        builder.Property(x => x.Nqs5).HasColumnName("nqs_5");
        builder.Property(x => x.Nqs6).HasColumnName("nqs_6");
        builder.Property<bool>("AnswersComplete").HasColumnName("answers_complete")
            .HasComputedColumnSql(
                "par_q_1 IS NOT NULL AND par_q_2 IS NOT NULL AND par_q_3 IS NOT NULL AND par_q_4 IS NOT NULL AND " +
                "par_q_5 IS NOT NULL AND par_q_6 IS NOT NULL AND par_q_7 IS NOT NULL AND nqs_1 IS NOT NULL AND " +
                "nqs_2 IS NOT NULL AND nqs_3 IS NOT NULL AND nqs_4 IS NOT NULL AND nqs_5 IS NOT NULL AND nqs_6 IS NOT NULL", stored: true);

        builder.HasOne<Motor365Person>().WithMany().HasForeignKey(x => x.PersonId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(nameof(Motor365Screening.PersonId), nameof(Motor365Screening.SubmissionId), "AnswersComplete")
            .IsUnique().HasDatabaseName("ux_motor365_screening_completion");
        builder.HasIndex(x => new { x.PersonId, x.RecordedAt });
    }
}

internal sealed class Motor365DailyLogConfiguration : IEntityTypeConfiguration<Motor365DailyLog>
{
    public void Configure(EntityTypeBuilder<Motor365DailyLog> builder)
    {
        builder.ToTable("daily_log", "app", table =>
        {
            table.HasCheckConstraint("ck_motor365_daily_log_day", "day BETWEEN 1 AND 365");
            table.HasCheckConstraint("ck_motor365_daily_log_adherence", "adherence BETWEEN 0 AND 1");
            table.HasCheckConstraint("ck_motor365_daily_log_sleep", "sleep_hours BETWEEN 0 AND 24");
            table.HasCheckConstraint("ck_motor365_daily_log_symptoms", "symptoms <@ ARRAY['mareo','hambre','fatiga','insomnio','nauseas']::text[]");
            table.HasCheckConstraint("ck_motor365_daily_log_energy", "energy BETWEEN 1 AND 5");
            table.HasCheckConstraint("ck_motor365_daily_log_pain", "pain BETWEEN 0 AND 10");
            table.HasCheckConstraint("ck_motor365_daily_log_weight", "weight_kg > 0 AND weight_kg <= 1000");
            table.HasCheckConstraint("ck_motor365_daily_log_waist", "waist_cm > 0 AND waist_cm <= 500");
        });

        builder.HasKey(x => new { x.PersonId, x.Day });
        builder.Property(x => x.PersonId).HasColumnName("person_id");
        builder.Property(x => x.Day).HasColumnName("day");
        builder.Property(x => x.LogDate).HasColumnName("log_date");
        builder.Property(x => x.Adherence).HasColumnName("adherence").HasPrecision(5, 4);
        builder.Property(x => x.SleepHours).HasColumnName("sleep_hours").HasPrecision(4, 2);
        builder.Property(x => x.Energy).HasColumnName("energy");
        builder.Property(x => x.Pain).HasColumnName("pain");
        builder.Property(x => x.Symptoms).HasColumnName("symptoms").HasColumnType("text[]");
        builder.Property(x => x.WeightKg).HasColumnName("weight_kg").HasPrecision(6, 2);
        builder.Property(x => x.WaistCm).HasColumnName("waist_cm").HasPrecision(5, 2);
        builder.Property(x => x.CompletedExercise).HasColumnName("completed_exercise");

        builder.HasOne<Motor365Person>().WithMany().HasForeignKey(x => x.PersonId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.PersonId, x.LogDate }).IsUnique();
        builder.HasIndex(x => x.LogDate);
    }
}

internal sealed class Motor365EngineParamsConfiguration : IEntityTypeConfiguration<Motor365EngineParams>
{
    public void Configure(EntityTypeBuilder<Motor365EngineParams> builder)
    {
        builder.ToTable("engine_params", "app", table =>
        {
            table.HasCheckConstraint("ck_motor365_engine_params_version", "version > 0 AND btrim(engine_ver) <> ''");
            table.HasCheckConstraint("ck_motor365_engine_params_parameters", "jsonb_typeof(parameters_json) = 'object'");
            table.HasCheckConstraint("ck_motor365_engine_params_reason", "btrim(reason) <> ''");
        });

        builder.HasKey(x => x.Version);
        builder.Property(x => x.Version).HasColumnName("version").ValueGeneratedNever();
        builder.Property(x => x.EngineVer).HasColumnName("engine_ver").HasMaxLength(32);
        builder.Property(x => x.ParametersJson).HasColumnName("parameters_json").HasColumnType("jsonb");
        builder.Property(x => x.ChangedBy).HasColumnName("changed_by");
        builder.Property(x => x.Reason).HasColumnName("reason");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");

        builder.HasAlternateKey(x => new { x.Version, x.EngineVer });
        builder.HasIndex(x => x.CreatedAt);
    }
}

internal sealed class Motor365PlanSnapshotConfiguration : IEntityTypeConfiguration<Motor365PlanSnapshot>
{
    public void Configure(EntityTypeBuilder<Motor365PlanSnapshot> builder)
    {
        builder.ToTable("plan_snapshot", "app", table =>
        {
            table.HasCheckConstraint("ck_motor365_plan_snapshot_version", "plan_version > 0");
            table.HasCheckConstraint("ck_motor365_plan_snapshot_plan", "jsonb_typeof(plan_json) = 'object'");
            table.HasCheckConstraint("ck_motor365_plan_snapshot_screening", "screening_complete");
        });

        builder.HasKey(x => new { x.PersonId, x.PlanVersion, x.ParamsVersion });
        builder.Property(x => x.PersonId).HasColumnName("person_id");
        builder.Property(x => x.PlanVersion).HasColumnName("plan_version");
        builder.Property(x => x.ParamsVersion).HasColumnName("params_version");
        builder.Property(x => x.SubmissionId).HasColumnName("submission_id");
        builder.Property(x => x.EngineVer).HasColumnName("engine_ver").HasMaxLength(32);
        builder.Property(x => x.PlanJson).HasColumnName("plan_json").HasColumnType("jsonb");
        builder.Property(x => x.GeneratedAt).HasColumnName("generated_at").HasDefaultValueSql("now()");
        builder.Property<bool>("ScreeningComplete").HasColumnName("screening_complete").HasDefaultValue(true)
            .Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);

        builder.HasOne<Motor365Person>().WithMany().HasForeignKey(x => x.PersonId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Motor365EngineParams>().WithMany()
            .HasForeignKey(x => new { x.ParamsVersion, x.EngineVer })
            .HasPrincipalKey(x => new { x.Version, x.EngineVer }).OnDelete(DeleteBehavior.Restrict);
        // El FK de completitud usa un índice único sobre una columna generada de PostgreSQL.
        builder.HasIndex(x => new { x.PersonId, x.SubmissionId });
        builder.HasIndex(x => x.GeneratedAt);
    }
}

internal sealed class Motor365EngineEventConfiguration : IEntityTypeConfiguration<Motor365EngineEvent>
{
    public void Configure(EntityTypeBuilder<Motor365EngineEvent> builder)
    {
        builder.ToTable("engine_event", "app", table =>
        {
            table.HasCheckConstraint("ck_motor365_engine_event_day", "day BETWEEN 1 AND 365");
            table.HasCheckConstraint("ck_motor365_engine_event_version", "plan_version > 0");
            table.HasCheckConstraint("ck_motor365_engine_event_kind", "kind IN ('meta', 'modo', 'sin', 'ajuste')");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.PersonId).HasColumnName("person_id");
        builder.Property(x => x.Day).HasColumnName("day");
        builder.Property(x => x.PlanVersion).HasColumnName("plan_version");
        builder.Property(x => x.ParamsVersion).HasColumnName("params_version");
        builder.Property(x => x.EngineVer).HasColumnName("engine_ver").HasMaxLength(32);
        builder.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(12);
        builder.Property(x => x.CalorieDelta).HasColumnName("calorie_delta").HasPrecision(8, 2);
        builder.Property(x => x.RatePercent).HasColumnName("rate_percent").HasPrecision(10, 2);
        builder.Property(x => x.MessageEs).HasColumnName("message_es");
        builder.Property(x => x.MessageEn).HasColumnName("message_en");
        builder.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasDefaultValueSql("now()");

        builder.HasOne<Motor365Person>().WithMany().HasForeignKey(x => x.PersonId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Motor365EngineParams>().WithMany()
            .HasForeignKey(x => new { x.ParamsVersion, x.EngineVer })
            .HasPrincipalKey(x => new { x.Version, x.EngineVer }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.PersonId, x.Day });
        builder.HasIndex(x => x.OccurredAt);
    }
}

internal sealed class Motor365AuditLogConfiguration : IEntityTypeConfiguration<Motor365AuditLog>
{
    public void Configure(EntityTypeBuilder<Motor365AuditLog> builder)
    {
        builder.ToTable("audit_log", "app", table =>
        {
            table.HasCheckConstraint("ck_motor365_audit_log_action", "btrim(action) <> ''");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.PersonId).HasColumnName("person_id");
        builder.Property(x => x.ActorUserId).HasColumnName("actor_user_id").HasDefaultValueSql("NULLIF(current_setting('audit.user_id', true), '')::uuid");
        builder.Property(x => x.Action).HasColumnName("action").HasMaxLength(80);
        builder.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasDefaultValueSql("now()");

        builder.HasOne<Motor365Person>().WithMany().HasForeignKey(x => x.PersonId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.PersonId, x.OccurredAt });
        builder.HasIndex(x => x.OccurredAt);
    }
}

internal sealed class Motor365ProfessionalReviewConfiguration : IEntityTypeConfiguration<Motor365ProfessionalReview>
{
    public void Configure(EntityTypeBuilder<Motor365ProfessionalReview> builder)
    {
        builder.ToTable("professional_review", "app", table =>
        {
            table.HasCheckConstraint("ck_motor365_professional_review_decision", "decision IN ('Approved', 'Rejected', 'NeedsChanges')");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.PersonId).HasColumnName("person_id");
        builder.Property(x => x.SubmissionId).HasColumnName("submission_id");
        builder.Property(x => x.ProfessionalId).HasColumnName("professional_id");
        builder.Property(x => x.Decision).HasColumnName("decision").HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.Notes).HasColumnName("notes");
        builder.Property(x => x.ReviewedAt).HasColumnName("reviewed_at").HasDefaultValueSql("now()");

        builder.HasOne<Motor365Screening>().WithMany()
            .HasForeignKey(x => new { x.PersonId, x.SubmissionId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Professional>().WithMany().HasForeignKey(x => x.ProfessionalId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.PersonId, x.ReviewedAt });
    }
}
