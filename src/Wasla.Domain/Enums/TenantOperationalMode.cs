namespace Wasla.Domain.Enums;

/// <summary>
/// Whether the restaurant is operating yet. This is tenant state, separate from each user's guided-setup choice.
/// A new tenant starts in <see cref="Setup"/>: orders are still synchronized and kept, but nothing is accepted or
/// printed automatically. The first user who completes or skips guided setup moves the tenant to <see cref="Live"/>,
/// and normal flows never move it back.
/// <para>
/// <see cref="Live"/> is 0 on purpose: every settings row that predates this value, every tenant without a settings
/// row, and every row created later for such a tenant reads as Live, so existing restaurants keep operating.
/// </para>
/// </summary>
public enum TenantOperationalMode
{
    Live = 0,
    Setup = 1
}
