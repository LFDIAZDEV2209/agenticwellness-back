extern alias ApiEntry;

using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using CoppAddresd.Auth.Data;
using CoppAddresd.Auth.Entities;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using AuthApplication = CoppAddresd.Auth.Entities.Application;

// Host exclusivo de QA: API + Identity/refresh reales, PostgreSQL local aislado.
// Nunca se cargan configuraciones privadas ni se habilitan transportes externos.
var dbName = Environment.GetEnvironmentVariable("COP_MEDICATION_DATABASE") ?? "copp_review_medications_20261117";
var connection = new NpgsqlConnectionStringBuilder
{
    Host = "127.0.0.1", Port = 55437, Database = dbName, Username = "app_user",
    IncludeErrorDetail = false,
};
if (!dbName.StartsWith("copp_review_medications_", StringComparison.Ordinal))
    throw new InvalidOperationException("Solo se admiten bases locales copp_review_medications_.");
var output = Path.GetFullPath(Environment.GetEnvironmentVariable("COP_E2E_OUTPUT") ?? "/tmp/copp-medication-e2e");
if (!output.StartsWith(Path.GetTempPath(), StringComparison.Ordinal))
    throw new InvalidOperationException("La evidencia debe estar fuera de los repositorios.");
Directory.CreateDirectory(output);
if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
var password = "Qa!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)) + "a9";
var settings = new Dictionary<string, string?>
{
    ["ConnectionStrings:DefaultConnection"] = connection.ConnectionString,
    ["Jwt:Secret"] = key, ["Jwt:Issuer"] = "CoppMedicationsE2E",
    ["Jwt:ValidAudiences:0"] = "app", ["Jwt:ValidAudiences:1"] = "erp",
    ["Jwt:AccessTokenExpirationMinutes"] = "60",
    ["Jwt:RefreshTokenExpirationDays"] = "1",
    ["Storage:SignatureKey"] = key, ["Storage:Provider"] = "Local",
    ["Storage:Local:RootPath"] = Path.Combine(output, "storage"),
    ["Cache:Provider"] = "Memory", ["Medications:Enabled"] = "true", ["Medications:MonitoringWorkerEnabled"] = "false",
    ["Auth:InternalApiKey"] = key, ["AuthService:InternalApiKey"] = key,
    ["AuthService:BaseUrl"] = "http://127.0.0.1:5163",
    ["Cors:Origins"] = "http://localhost:3100,http://127.0.0.1:3100,http://localhost:5188,http://127.0.0.1:5188,http://localhost:5160,http://127.0.0.1:5160",
    ["SkipDatabaseInitialization"] = "true",
    ["Sms:Provider"] = "Noop", ["Sms:IsEnabled"] = "false",
    ["Voice:Provider"] = "Noop", ["Voice:IsEnabled"] = "false",
    ["Twilio:IsEnabled"] = "false", ["Email:Provider"] = "Log", ["Fcm:Enabled"] = "false",
    ["AiService:BaseUrl"] = "http://127.0.0.1:5199",
    ["FoodAi:BaseUrl"] = "http://127.0.0.1:5199",
    ["Community:BaseUrl"] = "http://127.0.0.1:5199",
    ["ProgramAssignment:Enabled"] = "false",
    ["Logging:LogLevel:Default"] = "Warning",
    ["Logging:LogLevel:Microsoft"] = "Warning",
};
Environment.SetEnvironmentVariable("WHATSAPP_ENABLED", "false");
Environment.SetEnvironmentVariable("SosWhatsApp__Enabled", "false");
// Los hosts mínimos validan configuración antes del callback de TestServer.
// Solo afecta a este proceso de QA y sus claves efímeras.
foreach (var setting in settings)
    Environment.SetEnvironmentVariable(setting.Key.Replace(":", "__", StringComparison.Ordinal), setting.Value);

await using var auth = new AuthFactory(settings);
auth.UseKestrel(options => options.ListenLocalhost(5163));
using var authClient = auth.CreateClient();
await using (var scope = auth.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    await db.Database.MigrateAsync();
}

