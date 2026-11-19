using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.WhatsApp.Messaging;
using CoppAddresd.Domain.Entities.WhatsApp;
using CoppAddresd.WhatsApp.Messaging;
using CoppAddresd.WhatsApp.Persistence;
using CoppAddresd.WhatsApp.Webhook;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CoppAddresd.WhatsApp.Tests;

/// <summary>KAN-54: recibos de Meta (statuses) persistidos y conciliados con los salientes.</summary>
[Trait("Category", "Integration")]
public sealed class ReceiptTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_795_000_000);

    [Fact]
    public async Task SignedStatus_PersistsReceipt_AndAdvancesOutbound()
    {
        var wamid = NewWamid();
        await SeedOutboundAsync(wamid);

        Assert.Equal(200, await AcceptAsync(Statuses(Status(wamid, "delivered", T0))));

        await using var db = fixture.CreateDb();
        var receipt = await db.MessageReceipts.SingleAsync(x => x.Wamid == wamid);
        Assert.Equal(MessageStatus.delivered, receipt.Status);
        Assert.Equal("573053924819", receipt.RecipientId);
        Assert.Equal(T0, receipt.OccurredAt);
        Assert.Null(receipt.ErrorCode);
        var outbound = await db.OutboundMessages.SingleAsync(x => x.Wamid == wamid);
        Assert.Equal(MessageStatus.delivered, outbound.Status);
        Assert.Equal(T0, outbound.StatusAt);
    }

    [Fact]
    public async Task ExactDuplicate_StoresOneRow_AndNeverRegresses()
    {
        var wamid = NewWamid();
        await SeedOutboundAsync(wamid);
        var read = Statuses(Status(wamid, "read", T0.AddSeconds(5)));

        Assert.Equal(200, await AcceptAsync(read));
        Assert.Equal(200, await AcceptAsync(read));
        Assert.Equal(200, await AcceptAsync(Statuses(Status(wamid, "delivered", T0))));

        await using var db = fixture.CreateDb();
        Assert.Equal(
            1,
            await db.MessageReceipts.CountAsync(x =>
                x.Wamid == wamid && x.Status == MessageStatus.read
            )
        );
        var outbound = await db.OutboundMessages.SingleAsync(x => x.Wamid == wamid);
        Assert.Equal(MessageStatus.read, outbound.Status);
        Assert.Equal(T0.AddSeconds(5), outbound.StatusAt);
    }

    [Fact]
    public async Task InvertedOrder_ReadBeforeSent_EndsRead()
    {
        var wamid = NewWamid();
        await SeedOutboundAsync(wamid);

        // Ambos estados en el mismo payload, en orden invertido.
        Assert.Equal(
            200,
            await AcceptAsync(
                Statuses(
                    Status(wamid, "read", T0.AddSeconds(9)),
                    Status(wamid, "delivered", T0.AddSeconds(3)),
                    Status(wamid, "sent", T0)
                )
            )
        );

        await using var db = fixture.CreateDb();
        Assert.Equal(3, await db.MessageReceipts.CountAsync(x => x.Wamid == wamid));
        Assert.Equal(
            MessageStatus.read,
            (await db.OutboundMessages.SingleAsync(x => x.Wamid == wamid)).Status
        );
    }

    [Fact]
    public async Task UnknownWamid_PersistsReceipt_WithoutError()
    {
        var wamid = NewWamid();

        Assert.Equal(200, await AcceptAsync(Statuses(Status(wamid, "sent", T0))));

        await using var db = fixture.CreateDb();
        Assert.Equal(
            MessageStatus.sent,
            (await db.MessageReceipts.SingleAsync(x => x.Wamid == wamid)).Status
        );
        Assert.False(await db.OutboundMessages.AnyAsync(x => x.Wamid == wamid));
    }

    [Fact]
    public async Task UnsupportedStatusOrOtherPhoneNumber_IsIgnored()
    {
        var wamid = NewWamid();

        Assert.Equal(200, await AcceptAsync(Statuses(Status(wamid, "deleted", T0))));
        Assert.Equal(200, await AcceptAsync(Statuses("other-phone", Status(wamid, "sent", T0))));

        await using var db = fixture.CreateDb();
        Assert.False(await db.MessageReceipts.AnyAsync(x => x.Wamid == wamid));
    }

    [Fact]
    public async Task FailedWithMetaError_WinsOverRead_AndRecordsErrorCode()
    {
        var wamid = NewWamid();
        await SeedOutboundAsync(wamid);

        Assert.Equal(200, await AcceptAsync(Statuses(Status(wamid, "read", T0))));
        Assert.Equal(
            200,
            await AcceptAsync(
                Statuses(Status(wamid, "failed", T0.AddSeconds(1), errorCode: 131047))
            )
        );
        Assert.Equal(200, await AcceptAsync(Statuses(Status(wamid, "read", T0.AddSeconds(2)))));

        await using var db = fixture.CreateDb();
        var failed = await db.MessageReceipts.SingleAsync(x =>
            x.Wamid == wamid && x.Status == MessageStatus.failed
        );
        Assert.Equal("meta:131047", failed.ErrorCode);
        var outbound = await db.OutboundMessages.SingleAsync(x => x.Wamid == wamid);
        Assert.Equal(MessageStatus.failed, outbound.Status);
        Assert.Equal("meta:131047", outbound.ErrorCode);
    }

    [Fact]
    public async Task ReminderDeliveryWithWamid_ReceivesReceiptStatus()
    {
        var wamid = NewWamid();
        var deliveryId = await SeedReminderDeliveryAsync(wamid);

        Assert.Equal(200, await AcceptAsync(Statuses(Status(wamid, "delivered", T0))));
        Assert.Equal(200, await AcceptAsync(Statuses(Status(wamid, "sent", T0.AddSeconds(-2)))));

        await using var db = fixture.CreateDb();
        var delivery = await db.ReminderDeliveries.SingleAsync(x => x.Id == deliveryId);
        Assert.Equal(MessageStatus.delivered, delivery.ReceiptStatus);
        Assert.Equal(T0, delivery.ReceiptAt);
        // El estado de despacho no se toca: los recibos viven en columnas propias.
        Assert.Equal(DeliveryState.Accepted, delivery.State);
    }

    [Fact]
    public async Task ReceiptBeforeAck_IsReconciledForReminderDelivery()
    {
        var wamid = NewWamid();
        Assert.Equal(200, await AcceptAsync(Statuses(Status(wamid, "read", T0))));
        var deliveryId = await SeedReminderDeliveryAsync(wamid);

        await using var db = fixture.CreateDb();
        Assert.Equal(
            1,
            await MessageReceiptLedger.ReconcileReminderAsync(db, wamid, CancellationToken.None)
        );
        Assert.Equal(
            0,
            await MessageReceiptLedger.ReconcileReminderAsync(db, wamid, CancellationToken.None)
        );
        var delivery = await db
            .ReminderDeliveries.AsNoTracking()
            .SingleAsync(x => x.Id == deliveryId);
        Assert.Equal(MessageStatus.read, delivery.ReceiptStatus);
    }

    [Fact]
    public async Task SosReceiver_StillReceivesCorrelatedStatus_AfterLocalReceipt()
    {
        var wamid = NewWamid();
        var sos = Substitute.For<ISosWhatsAppStatusReceiver>();
        var correlation = "sos:" + Guid.NewGuid();

        Assert.Equal(
            200,
            await AcceptAsync(
                Statuses(Status(wamid, "delivered", T0, correlation: correlation)),
                sos
            )
        );
        // Sin correlación, SOS no se invoca (semántica previa).
        Assert.Equal(200, await AcceptAsync(Statuses(Status(NewWamid(), "delivered", T0)), sos));

        await sos.Received(1)
            .ReceiveAsync(
                correlation,
                wamid,
                "573053924819",
                "delivered",
                T0.UtcDateTime,
                null,
                Arg.Any<CancellationToken>()
            );
        await using var db = fixture.CreateDb();
        Assert.True(await db.MessageReceipts.AnyAsync(x => x.Wamid == wamid));
    }

    [Fact]
    public async Task SosReceiverFailure_KeepsLocalReceipt_AndAcknowledges()
    {
        var wamid = NewWamid();
        var sos = Substitute.For<ISosWhatsAppStatusReceiver>();
        sos.ReceiveAsync(default!, default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(
                Task.FromException(new InvalidOperationException("detalle interno"))
            );

        Assert.Equal(
            200,
            await AcceptAsync(
                Statuses(Status(wamid, "sent", T0, correlation: "sos:" + Guid.NewGuid())),
                sos
            )
        );

        await using var db = fixture.CreateDb();
        Assert.True(
            await db.MessageReceipts.AnyAsync(x =>
                x.Wamid == wamid && x.Status == MessageStatus.sent
            )
        );
    }

    [Fact]
    public async Task InboundReply_CreatesOutboundPerFragment_AndReconcilesEarlyReceipt()
    {
        var waId = "+57300" + Random.Shared.Next(1_000_000, 9_999_999);
        var first = NewWamid();
        var second = NewWamid();
        // El recibo del primer fragmento llega antes del ACK del envío.
        Assert.Equal(200, await AcceptAsync(Statuses(Status(first, "delivered", T0))));
        var eventId = await SeedPendingReplyAsync(waId, ["primer fragmento", "segundo fragmento"]);
        var wamids = new Queue<string>([first, second]);
        var sender = Substitute.For<IWhatsAppMessageSender>();
        sender
            .SendTextAsync(default!, default!, default, default)
            .ReturnsForAnyArgs(_ => new WhatsAppSendResult(wamids.Dequeue()));

        await using (var db = fixture.CreateDb())
            Assert.True(await CreateProcessor(db, sender).ProcessNextAsync(CancellationToken.None));

        await using var verify = fixture.CreateDb();
        var raw = await verify.RawEvents.SingleAsync(x => x.Id == eventId);
        Assert.Equal(2, raw.SentFragments);
        Assert.NotNull(raw.ProcessedAt);
        var rows = await verify
            .OutboundMessages.Where(x => x.RawEventId == eventId)
            .OrderBy(x => x.FragmentIndex)
            .ToListAsync();
        Assert.Equal([first, second], rows.Select(x => x.Wamid));
        Assert.Equal([0, 1], rows.Select(x => x.FragmentIndex));
        Assert.All(rows, x => Assert.Equal(waId, x.WaId));
        Assert.All(rows, x => Assert.NotNull(x.AcceptedAt));
        Assert.Equal(MessageStatus.delivered, rows[0].Status);
        Assert.Equal(MessageStatus.sent, rows[1].Status);
    }

    [Fact]
    public async Task RejectedFragment_LeavesFailedRow_AndKeepsRetryPolicy()
    {
        var waId = "+57301" + Random.Shared.Next(1_000_000, 9_999_999);
        var eventId = await SeedPendingReplyAsync(waId, ["fragmento"]);
        var sender = Substitute.For<IWhatsAppMessageSender>();
        sender
            .SendTextAsync(default!, default!, default, default)
            .ReturnsForAnyArgs(
                WhatsAppSendResult.Failed("http_503", WhatsAppFailureKind.Transient)
            );

        await using (var db = fixture.CreateDb())
            Assert.True(await CreateProcessor(db, sender).ProcessNextAsync(CancellationToken.None));

        await using var verify = fixture.CreateDb();
        var row = await verify.OutboundMessages.SingleAsync(x => x.RawEventId == eventId);
        Assert.Null(row.Wamid);
        Assert.Equal(MessageStatus.failed, row.Status);
        Assert.Equal("http_503", row.ErrorCode);
        var raw = await verify.RawEvents.SingleAsync(x => x.Id == eventId);
        Assert.Null(raw.ProcessedAt);
        Assert.Equal(0, raw.SentFragments);
        Assert.Equal("http_503", raw.ErrorCode);
        // Evita que el evento pendiente interfiera con otros tests de la clase.
        await verify
            .RawEvents.Where(x => x.Id == eventId)
            .ExecuteUpdateAsync(x => x.SetProperty(e => e.ProcessedAt, DateTimeOffset.UtcNow));
    }

    // ---------- Helpers ----------

    private static string NewWamid() => "wamid.HBgM" + Guid.NewGuid().ToString("N");

    private async Task<int> AcceptAsync(string body, ISosWhatsAppStatusReceiver? sos = null)
    {
        await using var db = fixture.CreateDb();
        var webhook = new WhatsAppWebhook(
            db,
            WebhookTests.Options,
            new WhatsAppQueue(),
            TimeProvider.System,
            NullLogger<WhatsAppWebhook>.Instance,
            sos
        );
        var bytes = Encoding.UTF8.GetBytes(body);
        var signature =
            "sha256="
            + Convert.ToHexStringLower(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookTests.Options.AppSecret), bytes)
            );
        return await webhook.AcceptAsync(bytes, signature, CancellationToken.None);
    }

    private static object Status(
        string wamid,
        string status,
        DateTimeOffset at,
        int? errorCode = null,
        string? correlation = null
    )
    {
        var item = new Dictionary<string, object>
        {
            ["id"] = wamid,
            ["status"] = status,
            ["timestamp"] = at.ToUnixTimeSeconds().ToString(),
            ["recipient_id"] = "573053924819",
        };
        if (errorCode is not null)
            item["errors"] = new[]
            {
                new { code = errorCode.Value, title = "Message undeliverable" },
            };
        if (correlation is not null)
            item["biz_opaque_callback_data"] = correlation;
        return item;
    }

    private static string Statuses(params object[] statuses) => Statuses("test-phone", statuses);

    private static string Statuses(string phoneNumberId, params object[] statuses) =>
        JsonSerializer.Serialize(
            new
            {
                @object = "whatsapp_business_account",
                entry = new[]
                {
                    new
                    {
                        changes = new[]
                        {
                            new
                            {
                                field = "messages",
                                value = new
                                {
                                    metadata = new { phone_number_id = phoneNumberId },
                                    statuses,
                                },
                            },
                        },
                    },
                },
            }
        );

    private async Task SeedOutboundAsync(string wamid)
    {
        await using var db = fixture.CreateDb();
        var raw = new WhatsAppRawEvent
        {
            Wamid = NewWamid(),
            Payload = "{}",
            WaId = "+573053924819",
            ReceivedAt = T0,
            MessageAt = T0,
            NextAttemptAt = T0,
            ProcessedAt = T0,
        };
        db.RawEvents.Add(raw);
        db.OutboundMessages.Add(
            new OutboundMessage
            {
                Wamid = wamid,
                WaId = raw.WaId,
                RawEventId = raw.Id,
                AcceptedAt = T0,
                StatusAt = T0,
            }
        );
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedReminderDeliveryAsync(string wamid)
    {
        await using var db = fixture.CreateDb();
        var reminder = new Reminder
        {
            PatientId = Guid.NewGuid(),
            SourceKey = "kan54:" + Guid.NewGuid(),
            ScheduledAt = T0,
            NextAttemptAt = T0,
            State = ReminderState.Completed,
        };
        var delivery = new ReminderDelivery
        {
            ReminderId = reminder.Id,
            PatientId = reminder.PatientId,
            Attempt = 1,
            Wamid = wamid,
            State = DeliveryState.Accepted,
            AttemptedAt = T0,
            AcceptedAt = T0,
        };
        db.Reminders.Add(reminder);
        db.ReminderDeliveries.Add(delivery);
        await db.SaveChangesAsync();
        return delivery.Id;
    }

    private async Task<Guid> SeedPendingReplyAsync(string waId, string[] fragments)
    {
        await using var db = fixture.CreateDb();
        var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        var raw = new WhatsAppRawEvent
        {
            Wamid = NewWamid(),
            Payload = "{}",
            WaId = waId,
            Text = "hola",
            ReceivedAt = now,
            MessageAt = now,
            NextAttemptAt = now,
            Reply = string.Join(" ", fragments),
            ReplyFragments = fragments,
        };
        db.RawEvents.Add(raw);
        await db.SaveChangesAsync();
        return raw.Id;
    }

    private static WhatsAppInboundProcessor CreateProcessor(
        WhatsAppDbContext db,
        IWhatsAppMessageSender sender
    )
    {
        var mediator = Substitute.For<ISender>();
        var clock = TimeProvider.System;
        return new WhatsAppInboundProcessor(
            db,
            new WhatsAppPatientResolver(db, mediator, sender, clock),
            mediator,
            sender,
            clock,
            NullLogger<WhatsAppInboundProcessor>.Instance
        );
    }
}

