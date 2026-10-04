namespace Wasla.Web.Models.Ui;

/// <summary>An order's received time for table cells: <paramref name="Local"/> is Turkey local time.</summary>
public sealed record ReceivedAtTimeModel(DateTime Utc, DateTime Local, DateOnly Today);
