using System.Security.Cryptography;
using CoppAddresd.Auth.Data;
using CoppAddresd.Auth.Entities;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Application.Features.Medications;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using CoppAddresd.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
try
{
    if (Environment.GetEnvironmentVariable("COPP_MEDICATION_DEMO_CONFIRM") != "SYNTHETIC_ACCOUNT_ONLY") throw new InvalidOperationException("DEMO_DISABLED");
    var production = args.Contains("--production");
    if (!production && !args.Contains("--isolated-test")) throw new InvalidOperationException("EXPLICIT_MODE_REQUIRED");
    var connection = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection") ?? throw new InvalidOperationException("CONNECTION_REQUIRED");
    var target = new Npgsql.NpgsqlConnectionStringBuilder(connection);
    if (production && (target.Database != "coppaddresd" || target.Host?.EndsWith(".rds.amazonaws.com", StringComparison.OrdinalIgnoreCase) != true)) throw new InvalidOperationException("PRODUCTION_TARGET_MISMATCH");
    if (!production && (!(target.Host == "127.0.0.1" || target.Host == "localhost") || target.Database?.StartsWith("copp_review_medications_") != true)) throw new InvalidOperationException("ISOLATED_TARGET_REQUIRED");
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { { "Medications:Enabled", "true" } }).Build();
    await using var auth = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(connection).Options);
    await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
    var userId = MedicationReminderTestScope.UserId; var patientId = MedicationReminderTestScope.PatientId;
    var password = "Demo!" + Convert.ToHexString(HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(connection), System.Text.Encoding.UTF8.GetBytes("copp:medication-demo:" + userId))).ToLowerInvariant()[..24];
    var state = new DemoState(userId, patientId, "900006702", "medication.demo.20261118@copp.test", password);
    // Refuse every collision before creating or changing any row. Existing passwords are never reset.
    if (await auth.Users.AnyAsync(u => (u.NormalizedEmail == state.Email.ToUpperInvariant() && u.Id != userId) || (u.Id == userId && u.NormalizedEmail != state.Email.ToUpperInvariant()))) throw new InvalidOperationException("IDENTITY_COLLISION");
    if (await db.PatientProfiles.AnyAsync(p => (p.DocumentNumber == state.Document && p.Id != patientId) || (p.Id == patientId && (p.DocumentNumber != state.Document || p.UserId != userId)))) throw new InvalidOperationException("PATIENT_COLLISION");
    var selectedTemplate = await db.ProgramTemplates.Where(t => t.Status == TemplateStatus.Active).OrderByDescending(t => t.Code == "default-83w").FirstOrDefaultAsync() ?? throw new InvalidOperationException("NO_ACTIVE_TEMPLATE");
    var user = await auth.Users.SingleOrDefaultAsync(u => u.Id == state.UserId);
    if (user is null)
    {
        if (await auth.Users.AnyAsync(u => u.NormalizedEmail == state.Email.ToUpperInvariant())) throw new Exception("Synthetic email collision; no existing account modified.");
        user = new ApplicationUser { Id = state.UserId, Email = state.Email, UserName = state.Email, NormalizedEmail = state.Email.ToUpperInvariant(), NormalizedUserName = state.Email.ToUpperInvariant(), FirstName = "Demo", LastName = "Medicamentos PROD", EmailConfirmed = true, IsActive = true, SecurityStamp = Guid.NewGuid().ToString(), ConcurrencyStamp = Guid.NewGuid().ToString() };
        user.PasswordHash = new PasswordHasher<ApplicationUser>().HashPassword(user, state.Password);
        auth.Users.Add(user); await auth.SaveChangesAsync();
    }
    var app = await auth.Applications.SingleAsync(a => a.Code == "app");
    if (!await auth.UserApplications.AnyAsync(a => a.UserId == user.Id && a.ApplicationId == app.Id)) auth.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = app.Id });
    var role = await auth.Roles.FirstOrDefaultAsync(r => r.NormalizedName == "PATIENT");
    if (role != null && !await auth.UserRoles.AnyAsync(r => r.UserId == user.Id && r.RoleId == role.Id)) auth.UserRoles.Add(new IdentityUserRole<Guid> { UserId = user.Id, RoleId = role.Id });
    await auth.SaveChangesAsync();
    if (!await db.PatientProfiles.AnyAsync(p => p.Id == state.PatientId))
    {
        if (await db.PatientProfiles.AnyAsync(p => p.DocumentNumber == state.Document)) throw new Exception("Synthetic document collision; no existing patient modified.");
        db.PatientProfiles.Add(new PatientProfile { Id = state.PatientId, UserId = user.Id, FirstName = "Demo", LastName = "Medicamentos PROD", MedicalRecordNumber = "DEMO-MED-PROD-20261118", DocumentNumber = state.Document, Email = state.Email, Status = "Activo", Timezone = "America/Bogota", EmergencyContact = "{}" }); await db.SaveChangesAsync();
    }
    var store = new MedicationControlStore(db, null!, TimeProvider.System);
    var access = await store.GetAccessAsync(state.PatientId, default);
    if (!access.Enabled) await store.SetAccessAsync(state.PatientId, user.Id, new(true, access.Revision), default);
    var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"); var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
    foreach (var (name, kind, times) in new[] { ("Medicamento DEMO — no usar", "medication", new[] { "00:01", "00:02" }), ("Nutracéutico DEMO — no usar", "supplement", new[] { "00:03" }) })
    {
        if (!await db.MedicationTreatments.AnyAsync(t => t.PatientId == state.PatientId && t.Name == name)) await store.CreateAsync(state.PatientId, user.Id, "patient_reported", new(Guid.NewGuid(), null, name, kind, 1, "unidad", null, "Datos ficticios para probar la aplicación; no son una indicación de uso.", times, zone.Id, today.AddDays(-7), null), default);
    }
    var workspace = await store.WorkspaceAsync(state.PatientId, today, zone.Id, true, default);
    var dose = workspace.Doses.FirstOrDefault(d => d.Name.StartsWith("Medicamento") && d.Status == "pending");
    if (dose != null && !workspace.Doses.Any(d => d.Status == "taken")) await store.RecordAsync(state.PatientId, user.Id, "patient_reported", dose.Id, new(Guid.NewGuid(), "taken", DateTimeOffset.UtcNow, null, dose.Revision, dose.ScheduledAt), default);
    var repo = new ProgramRepository(db, config);
    if (!await db.ProgramEnrollments.AnyAsync(e => e.PatientId == state.PatientId))
    {
        await repo.EnrollAsync(state.PatientId, selectedTemplate.Id, zone.Id, today, user.Id);
    }
    Console.WriteLine($"DEMO_READY document={state.Document} patient={state.PatientId}; synthetic identity, program and treatments ready; no existing passwords changed.");
}
catch (Exception error) { var guards = new[] { "DEMO_DISABLED", "EXPLICIT_MODE_REQUIRED", "CONNECTION_REQUIRED", "PRODUCTION_TARGET_MISMATCH", "ISOLATED_TARGET_REQUIRED", "IDENTITY_COLLISION", "PATIENT_COLLISION", "NO_ACTIVE_TEMPLATE" };
    var safeCode = guards.Contains(error.Message) ? error.Message : error is Npgsql.PostgresException postgres ? "POSTGRES_" + postgres.SqlState : error.GetType().Name;
    Console.Error.WriteLine("DEMO_FAILED: " + safeCode); Environment.ExitCode = 1; }
record DemoState(Guid UserId, Guid PatientId, string Document, string Email, string Password);
