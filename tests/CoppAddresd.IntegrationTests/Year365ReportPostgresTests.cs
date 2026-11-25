using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using CoppAddresd.Api.Constants;
using CoppAddresd.Api.Controllers;
using CoppAddresd.Api.Middleware;
using CoppAddresd.Application.Features.Wellness.Year365;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
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
        var version = new Motor365EngineParams { Version = 1, ParametersJson = "{}", ChangedBy = patient.Id, Reason = "Fixture" };
        var item = new Motor365EngineEvent
        {
            Id = Guid.NewGuid(), PersonId = patient.Id, Day = 4, PlanVersion = 1, ParamsVersion = 1,
            Kind = "modo", MessageEs = "Cambio", MessageEn = "Change",
            OccurredAt = new DateTimeOffset(2028, 2, 4, 19, 0, 0, TimeSpan.Zero)
        };
        db.Motor365EngineParams.Add(version);
        db.Motor365EngineEvents.Add(item);
        await db.SaveChangesAsync();

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
        builder.Services.AddSingleton<TimeProvider>(new Year365TestClock());
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
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseMiddleware<ExceptionHandlingMiddleware>();
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
                new("sub", Guid.NewGuid().ToString()), new("professional_id", professional.Id.ToString())
            };
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

        db.Motor365DailyLogs.Add(new Motor365DailyLog
        {
            PersonId = patient.Id, Day = 4, LogDate = first.AddDays(3), Adherence = 0.60m, Energy = 3, WaistCm = 82.3m
        });
        await db.SaveChangesAsync();
        using var refreshed = await Get(patient.Id, Token("erp"));
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var updatedReport = (await refreshed.Content.ReadFromJsonAsync<Year365ReportResponse>())!.Report!;
        Assert.Equal(60m, updatedReport.Days.Single(day => day.Day == 4).AdherencePercent);
        Assert.Equal(3, updatedReport.Summary.RecordedDays);
        Assert.Equal(new Year365Measurement(first.AddDays(3), 82.3m), updatedReport.Summary.Waist);
        await app.StopAsync();
    }

    private static Motor365Person CreateYear365Person(Guid patientId, DateOnly date) => new()
    {
        PatientId = patientId, StartDate = date, Sex = WellnessSex.Female, BirthYear = 1990,
        HeightCm = 170, InitialWeightKg = 75, InitialWaistCm = 85, ActivityLevel = 1,
        Goal = WellnessGoal.LoseWeight, Pace = WellnessPace.Moderate
    };

    private sealed class Year365TestClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2028, 2, 5, 18, 0, 0, TimeSpan.Zero);
    }
}
