using System.Data.Common;
using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.IdentityModel.Tokens.Jwt;
using CoppAddresd.Api.Context;
using CoppAddresd.Api.Controllers;
using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Application.Features.ProgramProgress.Queries.GetRoutineUsage;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    private static IPatientScopeActor UsageActor(Guid? professional = null, bool admin = false)
    {
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(Guid.NewGuid()); actor.IsAdmin.Returns(admin);
        actor.ProfessionalIdAsync(Arg.Any<CancellationToken>()).Returns(professional);
        actor.HasPermissionAsync("Wellness.View", Arg.Any<CancellationToken>()).Returns(true);
        return actor;
    }

    private static GetRoutineUsageQueryHandler UsageHandler(AppDbContext db, IPatientScopeActor actor)
    {
        var scopes = new PatientScopeStore(db); var data = new EffectiveDayContentStore(db);
        return new(new PatientScopeGuard(actor, scopes), new ProgramRoutineUsageStore(db, scopes), new EffectiveDayContentResolver(data));
    }

    private static ProgramEnrollment UsageEnrollment(Guid patient, Guid template, DateOnly start, ProgramEnrollmentStatus state = ProgramEnrollmentStatus.Active)
        => new() { Id = Guid.NewGuid(), PatientId = patient, TemplateId = template, StartLocalDate = start, Status = state };

    private static ProgramWeek UsageWeek(Guid enrollment, DateOnly start, ProgramWeekStatus status = ProgramWeekStatus.Active)
        => new() { Id = Guid.NewGuid(), EnrollmentId = enrollment, WeekNumber = 1, WeekStartDateLocal = start, WeekEndDateLocal = start.AddDays(6),
            Status = status, TasksSnapshot = JsonSerializer.SerializeToElement(Array.Empty<object>()) };

    [NutritionPostgresFact]
    public async Task RoutineUsage_AlcanceReal_DistintosSinInflarDias_ExcluyeInactivosYCerrados()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db);
        var pro = NutritionProfessional(seed.Patient); db.Professionals.Add(pro);
        db.PatientProfessionalAssignments.Add(new() { PatientId = seed.Patient, ProfessionalId = pro.Id });
        var foreign = await PatientAsync(db);
        var other = UsageEnrollment(foreign.Id, seed.Template, seed.Start); db.ProgramEnrollments.Add(other); db.ProgramWeeks.Add(UsageWeek(other.Id, seed.Start));
        foreach (var reason in new[] { "inactive", "deleted", "paused", "completed", "locked", "closed" })
        {
            var patient = await PatientAsync(db);
            if (reason == "inactive") patient.Status = "Inactivo";
            if (reason == "deleted") patient.DeletedAt = DateTime.UtcNow;
            var enrollment = UsageEnrollment(patient.Id, seed.Template, seed.Start,
                reason == "paused" ? ProgramEnrollmentStatus.Paused : reason == "completed" ? ProgramEnrollmentStatus.Completed : ProgramEnrollmentStatus.Active);
            db.ProgramEnrollments.Add(enrollment);
            db.ProgramWeeks.Add(UsageWeek(enrollment.Id, seed.Start, reason == "locked" ? ProgramWeekStatus.Locked
                : reason == "closed" ? ProgramWeekStatus.Completed : ProgramWeekStatus.Active));
        }
        await db.SaveChangesAsync();
        Assert.Equal(new(seed.Routine, 1, 1, 1), await UsageHandler(db, UsageActor(pro.Id)).Handle(new(seed.Routine), default));
        Assert.Equal(new(seed.Routine, 2, 2, 2), await UsageHandler(db, UsageActor(admin: true)).Handle(new(seed.Routine), default));
        Assert.Equal(new(seed.Routine, 0, 0, 0), await UsageHandler(db, UsageActor(Guid.NewGuid())).Handle(new(seed.Routine), default));
        Assert.Null(await UsageHandler(db, UsageActor(admin: true)).Handle(new(Guid.NewGuid()), default));
        await db.PatientProfessionalAssignments.ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "Inactive"));
        Assert.Equal(0, (await UsageHandler(db, UsageActor(pro.Id)).Handle(new(seed.Routine), default))!.PatientCount);
    }

    [NutritionPostgresFact]
    public async Task RoutineUsage_ResolverCanonico_SnapshotPlantillaDescansoYLegado_ConteosReadOnly()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db);
        var alternative = new ExerciseRoutine { Id = Guid.NewGuid(), Name = "Alternativa", Status = NutritionPlanStatus.Active };
        db.ExerciseRoutines.Add(alternative);
        db.WeeklyDayTemplates.Add(new() { Id = Guid.NewGuid(), TemplateId = seed.Template, WeekNumber = 1, Weekday = 1,
            TaskCode = TaskCode.ejercicio, RoutineId = alternative.Id }); await db.SaveChangesAsync();
        var handler = UsageHandler(db, UsageActor(admin: true));
        Assert.Equal(0, (await handler.Handle(new(alternative.Id), default))!.PatientCount); // El snapshot válido gana a plantilla semanal.
        await db.ProgramWeeks.Where(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.TasksSnapshot, JsonSerializer.SerializeToElement(Array.Empty<object>())));
        Assert.Equal(1, (await handler.Handle(new(alternative.Id), default))!.PatientCount);
        var rest = WeekSnapshotJson.Serialize(Enumerable.Range(1, 7).Select(d => new WeekSnapshotTaskRow((short)d, "ejercicio", 0, 0,
            RoutineId: alternative.Id, Rest: true)).ToArray());
        await db.ProgramWeeks.Where(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 1).ExecuteUpdateAsync(s => s.SetProperty(w => w.TasksSnapshot, rest));
        Assert.Equal(0, (await handler.Handle(new(alternative.Id), default))!.ActiveWeekCount);
        await db.ProgramWeeks.Where(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.TasksSnapshot, JsonSerializer.SerializeToElement(Array.Empty<object>())));
        await db.WeeklyDayTemplates.Where(t => t.TemplateId == seed.Template && t.TaskCode == TaskCode.ejercicio).ExecuteDeleteAsync();
        db.RoutineAssignments.Add(new() { Id = Guid.NewGuid(), PatientId = seed.Patient, RoutineId = alternative.Id, StartDate = seed.Start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            EndDate = seed.Start.AddDays(6).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), Status = AssignmentStatus.Active }); await db.SaveChangesAsync();
        Assert.Equal(1, (await handler.Handle(new(alternative.Id), default))!.EnrollmentCount);
        await using var read = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(
            new NpgsqlConnectionStringBuilder(connection) { Options = "-c default_transaction_read_only=on" }.ConnectionString).Options);
        Assert.Equal(1, (await UsageHandler(read, UsageActor(admin: true)).Handle(new(alternative.Id), default))!.PatientCount);
        Assert.Empty(read.ChangeTracker.Entries());
        await db.ExerciseRoutines.Where(r => r.Id == alternative.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, NutritionPlanStatus.Archived));
        Assert.Equal(0, (await handler.Handle(new(alternative.Id), default))!.EnrollmentCount);
    }

    private sealed class UsageSqlCounter : DbCommandInterceptor
    {
        public int Reads;
        public readonly List<string> Commands = [];
        public string? ActiveWeeksSql;
        public NpgsqlParameter[] ActiveWeeksParameters = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Reads++; Commands.Add(command.CommandText);
            if (Reads == 2)
            {
                ActiveWeeksSql = command.CommandText;
                ActiveWeeksParameters = command.Parameters.Cast<NpgsqlParameter>()
                    .Select(p => new NpgsqlParameter { ParameterName = p.ParameterName, NpgsqlDbType = p.NpgsqlDbType, Value = p.Value }).ToArray();
            }
            return ValueTask.FromResult(result);
        }
    }

    [NutritionPostgresFact]
    public async Task RoutineUsage_MilSemanas_ConsultasAcotadasYSinTracking()
    {
        await using var setup = Context(); var seed = await EffectiveSeedAsync(setup);
        for (var i = 0; i < 999; i++)
        {
            var patient = new PatientProfile { Id = Guid.NewGuid(), FirstName = "QA", LastName = "Volumen", Status = "Activo" };
            var enrollment = UsageEnrollment(patient.Id, seed.Template, seed.Start); setup.PatientProfiles.Add(patient);
            setup.ProgramEnrollments.Add(enrollment); setup.ProgramWeeks.Add(UsageWeek(enrollment.Id, seed.Start));
        }
        await setup.SaveChangesAsync();
        var counter = new UsageSqlCounter(); await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection).AddInterceptors(counter).Options);
        var timer = Stopwatch.StartNew(); var result = await UsageHandler(db, UsageActor(admin: true)).Handle(new(seed.Routine), default); timer.Stop();
        Assert.Equal(new(seed.Routine, 1000, 1000, 1000), result); Assert.Equal(5, counter.Reads); Assert.Empty(db.ChangeTracker.Entries());
        Assert.All(counter.Commands, sql => Assert.DoesNotContain("task_completions", sql));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"Lectura inesperadamente lenta: {timer.Elapsed}");
        var output = Environment.GetEnvironmentVariable("COP_222_EVIDENCE");
        if (!string.IsNullOrWhiteSpace(output))
        {
            await File.WriteAllTextAsync(Path.Combine(output, "volume.json"),
                JsonSerializer.Serialize(new { activeWeeks = 1000, patients = result!.PatientCount, selectQueries = counter.Reads, elapsedMs = timer.Elapsed.TotalMilliseconds, snapshotIsolation = "repeatable read / read only" }));
            await db.Database.OpenConnectionAsync();
            await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + counter.ActiveWeeksSql, (NpgsqlConnection)db.Database.GetDbConnection());
            explain.Parameters.AddRange(counter.ActiveWeeksParameters);
            await File.WriteAllTextAsync(Path.Combine(output, "active-weeks-explain.json"), (string)(await explain.ExecuteScalarAsync())!);
        }
    }

    private const string UsageJwtKey = "synthetic-only-routine-usage-222-signing-key-not-production";
    private sealed class UsageHttpActor(IHttpContextAccessor accessor) : IPatientScopeActor
    {
        private ClaimsPrincipal User => accessor.HttpContext!.User;
        public Guid? UserId => Guid.TryParse(User.FindFirst("sub")?.Value, out var value) ? value : null;
        public bool IsAdmin => User.IsInRole("Admin");
        public Task<Guid?> ProfessionalIdAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult<Guid?>(Guid.TryParse(User.FindFirst("professional_id")?.Value, out var id) ? id : null); }
        public Task<bool> HasPermissionAsync(string permission, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(User.HasClaim("permission", permission)); }
    }

    private static string UsageJwt(Guid? professional, bool permission = true, bool admin = false)
    {
        var claims = new List<Claim> { new("sub", Guid.NewGuid().ToString()) };
        if (permission) claims.Add(new("permission", "Wellness.View"));
        if (professional.HasValue) claims.Add(new("professional_id", professional.Value.ToString()));
        if (admin) claims.Add(new("role", "Admin"));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("usage-qa", "erp", claims, expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(UsageJwtKey)), SecurityAlgorithms.HmacSha256)));
    }

    [NutritionPostgresFact]
    public async Task RoutineUsage_HTTP401403404_ContratoYAlcanceConPostgresReadOnly()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db); var pro = NutritionProfessional(seed.Patient); db.Professionals.Add(pro);
        db.PatientProfessionalAssignments.Add(new() { PatientId = seed.Patient, ProfessionalId = pro.Id });
        var foreign = await PatientAsync(db); var other = UsageEnrollment(foreign.Id, seed.Template, seed.Start);
        db.ProgramEnrollments.Add(other); db.ProgramWeeks.Add(UsageWeek(other.Id, seed.Start)); await db.SaveChangesAsync();
        var before = (await db.ProgramWeeks.AsNoTracking().OrderBy(w => w.Id).Select(w => w.TasksSnapshot).ToArrayAsync()).Select(x => x.GetRawText()).ToArray();
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Host.UseDefaultServiceProvider(o => { o.ValidateOnBuild = false; o.ValidateScopes = true; });
        builder.Services.AddControllers().AddApplicationPart(typeof(RoutineUsageController).Assembly);
        builder.Services.AddHttpContextAccessor(); builder.Services.AddScoped<IPatientScopeActor, UsageHttpActor>();
        builder.Services.AddScoped<ICurrentContext>(s =>
        {
            var accessor = s.GetRequiredService<IHttpContextAccessor>(); var current = Substitute.For<ICurrentContext>();
            current.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => Task.FromResult(accessor.HttpContext!.User.HasClaim("permission", call.ArgAt<string>(0)))); return current;
        });
        builder.Services.AddScoped(_ => new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(
            new NpgsqlConnectionStringBuilder(connection) { Options = "-c default_transaction_read_only=on" }.ConnectionString).Options));
        builder.Services.AddScoped<IPatientScopeStore, PatientScopeStore>(); builder.Services.AddScoped<IPatientScopeGuard, PatientScopeGuard>();
        builder.Services.AddScoped<IProgramRoutineUsageStore, ProgramRoutineUsageStore>();
        builder.Services.AddScoped<IEffectiveDayContentStore, EffectiveDayContentStore>(); builder.Services.AddScoped<IEffectiveDayContentResolver, EffectiveDayContentResolver>();
        builder.Services.AddMediatR(c => c.RegisterServicesFromAssembly(typeof(GetRoutineUsageQuery).Assembly));
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => { o.MapInboundClaims = false; o.TokenValidationParameters = new()
        { ValidateIssuer = true, ValidIssuer = "usage-qa", ValidateAudience = true, ValidAudience = "erp", ValidateLifetime = true,
            ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(UsageJwtKey)), RoleClaimType = "role" }; });
        builder.Services.AddAuthorization();
        await using var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapControllers(); await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        async Task<HttpResponseMessage> Get(Guid routine, string? token = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/program/routines/{routine}/usage");
            if (token is not null) request.Headers.Authorization = new("Bearer", token);
            return await http.SendAsync(request);
        }
        using (var response = await Get(seed.Routine)) Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        using (var response = await Get(seed.Routine, UsageJwt(pro.Id, false))) Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        using (var response = await Get(Guid.NewGuid(), UsageJwt(pro.Id))) Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        foreach (var (token, count) in new[] { (UsageJwt(pro.Id), 1), (UsageJwt(null, admin: true), 2), (UsageJwt(null), 0) })
        {
            using var response = await Get(seed.Routine, token); Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(new[] { "routineId", "patientCount", "enrollmentCount", "activeWeekCount" }, json.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.Equal(seed.Routine, json.RootElement.GetProperty("routineId").GetGuid());
            foreach (var field in new[] { "patientCount", "enrollmentCount", "activeWeekCount" }) Assert.Equal(count, json.RootElement.GetProperty(field).GetInt32());
        }
        Assert.Equal(before, (await db.ProgramWeeks.AsNoTracking().OrderBy(w => w.Id).Select(w => w.TasksSnapshot).ToArrayAsync()).Select(x => x.GetRawText()).ToArray());
        await app.StopAsync();
    }
}