var clock = new QaClock();
await using var api = new ApiFactory(settings, clock);
api.UseKestrel(options => options.ListenLocalhost(5162));
using var apiClient = api.CreateClient();
await using (var scope = api.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

var accounts = await SeedAsync(auth.Services, api.Services, password);
var fixture = new
{
    database = dbName, container = "copp-medications-e2e", gateway = "http://127.0.0.1:5160",
    patientId = Id("101"), otherPatientId = Id("102"), clinicId = Id("201"), foreignClinicId = Id("202"),
    legacyMedicationId = Id("401"), legacyPatientMedicationId = Id("402"),
    accounts,
    appleBaseline = new { version = "1.0", build = 67, commit = "c542af2e0aa60644b833496debfbe8918f93d603", binaryExecutionVerified = false },
};
var fixturePath = Path.Combine(output, "fixture.private.json");
await File.WriteAllTextAsync(fixturePath, JsonSerializer.Serialize(fixture, new JsonSerializerOptions { WriteIndented = true }));
if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fixturePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

// Relay de transporte únicamente: conserva contratos y cookies de Auth/API.
// No implementa ni simula respuestas clínicas/de autenticación.
var relayBuilder = WebApplication.CreateBuilder();
relayBuilder.Logging.ClearProviders();
relayBuilder.WebHost.UseUrls("http://127.0.0.1:5160");
var relay = relayBuilder.Build();
using var forward = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
relay.Run(async context =>
{
    // Único control de infraestructura de QA: reloj inyectado de la API.
    // No sustituye repositorios, autenticación, autorización ni respuestas de negocio.
    if (context.Request.Path == "/__qa/clock" && context.Request.Method == "POST")
    {
        var value = await context.Request.ReadFromJsonAsync<QaClockRequest>();
        clock.Set(value?.UtcNow);
        await context.Response.WriteAsJsonAsync(new { utcNow = clock.GetUtcNow() });
        return;
    }
    if (context.Request.Path == "/__qa/medications/refresh" && context.Request.Method == "POST")
    {
        await using var scope = api.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<CoppAddresd.Application.Features.Medications.IMedicationMonitoringStore>();
        var patients = await store.MonitoredPatientsAsync(null, 50, context.RequestAborted);
        foreach (var patient in patients) await store.RefreshAlertsAsync(patient, context.RequestAborted);
        if (patients.Count > 0) await store.MonitoredPatientsAsync(patients[^1], 50, context.RequestAborted);
        await context.Response.WriteAsJsonAsync(new { refreshed = true, count = patients.Count }); return;
    }
    var port = context.Request.Path.StartsWithSegments("/api/auth") ? 5163 : 5162;
    using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
        $"http://127.0.0.1:{port}{context.Request.Path}{context.Request.QueryString}");
    if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        request.Content = new StreamContent(context.Request.Body);
    foreach (var header in context.Request.Headers)
    {
        if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
        if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
    }
    using var response = await forward.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
    context.Response.StatusCode = (int)response.StatusCode;
    foreach (var header in response.Headers.Concat(response.Content.Headers))
        if (!header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            && !header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            context.Response.Headers[header.Key] = header.Value.ToArray();
    await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
});
Console.WriteLine("Medication E2E ready: real API5162/Auth5163/PostgreSQL55437; relay5160; private fixture created.");
await relay.RunAsync();

static Guid Id(string suffix) => Guid.Parse("00000000-0000-4000-8000-" + suffix.PadLeft(12, '0'));

static async Task<Dictionary<string, object>> SeedAsync(IServiceProvider authServices, IServiceProvider apiServices, string password)
{
    var scopedClinicId = Id("201");
    var scopedProfessionalId = Id("303");
    string[] permissionCodes = ["Medications.View", "Medications.Manage", "Medications.ManageAccess", "Medications.Export", "Audit.View", "Patients.View", "Patients.ViewOwn", "Patients.ViewAll", "Patients.Update", "Program.Profile360View", "Nutrition.View", "System.AdminSettings", "Organizations.View", "Clinics.View"];
    var specs = new[]
    {
        new { Code = "patient", Id = Id("1"), Email = "medication.patient@copp.test", Document = "900000067" },
        new { Code = "otherPatient", Id = Id("2"), Email = "medication.other@copp.test", Document = "900000068" },
        new { Code = "manager", Id = Id("3"), Email = "medication.manager@copp.test", Document = "" },
        new { Code = "reader", Id = Id("4"), Email = "medication.reader@copp.test", Document = "" },
        new { Code = "foreign", Id = Id("5"), Email = "medication.foreign@copp.test", Document = "" },
        new { Code = "scoped", Id = Id("6"), Email = "medication.scoped@copp.test", Document = "" },
    };
    await using (var scope = authServices.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        if (!await roles.RoleExistsAsync("Admin")) await roles.CreateAsync(new ApplicationRole { Id = Id("11"), Name = "Admin", IsSystem = true });
        foreach (var code in permissionCodes)
            if (!await db.Permissions.AnyAsync(x => x.Code == code))
                db.Permissions.Add(new Permission { Id = Guid.NewGuid(), Code = code, Name = code, Module = code.Split('.')[0] });
        foreach (var code in new[] { "app", "erp" })
            if (!await db.Applications.AnyAsync(x => x.Code == code)) db.Applications.Add(new AuthApplication { Id = code == "app" ? Id("21") : Id("22"), Code = code, Name = "QA " + code });
        await db.SaveChangesAsync();
        foreach (var spec in specs)
        {
            var user = await users.FindByIdAsync(spec.Id.ToString());
            if (user is null)
            {
                user = new ApplicationUser { Id = spec.Id, Email = spec.Email, UserName = spec.Email, FirstName = "Medication", LastName = "QA " + spec.Code, EmailConfirmed = true, IsActive = true };
                var result = await users.CreateAsync(user, password);
                if (!result.Succeeded) throw new InvalidOperationException("QA identity could not be created.");
            }
            else
            {
                user.PasswordHash = users.PasswordHasher.HashPassword(user, password);
                await users.UpdateAsync(user);
            }
            var appId = spec.Code is "patient" or "otherPatient" ? Id("21") : Id("22");
            if (!await db.UserApplications.AnyAsync(x => x.UserId == user.Id && x.ApplicationId == appId))
                db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = appId });
            if (spec.Code == "manager" && !await users.IsInRoleAsync(user, "Admin")) await users.AddToRoleAsync(user, "Admin");
            var grants = spec.Code == "manager" ? permissionCodes : spec.Code is "reader" or "foreign" ? ["Medications.View", "Patients.ViewOwn", "Program.Profile360View"] : Array.Empty<string>();
            foreach (var permission in await db.Permissions.Where(x => grants.Contains(x.Code)).ToListAsync())
                if (!await db.UserPermissions.AnyAsync(x => x.UserId == user.Id && x.PermissionId == permission.Id))
                    db.UserPermissions.Add(new UserPermission { UserId = user.Id, PermissionId = permission.Id });
            if (spec.Code == "scoped")
                foreach (var permission in await db.Permissions.Where(x => x.Code == "Medications.View" || x.Code == "Medications.Manage").ToListAsync())
                    if (!await db.ScopedPermissionAssignments.AnyAsync(x => x.UserId == user.Id && x.PermissionId == permission.Id && x.ScopeType == "Clinic" && x.ScopeId == scopedClinicId))
                        db.ScopedPermissionAssignments.Add(new ScopedPermissionAssignment { Id = Guid.NewGuid(), UserId = user.Id, PermissionId = permission.Id, ScopeType = "Clinic", ScopeId = Id("201"), Effect = "Grant", GrantedBy = Id("3") });
            await db.SaveChangesAsync();
        }
    }
    await using (var scope = apiServices.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organizationId = Id("200");
        if (!await db.Organizations.AnyAsync(x => x.Id == organizationId))
        {
            db.Organizations.Add(new Organization { Id = Id("200"), Code = "medication-qa", Name = "Medication QA Organization" });
            db.Clinics.AddRange(new Clinic { Id = Id("201"), OrganizationId = Id("200"), Name = "Medication QA A", Code = "med-a" }, new Clinic { Id = Id("202"), OrganizationId = Id("200"), Name = "Medication QA B", Code = "med-b" });
            foreach (var (userId, patientId, clinicId, suffix) in new[] { (Id("1"), Id("101"), Id("201"), "067"), (Id("2"), Id("102"), Id("202"), "068") })
                db.PatientProfiles.Add(new PatientProfile { Id = patientId, UserId = userId, ClinicId = clinicId, FirstName = "Paciente", LastName = "Medicamentos QA " + suffix, MedicalRecordNumber = "QA-MED-" + suffix, DocumentNumber = "900000" + suffix, Email = "medication.patient" + suffix + "@copp.test", PhoneCountryCode = "1", PhoneNumber = "2025550101", Status = "Activo", Timezone = "America/Bogota", EmergencyContact = JsonSerializer.Serialize(new { name = "Contacto QA", relationship = "Familiar", phone = "+12025550101", email = "contact@copp.test" }) });
            foreach (var (userId, professionalId, patientId, clinicId) in new[] { (Id("4"), Id("301"), Id("101"), Id("201")), (Id("5"), Id("302"), Id("102"), Id("202")) })
            {
                var employeeId = Guid.NewGuid();
                db.Employees.Add(new Employee { Id = employeeId, UserId = userId, OrganizationId = Id("200"), FirstName = "Profesional", LastName = "QA", Email = userId.ToString("N") + "@copp.test", Status = "Active" });
                db.Professionals.Add(new Professional { Id = professionalId, EmployeeId = employeeId });
                db.EmployeeClinics.Add(new EmployeeClinic { EmployeeId = employeeId, ClinicId = clinicId, IsPrimary = true });
                db.PatientProfessionalAssignments.Add(new PatientProfessionalAssignment { PatientId = patientId, ProfessionalId = professionalId, ClinicId = clinicId, Status = "Active", CreatedBy = Id("3") });
            }
            db.Medications.Add(new Medication { Id = Id("401"), Name = "Medicamento histórico QA" });
            db.PatientMedications.Add(new PatientMedication { Id = Id("402"), PatientId = Id("101"), MedicationId = Id("401"), Frequency = "Según indicación previa", SortOrder = 0 });
            await db.SaveChangesAsync();
        }
    }
    await using (var scope = apiServices.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!await db.Professionals.AnyAsync(x => x.Id == scopedProfessionalId))
        {
            var employeeId = Guid.NewGuid();
            db.Employees.Add(new Employee { Id = employeeId, UserId = Id("6"), OrganizationId = Id("200"), FirstName = "Profesional", LastName = "Scoped QA", Email = "scoped.employee@copp.test", Status = "Active" });
            db.Professionals.Add(new Professional { Id = Id("303"), EmployeeId = employeeId });
            db.EmployeeClinics.Add(new EmployeeClinic { EmployeeId = employeeId, ClinicId = Id("201"), IsPrimary = true });
            db.PatientProfessionalAssignments.Add(new PatientProfessionalAssignment { PatientId = Id("101"), ProfessionalId = Id("303"), ClinicId = Id("201"), Status = "Active", CreatedBy = Id("3") });
            await db.SaveChangesAsync();
        }
        Guid[] qaProfessionalIds = [Id("301"), Id("302"), Id("303")];
        await db.Professionals.Where(x => qaProfessionalIds.Contains(x.Id) && x.OnboardingCompletedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.OnboardingCompletedAt, DateTime.UtcNow));
        // Programa mínimo sintético para comprobar el mismo acceso en el protocolo diario.
        var templateId = Id("501");
        if (!await db.ProgramTemplates.AnyAsync(x => x.Id == templateId))
        {
            db.ProgramTemplates.Add(new ProgramTemplate
            {
                Id = templateId, Code = "medication-access-e2e", Name = "Medication access QA",
                TotalWeeks = 1, Status = TemplateStatus.Active,
            });
            foreach (var weekday in Enumerable.Range(1, 7))
                foreach (var task in new[] { TaskCode.emocional, TaskCode.nutraceutico })
                    db.WeeklyDayTemplates.Add(new WeeklyDayTemplate
                    {
                        Id = Guid.NewGuid(), TemplateId = templateId, Weekday = (short)weekday,
                        TaskCode = task, Points = 80, SortOrder = task == TaskCode.emocional ? 0 : 1,
                    });
            await db.SaveChangesAsync();
        }
        var programPatientId = Id("101");
        if (!await db.ProgramEnrollments.AnyAsync(x => x.PatientId == programPatientId))
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Medications:Enabled"] = "true" }).Build();
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById("America/Bogota")));
            await new ProgramRepository(db, configuration).EnrollAsync(programPatientId, templateId, "America/Bogota", today, Id("3"));
        }
    }
    return specs.ToDictionary(x => x.Code, x => (object)new { userId = x.Id, login = x.Email, documentNumber = x.Document, password });
}

