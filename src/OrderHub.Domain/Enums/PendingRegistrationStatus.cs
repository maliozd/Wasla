namespace OrderHub.Domain.Enums;

public enum PendingRegistrationStatus
{
    Draft = 0,
    AwaitingPayment = 1,
    PaymentSucceeded = 2,
    PaymentFailed = 3,
    Cancelled = 4,
    Expired = 5,
    Provisioned = 6
}
