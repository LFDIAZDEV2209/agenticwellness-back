using System.Security.Cryptography;
using CoppAddresd.Auth.Data;
using CoppAddresd.Auth.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Auth.Services;

public sealed record PaidMembershipDemoResult(PaidActivationReceipt? Receipt, bool Conflict = false);

public sealed class PaidMembershipDemoService(AuthDbContext db)
{
    // Solo un plazo sintético para la simulación. No configura una prueba de producción.
    internal const int TrialDays = 7;

    internal static byte[] HashCheckoutKey(Guid key) => SHA256.HashData(key.ToByteArray());

    internal static DateTime AddPeriod(DateTime start, string plan) =>
        start.AddMonths(plan == "annual" ? 12 : 1);

    internal static DateTime ExpiresAt(PaidActivation payment) =>
        payment.MembershipExpiresAt ?? AddPeriod(payment.ConfirmedAt, payment.Plan);

    private static string State(PaidActivation payment, DateTime now) =>
        payment.MembershipState == "canceled" ? "canceled" :
        ExpiresAt(payment) <= now ? "expired" :
        payment.MembershipState;

    internal static bool IsActive(PaidActivation payment, DateTime now) =>
        payment.MembershipPaid && State(payment, now) == "active";

    internal static PaidActivationReceipt Receipt(PaidActivation payment, DateTime now) =>
        new(payment.PaymentId, payment.Plan, payment.AmountCents, "USD", State(payment, now),
            ExpiresAt(payment), payment.MembershipRevision, payment.MembershipPaid);

    public async Task<PaidMembershipDemoResult> ApplyAsync(
        Guid paymentId, Guid eventId, Guid checkoutKey, int expectedRevision, string kind, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var payment = await db.PaidActivations
            .FromSqlInterpolated($"""SELECT * FROM auth."PaidActivations" WHERE "PaymentId" = {paymentId} FOR UPDATE""")
            .SingleOrDefaultAsync(ct);
        if (payment?.CheckoutKeyHash is not { } hash ||
            !CryptographicOperations.FixedTimeEquals(hash, HashCheckoutKey(checkoutKey)))
            return new(null);

        var now = DateTime.UtcNow;
        var previous = await db.PaidMembershipDemoEvents.FindAsync([paymentId, eventId], ct);
        if (previous is not null)
            return previous.Kind == kind && previous.Revision - 1 == expectedRevision
                ? new(Receipt(payment, now))
                : new(null, Conflict: true);

        if (payment.MembershipRevision != expectedRevision || payment.MembershipState == "canceled")
            return new(null, Conflict: true);

        var amount = 0;
        switch (kind)
        {
            case "activate" when !payment.MembershipPaid:
                payment.MembershipState = "active";
                payment.MembershipPaid = true;
                payment.ConfirmedAt = now;
                payment.MembershipExpiresAt = AddPeriod(now, payment.Plan);
                payment.LastEmailRequestedAt = now;
                payment.NextEmailAttemptAt = now;
                amount = payment.AmountCents;
                break;
            case "renew" when payment.MembershipPaid:
                var end = ExpiresAt(payment);
                payment.MembershipState = "active";
                payment.MembershipExpiresAt = AddPeriod(end > now ? end : now, payment.Plan);
                amount = payment.AmountCents;
                break;
            case "expire" when State(payment, now) is "trial" or "active":
                payment.MembershipState = "expired";
                payment.MembershipExpiresAt = now;
                break;
            case "cancel":
                payment.MembershipState = "canceled";
                payment.MembershipExpiresAt = now;
                break;
            default:
                return new(null, Conflict: true);
        }

        // No se distribuyen enlaces de una membresía sin acceso vigente.
        if (!IsActive(payment, now))
        {
            payment.NextEmailAttemptAt = null;
            payment.EmailLeaseUntil = null;
        }

        payment.MembershipRevision++;
        db.PaidMembershipDemoEvents.Add(new()
        {
            PaymentId = payment.PaymentId,
            EventId = eventId,
            Revision = payment.MembershipRevision,
            Kind = kind,
            AmountCents = amount,
            OccurredAt = now
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(Receipt(payment, now));
    }
}
