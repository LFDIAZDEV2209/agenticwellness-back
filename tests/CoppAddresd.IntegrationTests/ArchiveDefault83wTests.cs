using System.Diagnostics;
using System.Text.Json;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task ArchivoDefault_SimulaIdempotenteRevierteYConservaInscripcionYContenidoLegado()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db);
        var template = await db.ProgramTemplates.SingleAsync(t => t.Id == seed.Template);
        template.Code = "default-83w"; template.TotalWeeks = 83; await db.SaveChangesAsync();
        var enrollmentBefore = JsonSerializer.Serialize(await db.ProgramEnrollments.AsNoTracking().ToArrayAsync());
        var weeksBefore = JsonSerializer.Serialize(await db.ProgramWeeks.AsNoTracking().OrderBy(w => w.Id).ToArrayAsync());
        var version = template.Version;
        var backup = Path.Combine(Path.GetTempPath(), $"archive-default-test-{Guid.NewGuid():N}.json");
        try
        {
            var simulation = await ArchiveDefaultScriptAsync("simulate", backup);
            Assert.Equal(0, simulation.GetProperty("escrituras").GetInt32()); Assert.Equal(1, simulation.GetProperty("propuestas").GetInt32());
            Assert.False(File.Exists(backup));
            Assert.Equal(1, (await ArchiveDefaultScriptAsync("apply", backup)).GetProperty("escrituras").GetInt32());
            Assert.Equal(0, (await ArchiveDefaultScriptAsync("apply", backup)).GetProperty("escrituras").GetInt32());
            await db.Entry(template).ReloadAsync(); Assert.Equal(TemplateStatus.Archived, template.Status); Assert.Equal(version, template.Version);
            var store = new CoppAddresd.Infrastructure.Repositories.EffectiveDayContentStore(db);
            var data = await store.LoadAsync([seed.Enrollment], seed.Start, seed.Start.AddDays(580), CancellationToken.None);
            var days = new EffectiveDayContentResolver(store).ResolveRange(data, data.Enrollments.Single(), seed.Start, seed.Start.AddDays(580), CancellationToken.None);
            Assert.Equal(581, days.Count); Assert.All(days, d => Assert.False(d.HasGaps));
            Assert.Equal(enrollmentBefore, JsonSerializer.Serialize(await db.ProgramEnrollments.AsNoTracking().ToArrayAsync()));
            Assert.Equal(weeksBefore, JsonSerializer.Serialize(await db.ProgramWeeks.AsNoTracking().OrderBy(w => w.Id).ToArrayAsync()));
            Assert.Equal(0, (await ArchiveDefaultScriptAsync("simulate", backup, reverse: true)).GetProperty("escrituras").GetInt32());
            Assert.Equal(1, (await ArchiveDefaultScriptAsync("reverse", backup)).GetProperty("escrituras").GetInt32());
            Assert.Equal(0, (await ArchiveDefaultScriptAsync("reverse", backup)).GetProperty("escrituras").GetInt32());
            await db.Entry(template).ReloadAsync(); Assert.Equal(TemplateStatus.Active, template.Status);
            await ArchiveDefaultScriptAsync("apply", backup);
            await db.ProgramTemplates.Where(t => t.Id == template.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Version, t => t.Version + 1));
            await ArchiveDefaultScriptAsync("reverse", backup, expectFailure: true);
            await db.Entry(template).ReloadAsync(); Assert.Equal(TemplateStatus.Archived, template.Status); Assert.Equal(version + 1, template.Version);
        }
        finally { if (File.Exists(backup)) File.Delete(backup); }
    }

    private async Task<JsonElement> ArchiveDefaultScriptAsync(string mode, string backup, bool reverse = false, bool expectFailure = false)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "scripts", "archive_default_83w.py"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var start = new ProcessStartInfo("python") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(directory.FullName, "scripts", "archive_default_83w.py"));
        start.ArgumentList.Add("--mode"); start.ArgumentList.Add(mode);
        start.ArgumentList.Add("--backup"); start.ArgumentList.Add(backup);
        if (reverse) start.ArgumentList.Add("--reverse");
        var builder = new NpgsqlConnectionStringBuilder(connection);
        start.Environment["PGHOST"] = builder.Host; start.Environment["PGPORT"] = builder.Port.ToString();
        start.Environment["PGDATABASE"] = builder.Database; start.Environment["PGUSER"] = builder.Username; start.Environment["PGPASSWORD"] = builder.Password;
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(); var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (expectFailure) { Assert.NotEqual(0, process.ExitCode); return default; }
        Assert.True(process.ExitCode == 0, error);
        return JsonSerializer.Deserialize<JsonElement>(output);
    }
}