/// <summary>Máquina de estados de dominio: debe coincidir con el CASE SQL del ledger.</summary>
[Trait("Category", "Unit")]
public sealed class MessageStatusOrderTests
{
    [Theory]
    [InlineData(null, MessageStatus.sent, true)]
    [InlineData(MessageStatus.sent, MessageStatus.delivered, true)]
    [InlineData(MessageStatus.delivered, MessageStatus.read, true)]
    [InlineData(MessageStatus.read, MessageStatus.failed, true)]
    [InlineData(MessageStatus.read, MessageStatus.delivered, false)]
    [InlineData(MessageStatus.read, MessageStatus.read, false)]
    [InlineData(MessageStatus.failed, MessageStatus.read, false)]
    public void Advances_IsMonotonic_AndFailedWins(
        MessageStatus? current,
        MessageStatus next,
        bool expected
    ) => Assert.Equal(expected, MessageStatusOrder.Advances(current, next));

    [Theory]
    [InlineData("sent", MessageStatus.sent)]
    [InlineData("delivered", MessageStatus.delivered)]
    [InlineData("read", MessageStatus.read)]
    [InlineData("failed", MessageStatus.failed)]
    [InlineData("deleted", null)]
    [InlineData(null, null)]
    public void Parse_AcceptsOnlyKnownMetaStatuses(string? value, MessageStatus? expected) =>
        Assert.Equal(expected, MessageStatusOrder.Parse(value));

    [Fact]
    public void MaskWamid_NeverReturnsTheFullIdentifier()
    {
        var wamid = "wamid.HBgMNTczMDUzOTI0ODE5FQIAERgSQUJDREVGMTIzNDU2";
        var masked = MessageReceiptLedger.MaskWamid(wamid);
        Assert.NotEqual(wamid, masked);
        Assert.EndsWith(wamid[^8..], masked);
        Assert.Equal("***", MessageReceiptLedger.MaskWamid("corto"));
    }
}
