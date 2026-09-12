using System;

namespace TaxRateTweak.Services
{
    public static class DensityDemand
    {
        public const int Count = 5;
        public const int Maximum = 100;

        public struct Values
        {
            public int Low;
            public int Row;
            public int Medium;
            public int High;
            public int LowRent;

            public int Get(int category)
            {
                switch (category)
                {
                    case 0: return Low;
                    case 1: return Row;
                    case 2: return Medium;
                    case 3: return High;
                    case 4: return LowRent;
                    default: return 0;
                }
            }
        }

        public static int Vacancy(int free, int requirement)
        {
            return (int)Math.Round(100f * (Math.Max(1, requirement) - free) / Math.Max(1, requirement));
        }

        public static int Calculate(int householdDemand, int vacancy, int factors, int taxEffect, bool unlocked, bool unlimited)
        {
            if (unlimited) return Maximum;
            if (!unlocked) return 0;
            int score = householdDemand / 2 + vacancy + (vacancy >= 0 ? factors + vacancy : 0);
            int nativeDemand = Math.Min(Maximum, Math.Max(0, score));
            return Math.Min(Maximum, Math.Max(0, nativeDemand + taxEffect));
        }
    }
}
