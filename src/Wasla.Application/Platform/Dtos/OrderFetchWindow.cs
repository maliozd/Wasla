namespace Wasla.Application.Platform.Dtos;

/// <summary>
/// The provider modification-time interval one order fetch asks for. Both bounds are UTC and are sent to the
/// provider as given, so consecutive windows share their boundary instant. A client that does not filter by
/// modification time ignores the window (see <see cref="Wasla.Application.Abstractions.Platform.IFoodPlatformClient.MaxFetchWindow"/>).
/// </summary>
public sealed record OrderFetchWindow(DateTime StartUtc, DateTime EndUtc);
