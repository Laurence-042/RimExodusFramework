namespace RimExodus.Maps
{
    internal static class MapRateSchedule
    {
        // Opportunity count is independent of the engine's Normal/Rare/Long bucket interval.
        internal static bool Allows(int opportunity, int percent)
        {
            if (percent <= 0) return false;
            if (percent >= 100) return true;
            long n = opportunity;
            return Floor(n * percent, 100) != Floor((n - 1) * percent, 100);
        }
        private static long Floor(long n, long d) => n >= 0 ? n / d : (n - d + 1) / d;
    }
}
