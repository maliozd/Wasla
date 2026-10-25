using System.Linq.Expressions;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// Which pending registrations hold their slug, domain and database name. A registration waiting for payment (Draft,
/// AwaitingPayment) holds them until it expires. A paid one (PaymentSucceeded) holds them until it is resolved, whatever
/// its expiry says: payment was taken for that address, so it must not pass to another signup before provisioning
/// (WAS-93).
/// </summary>
internal static class PendingRegistrationReservations
{
    public static Expression<Func<PendingRegistration, bool>> HoldsReservation(DateTime utcNow) =>
        p => p.Status == PendingRegistrationStatus.PaymentSucceeded
             || ((p.Status == PendingRegistrationStatus.Draft || p.Status == PendingRegistrationStatus.AwaitingPayment)
                 && (p.ExpiresAtUtc == null || p.ExpiresAtUtc > utcNow));
}
