using System.Text.Json;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.ProgramProgress;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    private static PatientAiBrief Brief(Guid patientId, string kind = "patient_summary") => new()
    {
        PatientId = patientId, Kind = kind,
        DraftJson = "{\"draft\":{\"es\":\"Revisar registros\",\"en\":\"Review records\"},\"sourceRefs\":[]}",
        ModelName = "modelo-qa", GeneratedAt = new(2026, 11, 15, 15, 0, 0, DateTimeKind.Utc),
        InputHash = new string('a', 64),
    };

    [NutritionPostgresFact]
    public async Task PatientAiBrief_SinInscripcion_ConservaTiposHistorialYOrdenPorPaciente()
    {
        await using var db = Context();
        var patient = await PatientAsync(db); var other = await PatientAsync(db);
        var first = Brief(patient.Id);
        first.Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Brief(patient.Id);
        second.Id = Guid.Parse("00000000-0000-0000-0000-000000000002");
        second.ModelName = null;
        db.PatientAiBriefs.AddRange(first, second, Brief(patient.Id, "weekly_plan_suggestions"), Brief(other.Id));
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();

        var history = await db.PatientAiBriefs.AsNoTracking().Where(x => x.PatientId == patient.Id && x.Kind == "patient_summary")
            .OrderByDescending(x => x.GeneratedAt).ThenByDescending(x => x.Id).ToListAsync();
        Assert.Equal(new[] { second.Id, first.Id }, history.Select(x => x.Id));
        Assert.All(history, x =>
        {
            Assert.Equal("draft", x.State); Assert.Equal(first.InputHash, x.InputHash);
            Assert.Equal(DateTimeKind.Utc, x.GeneratedAt.Kind);
            using var draft = JsonDocument.Parse(x.DraftJson);
            Assert.Equal("Revisar registros", draft.RootElement.GetProperty("draft").GetProperty("es").GetString());
        });
        Assert.Null(history[0].ModelName); Assert.Equal("modelo-qa", history[1].ModelName);
        Assert.Equal(3, await db.PatientAiBriefs.CountAsync(x => x.PatientId == patient.Id));
        Assert.False(await db.ProgramEnrollments.AnyAsync(x => x.PatientId == patient.Id));
        Assert.False(await db.ProgramAssignmentRecommendations.AnyAsync(x => x.PatientId == patient.Id));

        // Volumen sintético para validar el acceso acotado al historial, incluido el índice de la FK.
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO app.patient_ai_briefs (id, patient_id, kind, draft_json, generated_at, input_hash)
            SELECT gen_random_uuid(), {{patient.Id}}, 'patient_summary', '{}'::jsonb,
                now() - n * interval '1 minute', repeat('b', 64) FROM generate_series(1, 4000) AS n;
            """);
        await db.Database.ExecuteSqlRawAsync("ANALYZE app.patient_ai_briefs");
        var plan = await db.Database.SqlQueryRaw<string>("""
            EXPLAIN (ANALYZE, BUFFERS) SELECT id FROM app.patient_ai_briefs
            WHERE patient_id = @patient AND kind = 'patient_summary'
            ORDER BY generated_at DESC, id DESC LIMIT 10
            """, new NpgsqlParameter("patient", patient.Id)).ToListAsync();
        Assert.Contains(plan, line => line.Contains("ix_patient_ai_briefs_patient_kind_generated", StringComparison.Ordinal));
    }

    [NutritionPostgresFact]
    public async Task PatientAiBrief_Restricciones_RechazaPacienteInexistenteYMetadatosInvalidos()
    {
        await using var db = Context(); var patient = await PatientAsync(db);
        var invalid = new (Action<PatientAiBrief> Change, string SqlState)[]
        {
            (x => x.PatientId = Guid.NewGuid(), PostgresErrorCodes.ForeignKeyViolation),
            (x => x.Kind = "assignment", PostgresErrorCodes.CheckViolation),
            (x => x.State = "published", PostgresErrorCodes.CheckViolation),
            (x => x.InputHash = new string('A', 64), PostgresErrorCodes.CheckViolation),
            (x => x.InputHash = "incompleto", PostgresErrorCodes.CheckViolation),
            (x => x.DraftJson = "[]", PostgresErrorCodes.CheckViolation),
            (x => x.DraftJson = "null", PostgresErrorCodes.CheckViolation),
            (x => x.ModelName = "  ", PostgresErrorCodes.CheckViolation),
        };
        foreach (var (change, expected) in invalid)
        {
            await using var attempt = Context(); var brief = Brief(patient.Id); change(brief);
            attempt.PatientAiBriefs.Add(brief);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => attempt.SaveChangesAsync());
            Assert.Equal(expected, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
        Assert.Empty(await db.PatientAiBriefs.ToListAsync());
        var applied = Brief(patient.Id, "weekly_plan_suggestions"); applied.State = "applied";
        var dismissed = Brief(patient.Id); dismissed.State = "dismissed";
        db.PatientAiBriefs.AddRange(applied, dismissed); await db.SaveChangesAsync();
        var restricted = await Assert.ThrowsAsync<PostgresException>(() => db.PatientProfiles.Where(x => x.Id == patient.Id).ExecuteDeleteAsync());
        Assert.Equal(PostgresErrorCodes.RestrictViolation, restricted.SqlState);
        Assert.Equal("FK_patient_ai_briefs_patient_profiles_patient_id", restricted.ConstraintName);
    }

    [NutritionPostgresFact]
    public async Task PatientAiBrief_Migracion_DownUpConservaNarrativaYPropuestaExistentes()
    {
        var seed = await CompletionSeedAsync(); await using var db = Context();
        var recommendation = new ProgramAssignmentRecommendation
        {
            Id = Guid.NewGuid(), PatientId = seed.PatientId, EnrollmentId = seed.EnrollmentId,
            ProfileSnapshot = "{\"conditions\":[]}", Proposal = "{\"matchedRules\":[]}", EngineVersion = "qa-4.3",
            NarrativeEs = "Narrativa anterior", NarrativeEn = "Previous narrative", ModelName = "modelo-anterior",
        };
        db.ProgramAssignmentRecommendations.Add(recommendation); db.PatientAiBriefs.Add(Brief(seed.PatientId));
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before = await db.ProgramAssignmentRecommendations.AsNoTracking().SingleAsync();
        var migrations = db.Database.GetMigrations().ToArray();
        var briefMigration = Array.FindIndex(migrations, id => id.EndsWith("_AddPatientAiBriefs", StringComparison.Ordinal));
        Assert.True(briefMigration > 0);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(migrations[briefMigration - 1]);
        Assert.Null(await db.Database.SqlQueryRaw<string>("SELECT to_regclass('app.patient_ai_briefs')::text AS \"Value\"").SingleAsync());
        Assert.Equal("Narrativa anterior", (await db.ProgramAssignmentRecommendations.AsNoTracking().SingleAsync()).NarrativeEs);
        await migrator.MigrateAsync();
        var preserved = await db.ProgramAssignmentRecommendations.AsNoTracking().SingleAsync();
        Assert.Equal(recommendation.NarrativeEn, preserved.NarrativeEn); Assert.Equal(recommendation.ModelName, preserved.ModelName);
        Assert.Equal(before.ProfileSnapshot, preserved.ProfileSnapshot); Assert.Equal(before.Proposal, preserved.Proposal);
        Assert.Equal(recommendation.State, preserved.State);
        Assert.Empty(await db.PatientAiBriefs.ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