sealed class AuthFactory(IReadOnlyDictionary<string, string?> settings) : WebApplicationFactory<AuthDbContext>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Review");
        builder.UseContentRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Services/CoppAddresd.Auth")));
        builder.ConfigureAppConfiguration((_, config) => { config.Sources.Clear(); config.AddInMemoryCollection(settings); });
        builder.ConfigureLogging(log => log.ClearProviders());
        builder.ConfigureTestServices(RemoveBackgroundServices);
    }
    internal static void RemoveBackgroundServices(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Namespace?.StartsWith("CoppAddresd", StringComparison.Ordinal) == true).ToArray()) services.Remove(descriptor);
    }
}

sealed class ApiFactory(IReadOnlyDictionary<string, string?> settings, QaClock clock) : WebApplicationFactory<ApiEntry::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Review");
        builder.UseContentRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/CoppAddresd.Api")));
        builder.ConfigureAppConfiguration((_, config) => { config.Sources.Clear(); config.AddInMemoryCollection(settings); });
        builder.ConfigureLogging(log => log.ClearProviders());
        builder.ConfigureTestServices(services => {
            AuthFactory.RemoveBackgroundServices(services);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
    }
}

sealed record QaClockRequest(DateTimeOffset? UtcNow);
sealed class QaClock : TimeProvider
{
    private long utcTicks;
    public override DateTimeOffset GetUtcNow()
    {
        var ticks = Interlocked.Read(ref utcTicks);
        return ticks == 0 ? DateTimeOffset.UtcNow : new DateTimeOffset(ticks, TimeSpan.Zero);
    }
    public void Set(DateTimeOffset? value) => Interlocked.Exchange(ref utcTicks, value?.UtcTicks ?? 0);
}
