using System.Globalization;
using System.Text;
using CoppAddresd.Application.Features.Wellness.Year365;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace CoppAddresd.Api.Http;

internal sealed class Year365ReportExport(Year365Report report, bool english)
{
    private static readonly Lazy<byte[]> ReportFont = new(LoadReportFont);

    public byte[] ToCsv()
    {
        var content = new StringBuilder("\uFEFF");
        AppendRow(content, Text("Periodo", "Period"), Text("Métrica", "Metric"),
            Text("Valor", "Value"), Text("Unidad", "Unit"), Text("Fecha", "Date"));
        foreach (var field in Fields())
            AppendRow(content, report.Month, field.Label, field.Value, field.Unit, field.Date);
        return Encoding.UTF8.GetBytes(content.ToString());
    }

    public byte[] ToPdf()
    {
        var document = new PdfDocumentBuilder();
        var page = document.AddPage(PageSize.A4);
        var font = document.AddTrueTypeFont(ReportFont.Value);
        page.SetTextAndFillColor(25, 43, 70);
        page.AddText(Text("Año 365", "Year 365"), 24, new PdfPoint(44, 777), font);
        page.AddText($"{Text("Resumen mensual", "Monthly summary")}  /  {report.Month}", 12, new PdfPoint(44, 752), font);
        page.SetStrokeColor(175, 186, 201);
        page.DrawLine(new PdfPoint(44, 733), new PdfPoint(550, 733));
        page.AddText(Text("Métrica", "Metric"), 10, new PdfPoint(44, 710), font);
        page.AddText(Text("Valor", "Value"), 10, new PdfPoint(320, 710), font);
        page.AddText(Text("Fecha", "Date"), 10, new PdfPoint(445, 710), font);
        var row = 684;
        foreach (var field in Fields())
        {
            page.AddText(field.Label, 11, new PdfPoint(44, row), font);
            page.AddText(string.IsNullOrEmpty(field.Unit) ? field.Value : $"{field.Value} {field.Unit}",
                11, new PdfPoint(320, row), font);
            if (field.Date.Length > 0)
                page.AddText(field.Date, 11, new PdfPoint(445, row), font);
            row -= 29;
        }
        return document.Build();
    }

    private IEnumerable<ReportField> Fields()
    {
        var summary = report.Summary;
        yield return Field("Días con registro", "Recorded days", summary.RecordedDays, DayUnit(summary.RecordedDays));
        yield return Field("Adherencia", "Adherence", summary.AdherencePercent, "%");
        yield return Field("Peso", "Weight", summary.Weight?.Value, "kg", summary.Weight?.Date);
        yield return Field("Cintura", "Waist", summary.Waist?.Value, "cm", summary.Waist?.Date);
        yield return Field("Racha", "Streak", summary.StreakDays, DayUnit(summary.StreakDays));
        yield return Field("Pausas programadas", "Scheduled breaks", summary.ScheduledPauseDays, DayUnit(summary.ScheduledPauseDays));
        yield return Field("Pausas de ejercicio", "Exercise pauses", summary.SafetyExercisePauseDays, DayUnit(summary.SafetyExercisePauseDays));
    }

    private string Text(string es, string en) => english ? en : es;

    private string DayUnit(int? days) => days == 1 ? Text("día", "day") : Text("días", "days");

    private ReportField Field(string es, string en, decimal? value, string unit, DateOnly? date = null) => new(
        Text(es, en),
        value?.ToString("0.##", CultureInfo.InvariantCulture) ?? Text("Sin dato", "No data"),
        value.HasValue ? unit : "",
        date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "");

    private static void AppendRow(StringBuilder content, params string[] cells)
    {
        content.AppendJoin(',', cells.Select(cell =>
        {
            var trimmed = cell.AsSpan().TrimStart();
            if (!trimmed.IsEmpty && trimmed[0] is '=' or '+' or '-' or '@')
                cell = "'" + cell;
            return $"\"{cell.Replace("\"", "\"\"")}\"";
        }));
        content.Append("\r\n");
    }

    private static byte[] LoadReportFont()
    {
        using var source = typeof(Year365ReportExport).Assembly.GetManifestResourceStream("Year365ReportSans.ttf")
            ?? throw new InvalidOperationException("The Year 365 report font is missing.");
        using var target = new MemoryStream();
        source.CopyTo(target);
        return target.ToArray();
    }

    private sealed record ReportField(string Label, string Value, string Unit, string Date);
}
