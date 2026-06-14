namespace Wasla.Application.Notifications;

public static class NotificationHighlightOptions
{
    public const string DefaultColor = "yellow";
    public const string DefaultBehavior = "fade";
    public const int DefaultDurationSeconds = 30;

    public static readonly string[] Colors = { "orange", "blue", "green", "yellow", "red" };
    public static readonly string[] Behaviors = { "fade", "pulse", "blink", "borderGlow", "none" };
    public static readonly int[] DurationsSeconds = { 10, 20, 30, 60 };

    public static string NormalizeColor(string? v)
    {
        var s = (v ?? string.Empty).Trim().ToLowerInvariant();
        if (Colors.Contains(s)) return s;
        if (IsHexColor(s)) return s;
        return DefaultColor;
    }

    public static string NormalizeBehavior(string? v)
    {
        var s = (v ?? string.Empty).Trim();
        if (string.Equals(s, "border-glow", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "borderglow", StringComparison.OrdinalIgnoreCase))
            return "borderGlow";

        var lower = s.ToLowerInvariant();
        var match = Behaviors.FirstOrDefault(b => string.Equals(b, lower, StringComparison.OrdinalIgnoreCase));
        return match ?? DefaultBehavior;
    }

    public static int NormalizeDurationSeconds(int v) =>
        DurationsSeconds.Contains(v) ? v : DefaultDurationSeconds;

    public static bool IsHexColor(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return false;
        var s = v.Trim();
        if (s.Length != 7) return false;
        if (s[0] != '#') return false;
        for (var i = 1; i < 7; i++)
        {
            var c = s[i];
            var isHex = (c >= '0' && c <= '9')
                        || (c >= 'a' && c <= 'f')
                        || (c >= 'A' && c <= 'F');
            if (!isHex) return false;
        }
        return true;
    }
}
