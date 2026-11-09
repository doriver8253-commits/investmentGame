using System;
using System.Collections.Generic;

namespace BlackWhale.Rules
{
    /// <summary>
    /// NEXT-B02: the real per-turn market for normal play (02_결정_사항 §10).
    /// One generator per room, owned by the server. A turn is generated once from (seed, turn) and then
    /// cached, so reconnects, purchases and lookups never re-roll it. The fixed fixture market stays
    /// available through <c>new MarketService(MarketData)</c> (fixture mode).
    ///
    /// Rules per company and day 1..30:
    ///   step  = uniform integer in [-dailyLimit, +dailyLimit] bps
    ///   value = clamp(previous value + step, -absoluteCap, +absoluteCap), day 0 = 0
    ///   dailyLimit(turn) = round(baseLimit * (1 + 0.05 * (turn - 1))), .5 rounds up (e.g. AUTO turn 2: 367.5 -> 368)
    /// Two distinct free bundles (0..5) are drawn once per company and are the same for every player.
    ///
    /// Hidden swing (11/9 meeting): so hidden data cannot be guessed from the visible days and buying it is worth it,
    /// each company has a <see cref="SwingChancePercent"/> chance per turn of one sharp V or inverted-V move inside
    /// one NON-public bundle. The day before the bundle and the bundle's last day keep their random-walk values, so the visible
    /// neighbours give no hint; only the days in between (4 of the 5) move by up to SwingMultiplier x dailyLimit.
    /// The absolute cap still applies. Inside a swing bundle a single day may move more than dailyLimit.
    /// </summary>
    public sealed class RandomMarketGenerator
    {
        /// <summary>Base daily change limit in bps (turn 1).</summary>
        public static readonly IReadOnlyDictionary<string, int> BaseDailyLimitBps = new Dictionary<string, int>
        {
            { "BANK", 80 }, { "FOOD", 120 }, { "AUTO", 350 }, { "AI", 800 }, { "SPACE", 800 }, { "BIO", 1500 }
        };

        /// <summary>Clamp for the change vs day 0 in bps. Does not grow with the turn.</summary>
        public static readonly IReadOnlyDictionary<string, int> AbsoluteCapBps = new Dictionary<string, int>
        {
            { "BANK", 600 }, { "FOOD", 800 }, { "AUTO", 2000 }, { "AI", 5000 }, { "SPACE", 5000 }, { "BIO", 8000 }
        };

        /// <summary>Chance (0..100) that a company gets a hidden swing in a turn.</summary>
        public const int SwingChancePercent = 50;
        /// <summary>Swing size range, in multiples of that turn's daily limit.</summary>
        public const int SwingMinMultiplier = 3, SwingMaxMultiplier = 5;

        private readonly InvestmentRules rules;
        private readonly Dictionary<int, MarketData> generated = new Dictionary<int, MarketData>();
        private readonly object gate = new object();

        public int Seed { get; private set; }

        public RandomMarketGenerator(int seed, InvestmentRules rules = null)
        {
            Seed = seed;
            this.rules = (rules ?? new InvestmentRules()).Copy();
        }

        /// <summary>A fresh seed for a new room (server only).</summary>
        public static int NewSeed()
        {
            var bytes = new byte[4];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return BitConverter.ToInt32(bytes, 0);
        }

        /// <summary>
        /// Provider for <c>new MarketService(generator.ForTurn)</c>. The same turn always returns the same values.
        /// Each call returns a new copy, so a caller changing it cannot change the stored market.
        /// </summary>
        public MarketData ForTurn(int turn)
        {
            if (turn < 1) throw new ArgumentOutOfRangeException(nameof(turn));
            lock (gate)
            {
                MarketData data;
                if (!generated.TryGetValue(turn, out data))
                {
                    data = Generate(Seed, turn, rules);
                    generated.Add(turn, data);
                }
                return Copy(data);
            }
        }

        /// <summary>Daily limit for a company in a turn: base x (1 + 0.05 x (turn - 1)), .5 rounded up.</summary>
        public static int DailyLimitBps(string companyId, int turn)
        {
            if (turn < 1) throw new ArgumentOutOfRangeException(nameof(turn));
            long scaled = (long)BaseDailyLimitBps[companyId] * (100 + 5 * (turn - 1)); // x100
            return (int)((scaled + 50) / 100);
        }

        /// <summary>Pure function of (seed, turn): same inputs always give the same market.</summary>
        public static MarketData Generate(int seed, int turn, InvestmentRules rules = null)
        {
            int[] unused;
            return Generate(seed, turn, rules, out unused);
        }

        /// <summary>
        /// Server/test helper: which bundle (0..5) got the hidden swing per company, in
        /// <see cref="InvestmentRules.CompanyIds"/> order, or -1 when that company has none this turn.
        /// Never send this to clients.
        /// </summary>
        public static int[] SwingBundles(int seed, int turn, InvestmentRules rules = null)
        {
            int[] swings;
            Generate(seed, turn, rules, out swings);
            return swings;
        }

