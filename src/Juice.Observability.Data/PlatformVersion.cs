namespace Juice.Observability.Data
{
    /// <summary>
    /// Platform versions compared as Semantic Versioning orders them: major, minor and patch as numbers; a prerelease (<c>1.1.0-alpha.3</c>) before
    /// its release; prerelease identifiers one by one, numbers below names and as numbers among themselves. Build metadata after <c>+</c> is ignored.
    /// </summary>
    public static class PlatformVersion
    {
        /// <returns>Negative when <paramref name="a"/> is older, positive when newer, zero when the same. A version that doesn't read is older than any that does.</returns>
        public static int Compare(string a, string b)
        {
            var (x, y) = (Parse(a), Parse(b));
            if (x is null || y is null) return x is null ? (y is null ? 0 : -1) : 1;
            for (var i = 0; i < 3; i++)
            {
                if (x.Value.Core[i] != y.Value.Core[i]) return x.Value.Core[i].CompareTo(y.Value.Core[i]);
            }
            var (p, q) = (x.Value.Prerelease, y.Value.Prerelease);
            if (p.Length == 0 || q.Length == 0) return q.Length.CompareTo(p.Length);   // a release is newer than its prereleases
            for (var i = 0; i < Math.Min(p.Length, q.Length); i++)
            {
                var (pn, qn) = (long.TryParse(p[i], out var pv), long.TryParse(q[i], out var qv));
                var order = pn && qn ? pv.CompareTo(qv) : pn ? -1 : qn ? 1 : string.CompareOrdinal(p[i], q[i]);
                if (order != 0) return Math.Sign(order);
            }
            return p.Length.CompareTo(q.Length);
        }

        private static (long[] Core, string[] Prerelease)? Parse(string version)
        {
            var withoutBuild = version.Split('+')[0];
            var dash = withoutBuild.IndexOf('-');
            var core = (dash < 0 ? withoutBuild : withoutBuild[..dash]).Split('.');
            if (core.Length != 3) return null;
            var numbers = new long[3];
            for (var i = 0; i < 3; i++)
            {
                if (!long.TryParse(core[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out numbers[i])) return null;
            }
            return (numbers, dash < 0 ? [] : withoutBuild[(dash + 1)..].Split('.'));
        }
    }
}
