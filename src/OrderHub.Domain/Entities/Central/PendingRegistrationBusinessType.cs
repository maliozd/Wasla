namespace OrderHub.Domain.Entities.Central;

public class PendingRegistrationBusinessType
{
    public Guid PendingRegistrationId { get; set; }

    public int BusinessTypeId { get; set; }

    public PendingRegistration PendingRegistration { get; set; } = default!;

    public BusinessType BusinessType { get; set; } = default!;
}
