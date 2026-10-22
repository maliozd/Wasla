namespace Wasla.Domain.Billing;

/// <summary>
/// A provider-verified paid period, half-open: it covers <see cref="StartUtc"/> and ends at <see cref="EndUtc"/>.
/// </summary>
public readonly record struct BillingPaidPeriod
{
    public BillingPaidPeriod(DateTime startUtc, DateTime endUtc)
    {
        if (endUtc <= startUtc)
            throw new ArgumentException("A paid period ends after it starts.", nameof(endUtc));

        StartUtc = startUtc;
        EndUtc = endUtc;
    }

    public DateTime StartUtc { get; }

    public DateTime EndUtc { get; }

    public bool Covers(DateTime nowUtc) => StartUtc <= nowUtc && nowUtc < EndUtc;
}
