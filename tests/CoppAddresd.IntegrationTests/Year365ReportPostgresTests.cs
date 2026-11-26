using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using CoppAddresd.Api.Configuration;
using CoppAddresd.Api.Constants;
using CoppAddresd.Api.Controllers;
using CoppAddresd.Api.Middleware;
using CoppAddresd.Application.Features.Motor365;
using CoppAddresd.Application.Features.Wellness.Year365;
using CoppAddresd.Application.Features.Wellness365;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Wellness365;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using UglyToad.PdfPig;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task Year365ReportHttpReadsPersistedDaysAndEnforcesPatientScope()
    {
        await using var db = Context();
        var patient = await PatientAsync(db);
        patient.Timezone = "America/Bogota";
        var other = await PatientAsync(db);
        var withoutProgram = await PatientAsync(db);
        var professional = NutritionProfessional(patient.Id);
        db.Professionals.Add(professional);
        db.PatientProfessionalAssignments.Add(new() { PatientId = patient.Id, ProfessionalId = professional.Id });
        db.PatientProfessionalAssignments.Add(new() { PatientId = withoutProgram.Id, ProfessionalId = professional.Id });
        var first = new DateOnly(2028, 1, 30);
        db.Motor365People.AddRange(CreateYear365Person(patient.Id, first), CreateYear365Person(other.Id, first));
        db.Motor365DailyLogs.AddRange(
            new Motor365DailyLog { PersonId = patient.Id, Day = 3, LogDate = first.AddDays(2), Adherence = 0m, Energy = 3 },
            new Motor365DailyLog { PersonId = patient.Id, Day = 5, LogDate = first.AddDays(4), Adherence = 0.8m, Energy = 3, WeightKg = 73m },
            new Motor365DailyLog { PersonId = other.Id, Day = 4, LogDate = first.AddDays(3), Adherence = 1m, Energy = 3 });
        var version = new Motor365EngineParams
        {
            Version = 1, ParametersJson = JsonSerializer.Serialize(WellnessParameters.Default, JsonSerializerOptions.Web),
            ChangedBy = patient.UserId!.Value, Reason = "Fixture"
        };
        var item = new Motor365EngineEvent
        {
            Id = Guid.NewGuid(), PersonId = patient.Id, Day = 4, PlanVersion = 1, ParamsVersion = 1,
            Kind = "modo", MessageEs = "Cambio", MessageEn = "Change",
            OccurredAt = new DateTimeOffset(2028, 2, 4, 19, 0, 0, TimeSpan.Zero)
        };
        db.Motor365EngineParams.Add(version);
        db.Motor365EngineEvents.Add(item);
        await db.SaveChangesAsync();
        await SeedYear365MotorConsentAsync(db, patient);

        var signingKey = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Host.UseDefaultServiceProvider(options => options.ValidateOnBuild = false);
        builder.Services.AddControllers().AddApplicationPart(typeof(Year365ReportController).Assembly);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IPatientScopeActor, UsageHttpActor>();
        builder.Services.AddScoped<IPatientScopeStore, PatientScopeStore>();
        builder.Services.AddScoped<IPatientScopeGuard, PatientScopeGuard>();
        builder.Services.AddScoped<IYear365ReportStore, Year365ReportStore>();
        builder.Services.AddScoped(_ => Context());
        var clock = new Year365TestClock();
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddMediatR(c => c.RegisterServicesFromAssembly(typeof(GetYear365ReportQuery).Assembly));
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new()
            {
                ValidIssuer = "year365-test",
                ValidAudiences = ["app", "erp"],
                IssuerSigningKey = signingKey,
                ValidateIssuerSigningKey = true,
                RoleClaimType = "role"
            };
        });
        builder.Services.AddMotor365Api();
        builder.Services.AddSingleton<IWellnessPlanGate, Year365TestGate>();
        builder.Services.AddAuthorizationBuilder().AddPolicy(Motor365PlanController.PatientWritePolicy,
            policy => policy.RequireAuthenticatedUser().RequireClaim("aud", "app"));
        await using var app = builder.Build();
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        app.UseWhen(context => context.Request.Path.StartsWithSegments("/v1"),
            branch => branch.UseMiddleware<Motor365Errors>());
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        using var http = new HttpClient
        {
            BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())
        };

        string Token(string audience, bool mayView = true, bool mayExport = false)
        {
            var claims = new List<Claim>
            {
                new("sub", audience == "app" ? patient.UserId!.Value.ToString() : Guid.NewGuid().ToString()),
                new("professional_id", professional.Id.ToString())
            };
            if (audience == "app")
                claims.Add(new(ClaimTypes.NameIdentifier, patient.UserId!.Value.ToString()));
            if (mayView)
                claims.Add(new("permission", PermissionCodes.ProgramProfile360View));
            if (mayExport)
                claims.Add(new("permission", PermissionCodes.ProgramExport));
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                "year365-test", audience, claims, expires: DateTime.UtcNow.AddMinutes(5),
                signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256)));
        }

        async Task<HttpResponseMessage> Get(
            Guid patientId, string? token = null, string month = "2028-02", string suffix = "", string lang = "es")
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1/program/erp/patients/{patientId}/year-365{suffix}?month={month}&lang={lang}");
            if (token is not null)
                request.Headers.Authorization = new("Bearer", token);
            return await http.SendAsync(request);
        }

        using (var response = await Get(patient.Id)) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using (var response = await Get(patient.Id, Token("app"))) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using (var response = await Get(patient.Id, Token("erp", false))) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using (var response = await Get(other.Id, Token("erp"))) Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using (var response = await Get(patient.Id, Token("erp"), "2028-2")) Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using (var response = await Get(withoutProgram.Id, Token("erp")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null((await response.Content.ReadFromJsonAsync<Year365ReportResponse>())!.Report);
        }

        using var success = await Get(patient.Id, Token("erp"));
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.True(success.Headers.CacheControl?.NoStore);
        var report = (await success.Content.ReadFromJsonAsync<Year365ReportResponse>())!.Report!;
        Assert.Equal(29, report.Days.Count);
        Assert.Equal(0m, report.Days[0].AdherencePercent);
        Assert.Null(report.Days[1].AdherencePercent);
        Assert.Equal(2, report.Summary.RecordedDays);
        Assert.Equal(40m, report.Summary.AdherencePercent);
        Assert.Null(report.Summary.ScheduledPauseDays);
        Assert.Null(report.Summary.SafetyExercisePauseDays);
        Assert.Equal(new Year365Event(item.Id, 4, new DateOnly(2028, 2, 2), "modo"), Assert.Single(report.Events));
        Assert.Equal(new Year365Measurement(new DateOnly(2028, 2, 3), 73m), report.Summary.Weight);

        foreach (var format in new[] { "csv", "pdf" })
        {
            var suffix = $"/export/{format}";
            using var unprivileged = await Get(patient.Id, Token("erp"), suffix: suffix);
            Assert.Equal(HttpStatusCode.Forbidden, unprivileged.StatusCode);
            using var exportOnly = await Get(patient.Id, Token("erp", mayView: false, mayExport: true), suffix: suffix);
            Assert.Equal(HttpStatusCode.Forbidden, exportOnly.StatusCode);
            using var wrongPatient = await Get(other.Id, Token("erp", mayExport: true), suffix: suffix);
            Assert.Equal(HttpStatusCode.NotFound, wrongPatient.StatusCode);
        }

        using var csv = await Get(patient.Id, Token("erp", mayExport: true), suffix: "/export/csv");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.Equal("year-365-2028-02.csv", csv.Content.Headers.ContentDisposition?.FileName);
        var csvContent = await csv.Content.ReadAsStringAsync();
        Assert.Contains("\"2028-02\",\"Peso\",\"73\",\"kg\",\"2028-02-03\"", csvContent);
        Assert.Contains("\"2028-02\",\"Adherencia\",\"40\",\"%\"", csvContent);
        Assert.Contains("\"2028-02\",\"Cintura\",\"Sin dato\"", csvContent);
        Assert.DoesNotContain(patient.Id.ToString(), csvContent);

        using var pdf = await Get(patient.Id, Token("erp", mayExport: true), suffix: "/export/pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.Equal("year-365-2028-02.pdf", pdf.Content.Headers.ContentDisposition?.FileName);
        using var document = PdfDocument.Open(await pdf.Content.ReadAsByteArrayAsync());
        var pdfText = document.GetPage(1).Text;
        Assert.Contains("Año 365", pdfText);
        Assert.Contains("Métrica", pdfText);
        Assert.Contains("73 kg", pdfText);
        Assert.Contains("40 %", pdfText);
        Assert.Contains("2028-02-03", pdfText);
        Assert.DoesNotContain(patient.Id.ToString(), pdfText);

        using var english = await Get(patient.Id, Token("erp", mayExport: true), suffix: "/export/csv", lang: "en");
        Assert.Contains("Scheduled breaks", await english.Content.ReadAsStringAsync());

        const string motorPath = "/api/v1/people/me";
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"{motorPath}/plan"))
        {
            request.Headers.Authorization = new("Bearer", Token("app"));
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var snapshot = (await response.Content.ReadFromJsonAsync<Motor365DayResponse>())!;
            Assert.Equal(14, snapshot.Today);
            Assert.Equal(new DateOnly(2028, 2, 12), snapshot.TodayLocalDate);
            Assert.Equal("America/Bogota", snapshot.TimeZoneId);
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"{motorPath}/plan"))
        {
            request.Headers.Authorization = new("Bearer", Token("erp", mayExport: true));
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        async Task<HttpResponseMessage> CloseDay(int day, int energy = 3, int pain = 0)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"{motorPath}/logs/{day}");
            request.Headers.Authorization = new("Bearer", Token("app"));
            request.Content = JsonContent.Create(new Motor365ClosureRequest(0.60m, 8, energy, pain, [], null, 82.3m, false));
            return await http.SendAsync(request);
        }

        // February 13 in UTC is still program day 14 in Bogotá.
        using var futureClose = await CloseDay(15);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, futureClose.StatusCode);
        using var close = await CloseDay(4);
        Assert.Equal(HttpStatusCode.NoContent, close.StatusCode);
        async Task<int> Version() => await db.Motor365People.AsNoTracking().Where(row => row.PatientId == patient.Id)
            .Select(row => row.PlanVersion).SingleAsync();
        var revision = await Version();
        using var retry = await CloseDay(4);
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        Assert.Equal(revision, await Version());
        var storedClose = await db.Motor365DailyLogs.AsNoTracking()
            .SingleAsync(log => log.PersonId == patient.Id && log.Day == 4);
        Assert.Equal(first.AddDays(3), storedClose.LogDate);

        var weeklyDecisions = db.Motor365EngineEvents.AsNoTracking()
            .Where(entry => entry.PersonId == patient.Id && entry.Day == 14 && entry.Kind == "sin");
        Assert.Single(await weeklyDecisions.ToArrayAsync());

        using var correction = await CloseDay(4, energy: 4);
        Assert.Equal(HttpStatusCode.NoContent, correction.StatusCode);
        var decisions = await weeklyDecisions.OrderByDescending(entry => entry.PlanVersion).ToArrayAsync();
        Assert.Equal(2, decisions.Length);
        var latestDecision = decisions[0];
        using var refreshed = await Get(patient.Id, Token("erp"));
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var updatedReport = (await refreshed.Content.ReadFromJsonAsync<Year365ReportResponse>())!.Report!;
        Assert.Equal(60m, updatedReport.Days.Single(day => day.Day == 4).AdherencePercent);
        Assert.Equal(3, updatedReport.Summary.RecordedDays);
        Assert.Equal(46.7m, updatedReport.Summary.AdherencePercent);
        Assert.Equal(new Year365Measurement(first.AddDays(3), 82.3m), updatedReport.Summary.Waist);
        Assert.Equal(0, updatedReport.Summary.ScheduledPauseDays);
        Assert.Equal(0, updatedReport.Summary.SafetyExercisePauseDays);
        Assert.Equal(new Year365Event(latestDecision.Id, 14, first.AddDays(13), "sin"),
            Assert.Single(updatedReport.Events, entry => entry.Day == 14 && entry.Kind == "sin"));
        var decisionDay = updatedReport.Days.Single(day => day.Day == 14);
        Assert.Null(decisionDay.WeightKg);
        Assert.Null(decisionDay.WaistCm);

        var exportToken = Token("erp", mayExport: true);
        using var refreshedCsv = await Get(patient.Id, exportToken, suffix: "/export/csv");
        Assert.Equal(HttpStatusCode.OK, refreshedCsv.StatusCode);
        Assert.True(refreshedCsv.Headers.CacheControl?.NoStore);
        var refreshedCsvText = await refreshedCsv.Content.ReadAsStringAsync();
        Assert.Contains("\"2028-02\",\"Días con registro\",\"3\",\"días\"", refreshedCsvText);
        Assert.Contains("\"2028-02\",\"Adherencia\",\"46.7\",\"%\"", refreshedCsvText);
        Assert.Contains("\"2028-02\",\"Cintura\",\"82.3\",\"cm\",\"2028-02-02\"", refreshedCsvText);
        Assert.Contains("\"2028-02\",\"Pausas programadas\",\"0\"", refreshedCsvText);
        Assert.Contains("\"2028-02\",\"Pausas de ejercicio\",\"0\"", refreshedCsvText);

        using var refreshedPdf = await Get(patient.Id, exportToken, suffix: "/export/pdf");
        Assert.Equal(HttpStatusCode.OK, refreshedPdf.StatusCode);
        Assert.True(refreshedPdf.Headers.CacheControl?.NoStore);
        using var refreshedDocument = PdfDocument.Open(await refreshedPdf.Content.ReadAsByteArrayAsync());
        var refreshedPdfText = refreshedDocument.GetPage(1).Text;
        Assert.Contains("2028-02", refreshedPdfText);
        Assert.Contains("3 días", refreshedPdfText);
        Assert.Contains("46.7 %", refreshedPdfText);
        Assert.Contains("82.3 cm", refreshedPdfText);
        Assert.Contains("2028-02-02", refreshedPdfText);
        Assert.DoesNotContain(patient.Id.ToString(), refreshedPdfText);

        using var priorMonthCsv = await Get(patient.Id, exportToken, month: "2028-01", suffix: "/export/csv");
        Assert.Equal(HttpStatusCode.OK, priorMonthCsv.StatusCode);
        var priorMonthCsvText = await priorMonthCsv.Content.ReadAsStringAsync();
        Assert.Contains("\"2028-01\",\"Adherencia\",\"Sin dato\"", priorMonthCsvText);
        Assert.Contains("\"2028-01\",\"Cintura\",\"Sin dato\"", priorMonthCsvText);
        Assert.DoesNotContain("2028-02", priorMonthCsvText);

        // Week 9 crosses March and April. The late entry also drives two exercise pause days.
        clock.UtcNow = new(2028, 4, 2, 3, 0, 0, TimeSpan.Zero);
        using var lateClose = await CloseDay(57, pain: 8);
        Assert.Equal(HttpStatusCode.NoContent, lateClose.StatusCode);
        var lateVersion = await Version();
        using var lateRetry = await CloseDay(57, pain: 8);
        Assert.Equal(HttpStatusCode.NoContent, lateRetry.StatusCode);
        Assert.Equal(lateVersion, await Version());
        var pauseDays = await db.Motor365PauseDays.AsNoTracking()
            .Where(entry => entry.PersonId == patient.Id && entry.Day >= 57).ToArrayAsync();
        Assert.Equal(7, pauseDays.Length);
        Assert.All(pauseDays, entry => Assert.True(entry.Scheduled));
        Assert.Equal(2, pauseDays.Count(entry => entry.SafetyExercise));
        Assert.Contains(pauseDays, entry => entry.Date == new DateOnly(2028, 4, 1));

        using var march = await Get(patient.Id, Token("erp"), month: "2028-03");
        Assert.Equal(HttpStatusCode.OK, march.StatusCode);
        var marchSummary = (await march.Content.ReadFromJsonAsync<Year365ReportResponse>())!.Report!.Summary;
        Assert.Equal(6, marchSummary.ScheduledPauseDays);
        Assert.Equal(2, marchSummary.SafetyExercisePauseDays);
        using var april = await Get(patient.Id, Token("erp"), month: "2028-04");
        Assert.Equal(HttpStatusCode.OK, april.StatusCode);
        var aprilSummary = (await april.Content.ReadFromJsonAsync<Year365ReportResponse>())!.Report!.Summary;
        Assert.Equal(0, aprilSummary.RecordedDays);
        Assert.Equal(1, aprilSummary.ScheduledPauseDays);
        Assert.Equal(0, aprilSummary.SafetyExercisePauseDays);
        using var aprilCsv = await Get(patient.Id, exportToken, month: "2028-04", suffix: "/export/csv");
        Assert.Equal(HttpStatusCode.OK, aprilCsv.StatusCode);
        Assert.Contains("\"2028-04\",\"Pausas programadas\",\"1\",\"día\"", await aprilCsv.Content.ReadAsStringAsync());
        using var aprilPdf = await Get(patient.Id, exportToken, month: "2028-04", suffix: "/export/pdf");
        Assert.Equal(HttpStatusCode.OK, aprilPdf.StatusCode);
        using var aprilDocument = PdfDocument.Open(await aprilPdf.Content.ReadAsByteArrayAsync());
        Assert.Contains("1 día", aprilDocument.GetPage(1).Text);
        await app.StopAsync();
    }

    private static async Task SeedYear365MotorConsentAsync(AppDbContext db, PatientProfile patient)
    {
        foreach (var code in new[] { "paid-demo-terms-es", "paid-demo-privacy-es" })
        {
            var document = new LegalDocument { Id = Guid.NewGuid(), Code = code, Title = "Synthetic QA" };
            var version = new LegalDocumentVersion
            {
                Id = Guid.NewGuid(), Document = document, Major = 1, IsPublished = true, Content = "Synthetic QA"
            };
            db.LegalDocumentVersions.Add(version);
            await db.SaveChangesAsync();
            document.CurrentVersionId = version.Id;
            var receipt = new LegalDocumentAcceptance
            {
                Id = Guid.NewGuid(), UserId = patient.UserId!.Value, LegalDocumentVersionId = version.Id,
                Scope = code, Locale = "es", DisclosureId = "synthetic", DisclosureSnapshot = "{}",
                IdempotencyKey = Guid.NewGuid(), AcceptedAt = DateTime.UtcNow
            };
            db.LegalDocumentAcceptances.Add(receipt);
            db.Motor365Consents.Add(new() { PersonId = patient.Id, AcceptanceId = receipt.Id, RecordedAt = DateTimeOffset.UtcNow });
        }
        db.Motor365Screenings.Add(new()
        {
            PersonId = patient.Id, SubmissionId = Guid.NewGuid(), RecordedAt = DateTimeOffset.UtcNow,
            ParQ1 = 0, ParQ2 = 0, ParQ3 = 0, ParQ4 = 0, ParQ5 = 0, ParQ6 = 0, ParQ7 = 0,
            Nqs1 = 0, Nqs2 = 0, Nqs3 = 0, Nqs4 = 0, Nqs5 = 0, Nqs6 = 0
        });
        await db.SaveChangesAsync();
    }

    private static Motor365Person CreateYear365Person(Guid patientId, DateOnly date) => new()
    {
        PatientId = patientId, StartDate = date, Sex = WellnessSex.Female, BirthYear = 1990,
        HeightCm = 170, InitialWeightKg = 75, InitialWaistCm = 85, ActivityLevel = 1,
        Goal = WellnessGoal.LoseWeight, Pace = WellnessPace.Moderate
    };

    private sealed class Year365TestClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2028, 2, 13, 3, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class Year365TestGate : IWellnessPlanGate
    {
        public Task<WellnessBlock?> CheckAsync(Guid patientId, CancellationToken ct) => Task.FromResult<WellnessBlock?>(null);
    }
}
