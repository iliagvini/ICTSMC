using System;

namespace ICTSMC
{
    /// <summary>
    /// Pure, host-independent decision rules used by the live indicator and the
    /// regression harness. Keeping these rules free of ATAS state prevents the
    /// historical/realtime paths from silently adopting different definitions.
    /// </summary>
    internal static class StrictRules
    {
        public static LiquidityDisposition ClassifyLiquidity(
            bool buySide,
            decimal high,
            decimal low,
            decimal close,
            decimal level,
            decimal tickSize,
            int minimumPenetrationTicks,
            int reclaimTicks)
        {
            var minimumPenetration = Math.Max(0, minimumPenetrationTicks) * tickSize;
            var reclaim = Math.Max(0, reclaimTicks) * tickSize;
            var penetration = buySide ? high - level : level - low;

            if (penetration < minimumPenetration)
                return LiquidityDisposition.Indeterminate;

            // A strict reversal setup requires the close to reclaim the level.
            // An exact close is intentionally neutral rather than being silently
            // promoted to a trap or a run.
            if (buySide)
            {
                if (close < level - reclaim)
                    return LiquidityDisposition.ConfirmedTrap;
                if (close > level + reclaim)
                    return LiquidityDisposition.Run;
            }
            else
            {
                if (close > level + reclaim)
                    return LiquidityDisposition.ConfirmedTrap;
                if (close < level - reclaim)
                    return LiquidityDisposition.Run;
            }

            return LiquidityDisposition.Indeterminate;
        }

        public static bool HasOhlcIntersection(decimal high, decimal low, decimal top, decimal bottom) =>
            high >= bottom && low <= top;

        public static ZoneContactKind ClassifyObservedContact(
            bool bullish,
            decimal previousPrice,
            decimal currentPrice,
            decimal top,
            decimal bottom)
        {
            if (top < bottom)
                return ZoneContactKind.None;

            if (bullish)
            {
                if (previousPrice > top)
                {
                    if (currentPrice <= top && currentPrice >= bottom)
                        return ZoneContactKind.EnteredFromExpectedSide;
                    if (currentPrice < bottom)
                        return ZoneContactKind.GapThrough;
                }

                return currentPrice >= bottom && currentPrice <= top
                    ? ZoneContactKind.AlreadyInside
                    : ZoneContactKind.None;
            }

            if (previousPrice < bottom)
            {
                if (currentPrice >= bottom && currentPrice <= top)
                    return ZoneContactKind.EnteredFromExpectedSide;
                if (currentPrice > top)
                    return ZoneContactKind.GapThrough;
            }

            return currentPrice >= bottom && currentPrice <= top
                ? ZoneContactKind.AlreadyInside
                : ZoneContactKind.None;
        }

        public static bool IsBodyCloseInvalidated(bool bullish, decimal close, decimal top, decimal bottom) =>
            bullish ? close < bottom : close > top;

        /// <summary>
        /// Presentation state is not execution consumption. A confirmed POI remains
        /// eligible after one or more unarmed touches; only a confirmed invalidation,
        /// a prior qualified strict fill, or a pre-confirmation touch may veto it.
        /// </summary>
        public static bool IsStrictPoiAvailable(ZoneState state, bool coreEntryConsumed,
            bool preConfirmationTouched) =>
            state != ZoneState.Mitigated && !coreEntryConsumed && !preConfirmationTouched;

        public static bool IntervalsOverlap(decimal topA, decimal bottomA, decimal topB, decimal bottomB) =>
            topA >= bottomB && bottomA <= topB;

        public static bool PassesPremiumDiscount(bool longSide, decimal entry, decimal high, decimal low, decimal tolerance)
        {
            if (high <= low)
                return false;

            var eq = (high + low) / 2m;
            return longSide ? entry <= eq + tolerance : entry >= eq - tolerance;
        }

        public static bool IsPotentialOhlcAmbiguity(bool longSide, decimal high, decimal low, decimal entry, decimal stop, decimal tp2, decimal tp3)
        {
            var entryTouched = high >= entry && low <= entry;
            if (!entryTouched)
                return false;

            var stopTouched = longSide ? low <= stop : high >= stop;
            var targetTouched = longSide ? high >= Math.Min(tp2, tp3) : low <= Math.Max(tp2, tp3);
            return stopTouched || targetTouched;
        }
    }
}