        private static MarketData Generate(int seed, int turn, InvestmentRules rules, out int[] swings)
        {
            if (turn < 1) throw new ArgumentOutOfRangeException(nameof(turn));
            rules = rules ?? new InvestmentRules();
            string[] ids = InvestmentRules.CompanyIds;
            var companies = new CompanyMarketData[ids.Length];
            swings = new int[ids.Length];
            for (int c = 0; c < ids.Length; c++)
            {
                // Each company has its own stream, so adding/reordering draws elsewhere never changes it.
                var random = new SplitMix64(Mix(seed, turn, c));
                int limit = DailyLimitBps(ids[c], turn);
                int cap = AbsoluteCapBps[ids[c]];

                var days = new int[rules.daysPerTurn + 1];
                for (int d = 1; d < days.Length; d++)
                {
                    int next = days[d - 1] + random.NextInclusive(-limit, limit);
                    days[d] = Math.Max(-cap, Math.Min(cap, next));
                }

                int first = random.NextInclusive(0, InvestmentRules.BundlesPerCompany - 1);
                int second = random.NextInclusive(0, InvestmentRules.BundlesPerCompany - 2);
                if (second >= first) second++; // distinct, still uniform

                // Drawn after the walk and the free bundles, so those stay exactly as before.
                swings[c] = -1;
                int bundleDays = rules.bundleSizeDays;
                if (random.NextInclusive(1, 100) <= SwingChancePercent && bundleDays >= 3)
                {
                    int pick = random.NextInclusive(0, InvestmentRules.BundlesPerCompany - 3); // 4 hidden bundles
                    int bundle = 0;
                    for (int b = 0, k = 0; b < InvestmentRules.BundlesPerCompany; b++)
                    {
                        if (b == first || b == second) continue;
                        if (k++ == pick) { bundle = b; break; }
                    }
                    int sign = random.NextInclusive(0, 1) == 0 ? -1 : 1;
                    int size = random.NextInclusive(SwingMinMultiplier * limit, SwingMaxMultiplier * limit);
                    // Bundle b shows days b*size+1 .. (b+1)*size. Anchors: the day before it and its own last day
                    // stay on the walk; only the days strictly between them move.
                    int start = bundle * bundleDays, end = start + bundleDays;
                    int peak = random.NextInclusive(start + 1, end - 1);
                    for (int d = start + 1; d < end; d++)
                    {
                        // Linear up to the peak, linear back down to 0 at the bundle's last day.
                        long offset = d <= peak
                            ? (long)size * (d - start) / (peak - start)
                            : (long)size * (end - d) / (end - peak);
                        days[d] = (int)Math.Max(-cap, Math.Min(cap, days[d] + sign * offset));
                    }
                    swings[c] = bundle;
                }
                companies[c] = new CompanyMarketData
                {
                    companyId = ids[c],
                    dailyChangesBps = days,
                    publicBundleIndices = first < second ? new[] { first, second } : new[] { second, first }
                };
            }
            return new MarketData { turn = turn, companies = companies };
        }

        private static MarketData Copy(MarketData source)
        {
            var companies = new CompanyMarketData[source.companies.Length];
            for (int i = 0; i < companies.Length; i++)
                companies[i] = new CompanyMarketData
                {
                    companyId = source.companies[i].companyId,
                    dailyChangesBps = (int[])source.companies[i].dailyChangesBps.Clone(),
                    publicBundleIndices = (int[])source.companies[i].publicBundleIndices.Clone()
                };
            return new MarketData { turn = source.turn, companies = companies };
        }

        private static ulong Mix(int seed, int turn, int company)
        {
            unchecked
            {
                ulong x = (uint)seed;
                x = x * 0x9E3779B97F4A7C15UL + (uint)turn;
                x = x * 0xBF58476D1CE4E5B9UL + (uint)company;
                return x;
            }
        }

        /// <summary>
        /// Small fixed algorithm (SplitMix64) instead of System.Random, so Unity/Mono and .NET
        /// produce exactly the same market for the same seed.
        /// </summary>
        private sealed class SplitMix64
        {
            private ulong state;
            public SplitMix64(ulong seed) { state = seed; }

            public ulong Next()
            {
                unchecked
                {
                    ulong z = state += 0x9E3779B97F4A7C15UL;
                    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                    z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                    return z ^ (z >> 31);
                }
            }

            /// <summary>Uniform integer in [min, max] without modulo bias.</summary>
            public int NextInclusive(int min, int max)
            {
                ulong range = (ulong)((long)max - min + 1);
                ulong limit = ulong.MaxValue - ulong.MaxValue % range;
                ulong r;
                do { r = Next(); } while (r >= limit);
                return (int)(min + (long)(r % range));
            }
        }
    }
}
