using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CoppAddresd.Auth.Configuration;
using CoppAddresd.Auth.Data;
using CoppAddresd.Auth.Entities;
using CoppAddresd.Auth.Extensions;
using CoppAddresd.Auth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoppAddresd.UnitTests.Auth.Services;

public sealed class TransactionalEmailTests
{
    private const string Recipient = "ana@example.invalid";
    private static readonly string[] Codes = ["BIEN-01", "ACT-01", "ACT-02", "PWD-01", "NUT-01", "NUT-02", "INV-01", "PAID-ACTIVATION-DEMO"];

    [Theory]
    [InlineData("BIEN-01", "Bienvenido a Copp Adresd, Ana. Tu acceso ya está listo", "Welcome to Copp Adresd, Ana. Your account is ready")]
    [InlineData("ACT-01", "Tu cuenta de Copp Adresd está activa", "Your Copp Adresd account is active")]
    [InlineData("ACT-02", "Ana, te falta un paso para empezar", "Ana, you’re one step away from getting started")]
    [InlineData("PWD-01", "Restablece tu contraseña de Copp Adresd", "Reset your Copp Adresd password")]
    [InlineData("NUT-01", "Ana, tu plan nutricional ya está listo", "Ana, your nutrition plan is ready")]
    [InlineData("NUT-02", "Tu plan nutricional fue actualizado", "Your nutrition plan was updated")]
    [InlineData("INV-01", "Inventario bajo: 6 días de A", "Low inventory: 6 days of A left")]
    public void Templates_RenderExpectedCopyAndAlternatives(string code, string spanish, string english)
    {
        var es = TransactionalEmailTemplate.Build(new(code, Recipient, "fr", Values()), new());
        var en = TransactionalEmailTemplate.Build(new(code, Recipient, "en-US", Values()), new());
        Assert.Equal(spanish, es.Subject);
        Assert.Equal(english, en.Subject);
        Assert.Equal(code, es.TemplateCode);
        Assert.Contains("lang=\"es\"", es.HtmlBody);
        Assert.Contains("lang=\"en\"", en.HtmlBody);
        Assert.Contains("<table role=\"presentation\"", en.HtmlBody);
        Assert.Contains("https://example.invalid/", es.PlainTextBody);
        Assert.Contains("https://example.invalid/", en.PlainTextBody);
        Assert.DoesNotContain("{{", es.HtmlBody);
        Assert.DoesNotContain("{{", en.HtmlBody);
        Assert.DoesNotContain("NA-DIAGNOSE-NA", es.PlainTextBody);
        Assert.DoesNotContain("NA-DOSE-NA", en.HtmlBody);
        Assert.DoesNotContain("NA-LOGIN-CODE-NA", en.HtmlBody);
        Assert.DoesNotContain("NA-PASSWORD-NA", es.PlainTextBody);
    }

