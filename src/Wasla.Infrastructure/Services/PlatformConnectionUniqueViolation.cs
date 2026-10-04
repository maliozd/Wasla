using Microsoft.EntityFrameworkCore;

namespace Wasla.Infrastructure.Services;

internal static class PlatformConnectionUniqueViolation
{
    internal const string IndexName = "IX_PlatformConnections_Platform";

    internal static bool IsPlatformUnique(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains(IndexName, StringComparison.Ordinal))
                return true;

            if (message.Contains(
                    "UNIQUE constraint failed: PlatformConnections.Platform",
                    StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
