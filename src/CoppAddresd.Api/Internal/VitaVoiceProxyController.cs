using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using CoppAddresd.Api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Internal;

/// <summary>Puente SSE sin generación ni persistencia; AI valida token, hilo, conversación y turno.</summary>
[ApiController]
[AllowAnonymous]
[RequireVitaVoiceKey]
[Route("api/v1/internal/vita/voice/v1")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class VitaVoiceProxyController(
    IHttpClientFactory clients, IConfiguration config, ILogger<VitaVoiceProxyController> logger) : ControllerBase
{
    [HttpPost("chat/completions")]
    // Historial + schemas de tools del proveedor pueden superar 64 KiB; mantener un límite acotado.
    [RequestSizeLimit(1024 * 1024)]
    public async Task Stream([FromBody] JsonElement body, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "transport_error";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var names = new[] { "X-Vita-Session", "X-Vita-Conversation", "X-Vita-Turn" };
            var context = names.ToDictionary(n => n, n => Request.Headers[n]);
            if (context.Values.Any(v => v.Count != 1)
                || !SafeOpaque(context[names[0]].ToString(), 4096, allowDot: true)
                || !SafeOpaque(context[names[1]].ToString(), 200, allowDot: false)
                || !int.TryParse(context[names[2]].ToString(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var turn) || turn is < 0 or > 1_000_000)
            {
                outcome = "invalid_context";
                Response.StatusCode = 422;
                await Response.WriteAsJsonAsync(new { code = "VITA_VOICE_CONTEXT_INVALID" }, ct);
                return;
            }

            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/voice/v1/chat/completions")
            { Content = JsonContent.Create(body) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["Vita:VoiceServiceKey"]);
            // Transporte observado en el POC real. Solo estos encabezados de contexto cruzan el proxy.
            foreach (var name in names) request.Headers.Add(name, context[name].ToString());
            using var response = await clients.CreateClient("VitaVoice").SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                outcome = "upstream_rejected";
                Response.StatusCode = (int)response.StatusCode is 400 or 401 or 403 or 404 or 409 or 422 or 429
                    ? (int)response.StatusCode : 503;
                await Response.WriteAsJsonAsync(new { code = "VITA_VOICE_REQUEST_REJECTED" }, ct);
                return;
            }
            if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            {
                outcome = "invalid_content_type";
                Response.StatusCode = 502;
                await Response.WriteAsJsonAsync(new { code = "VITA_VOICE_PROTOCOL_ERROR" }, ct);
                return;
            }
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-store";
            Response.Headers["X-Accel-Buffering"] = "no";
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var buffer = new byte[8192];
            var terminal = new TerminalSseFrame();
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                await Response.Body.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
                await Response.Body.FlushAsync(deadline.Token);
                terminal.Observe(buffer.AsSpan(0, read));
            }

            // Un EOF sin el evento terminal de Vita no prueba que se haya terminado la respuesta.
            outcome = ct.IsCancellationRequested ? "client_aborted"
                : deadline.IsCancellationRequested ? "timeout"
                : terminal.Forwarded ? "completed" : "incomplete";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = "client_aborted";
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            outcome = ct.IsCancellationRequested ? "client_aborted"
                : ex is OperationCanceledException ? "timeout" : "transport_error";
            if (Response.HasStarted) HttpContext.Abort();
            else { Response.StatusCode = 503; await Response.WriteAsJsonAsync(new { code = "VITA_VOICE_UNAVAILABLE" }, ct); }
        }
        catch
        {
            outcome = ct.IsCancellationRequested ? "client_aborted" : "transport_error";
            throw;
        }
        finally
        {
            logger.LogInformation("VitaVoiceProxyOutcome {Outcome} {ProxyRequestId} {StatusCode} {DurationMs}",
                outcome, HttpContext.TraceIdentifier, Response.HasStarted ? Response.StatusCode : (int?)null,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    // Firma del evento que emite AI. Solo retiene el estado de coincidencia, nunca el cuerpo.
    private struct TerminalSseFrame
    {
        private int _matched;
        private bool _frameHasLine;
        private bool _terminalLineOnly;
        private bool _afterCarriageReturn;
        public bool Forwarded { get; private set; }

        public void Observe(ReadOnlySpan<byte> bytes)
        {
            if (Forwarded) return;
            ReadOnlySpan<byte> signature = "data: [DONE]"u8;
            foreach (var value in bytes)
            {
                if (_afterCarriageReturn && value == '\n')
                {
                    _afterCarriageReturn = false;
                    continue;
                }

                _afterCarriageReturn = value == '\r';
                if (_afterCarriageReturn || value == '\n')
                {
                    if (_matched == 0)
                    {
                        if (_terminalLineOnly) { Forwarded = true; return; }
                        _frameHasLine = false;
                    }
                    else
                    {
                        _terminalLineOnly = !_frameHasLine && _matched == signature.Length;
                        _frameHasLine = true;
                        _matched = 0;
                    }
                }
                else if (_matched >= 0)
                {
                    _matched = _matched < signature.Length && value == signature[_matched] ? _matched + 1 : -1;
                }
            }
        }
    }

    private static bool SafeOpaque(string value, int max, bool allowDot) => value.Length is > 0
        && value.Length <= max && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' || allowDot && c == '.');
}