    [Fact]
    public void Renderer_EncodesUntrustedVariablesAndTruncatesByTextElement()
    {
        var values = Values();
        values["nombre"] = "<img src=x onerror=alert(1)>\r\nBcc: audit";
        values["resumen_dia"] = string.Concat(Enumerable.Repeat("👩🏾‍🍳", 210)) + "BAD-TAIL";
        var mail = TransactionalEmailTemplate.Build(new("NUT-01", Recipient, "es", values), new());
        Assert.DoesNotContain("<img src=x", mail.HtmlBody);
        Assert.Contains("&lt;img", mail.HtmlBody);
        Assert.DoesNotContain("\r", mail.Subject);
        Assert.DoesNotContain("\n", mail.Subject);
        Assert.DoesNotContain("BAD-TAIL", mail.PlainTextBody);
        var summary = mail.PlainTextBody!.Split('\n').Single(line => line.StartsWith("• Hoy te toca: "));
        Assert.Equal(200, new StringInfo(summary["• Hoy te toca: ".Length..].TrimEnd('\r')).LengthInTextElements);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://example.invalid/reset")]
    [InlineData("http://127.0.0.1/reset")]
    [InlineData("https://user@example.invalid/reset")]
    public void RequiredLink_RejectsMissingOrUnsafeDestinations(string? target)
    {
        var values = Values();
        values["enlace_restablecer"] = target;
        Assert.Throws<ArgumentException>(() => TransactionalEmailTemplate.Build(new("PWD-01", Recipient, "es", values), new()));
    }

    [Theory]
    [InlineData("es-CO", "es", "Bienvenido a Copp Adresd", "Activa tu membresía")]
    [InlineData("en-US", "en", "Welcome to Copp Adresd", "Activate your membership")]
    public void PaidActivationDemo_StylesTheSameLinkInBothAlternatives(string locale, string lang, string subject, string cta)
    {
        const string link = "http://127.0.0.1:5196/activate#paymentId=fixture&token=test";
        var mail = TransactionalEmailTemplate.Build(new("PAID-ACTIVATION-DEMO", Recipient, locale,
            new Dictionary<string, string?> { ["enlace_activacion"] = link }), new());

        Assert.Equal("PAID-ACTIVATION-DEMO", mail.TemplateCode);
        Assert.Equal(subject, mail.Subject);
        Assert.Contains($"lang=\"{lang}\"", mail.HtmlBody);
        Assert.Contains($"href=\"{WebUtility.HtmlEncode(link)}\"", mail.HtmlBody);
        Assert.Contains(cta, WebUtility.HtmlDecode(mail.HtmlBody));
        Assert.Contains(cta, mail.PlainTextBody);
        Assert.Contains(link, mail.PlainTextBody);
    }

    [Theory]
    [InlineData("https://example.invalid/activate")]
    [InlineData("http://127.0.0.1@example.invalid/activate")]
    [InlineData("http://user@127.0.0.1/activate")]
    public void PaidActivationDemo_RejectsLinksOtherThanLoopbackWithoutCredentials(string link)
    {
        Assert.Throws<ArgumentException>(() => TransactionalEmailTemplate.Build(
            new("PAID-ACTIVATION-DEMO", Recipient, "es", new Dictionary<string, string?> { ["enlace_activacion"] = link }), new()));
    }

    [Fact]
    public void PasswordMail_RejectsRecipientMismatch()
    {
        var values = Values();
        values["correo_pago"] = "other@example.invalid";
        Assert.Throws<ArgumentException>(() => TransactionalEmailTemplate.Build(new("PWD-01", Recipient, null, values), new()));
    }

    [Fact]
    public void TemplateOverrides_AreLocalizedAndShareTheSameVariableContract()
    {
        var settings = new EmailSettings();
        var es = new TransactionalEmailCopy { Button = "Ver plan de {{nombre}}", Title = "Tu plan & metas" };
        settings.Transactional.Templates["NUT-01"] = new() { Es = es };
        var spanish = TransactionalEmailTemplate.Build(new("NUT-01", Recipient, "es-CO", Values()), settings);
        var english = TransactionalEmailTemplate.Build(new("NUT-01", Recipient, "en", Values()), settings);

        Assert.Contains("Ver plan de Ana", spanish.PlainTextBody);
        Assert.Contains("Tu plan &amp; metas", spanish.HtmlBody);
        Assert.DoesNotContain("Ver plan de Ana", english.PlainTextBody);
        es.Button = "{{dosis}}";
        Assert.Throws<InvalidOperationException>(() => TransactionalEmailTemplate.Build(new("NUT-01", Recipient, "es", Values()), settings));
    }

    [Fact]
    public async Task ResetLink_ExpiresAfterOneHourAndCannotBeRedeemedTwice()
    {
        Assert.Equal(TimeSpan.FromHours(1), new PasswordResetEmailTokenOptions().TokenLifespan);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection().AddLogging().AddDataProtection().UseEphemeralDataProtectionProvider().Services;
        services.AddDbContext<AuthDbContext>(options => options.UseSqlite(connection));
        services.AddAuthIdentity();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.EnsureCreatedAsync();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = Recipient, Email = Recipient, IsActive = true };
        Assert.True((await users.CreateAsync(user, "Original!113")).Succeeded);
        var issuer = scope.ServiceProvider.GetRequiredService<PasswordResetEmailLink>();
        var link = await issuer.IssueAsync(user, "https://example.invalid/reset?language=es");
        var query = QueryHelpers.ParseQuery(new Uri(link).Query);
        Assert.Equal(user.Id.ToString(), query["userId"].ToString());
        Assert.Equal("es", query["language"].ToString());
        var token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(query["token"].ToString()));
        var expiredTokens = new PasswordResetEmailTokenProvider(
            scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>(),
            Options.Create(new PasswordResetEmailTokenOptions { TokenLifespan = TimeSpan.FromTicks(-1) }),
            NullLogger<DataProtectorTokenProvider<ApplicationUser>>.Instance);
        Assert.False(await expiredTokens.ValidateAsync(UserManager<ApplicationUser>.ResetPasswordTokenPurpose,
            token, users, user));
        Assert.True((await users.ResetPasswordAsync(user, token, "FirstChange!321")).Succeeded);
        Assert.False((await users.ResetPasswordAsync(user, token, "SecondChange!456")).Succeeded);
    }

    [Fact]
    public async Task SmtpDeliversAllMultipartMessagesToLoopbackInbox()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var settings = new EmailSettings
        {
            From = "sender@example.invalid", SmtpHost = "127.0.0.1", SmtpUseSsl = false,
            SmtpPort = ((IPEndPoint)listener.LocalEndpoint).Port
        };
        var sender = new SmtpEmailSender(Options.Create(settings), NullLogger<SmtpEmailSender>.Instance);
        foreach (var code in Codes)
        {
            var inbox = Capture(listener, timeout.Token);
            await sender.SendAsync(TransactionalEmailTemplate.Build(new(code, Recipient, null, Values()), settings), timeout.Token);
            var mime = await inbox;
            Assert.Contains("Content-Type: multipart/alternative", mime, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Content-Type: text/plain", mime, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Content-Type: text/html", mime, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("To: " + Recipient, mime);
        }
    }

    private static async Task<string> Capture(TcpListener listener, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream);
        await using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };
        await writer.WriteLineAsync("220 localhost");
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line == "QUIT")
            {
                await writer.WriteLineAsync("221 closing connection");
                break;
            }
            if (line == "DATA")
            {
                await writer.WriteLineAsync("354 send data");
                var mime = new StringBuilder();
                while (await reader.ReadLineAsync(ct) is { } row && row != ".")
                    mime.AppendLine(row);
                await writer.WriteLineAsync("250 captured");
                await reader.ReadLineAsync(ct); // QUIT del cliente.
                await writer.WriteLineAsync("221 closing connection");
                return mime.ToString();
            }
            await writer.WriteLineAsync("250 OK");
        }
        throw new InvalidOperationException("No se recibió el mensaje.");
    }

    private static Dictionary<string, string?> Values() => new()
    {
        ["nombre"] = "Ana", ["correo_pago"] = Recipient,
        ["enlace_descarga"] = "https://example.invalid/download", ["enlace_plataforma"] = "https://example.invalid/app",
        ["enlace_restablecer"] = "https://example.invalid/reset?lang=es&token=opaque",
        ["enlace_activacion"] = "http://127.0.0.1:5196/activar-membresia#paymentId=fixture&token=test",
        ["enlace_nutricion"] = "https://example.invalid/nutrition", ["enlace_inventario"] = "https://example.invalid/inventory",
        ["fecha"] = "20 nov 2026", ["duracion"] = "4 semanas", ["resumen_dia"] = "Avena y fruta",
        ["medicamento"] = "A", ["cantidad"] = "12", ["dias_restantes"] = "6",
        ["diagnostico"] = "NA-DIAGNOSE-NA", ["dosis"] = "NA-DOSE-NA",
        ["codigo_acceso"] = "NA-LOGIN-CODE-NA", ["contrasena"] = "NA-PASSWORD-NA",
    };
}
