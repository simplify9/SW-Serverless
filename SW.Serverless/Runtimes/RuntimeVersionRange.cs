using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace SW.Serverless.Runtimes
{
    /// <summary>
    /// A manifest's runtimeVersion: comparisons joined by commas or spaces — "&gt;=3.12",
    /// "&gt;=22, &lt;24", "3.12" for exactly that minor line. Empty means any version.
    /// </summary>
    public static class RuntimeVersionRange
    {
        static readonly Regex Part = new(@"^(>=|<=|>|<|=|==)?\s*v?(\d+(?:\.\d+){0,2})$");

        /// <summary>Whether <paramref name="version"/> is in <paramref name="range"/>; a range that can't be read is not satisfied.</summary>
        public static bool Satisfies(string range, string version)
        {
            if (string.IsNullOrWhiteSpace(range)) return true;
            if (!TryVersion(version, out var actual)) return false;

            foreach (var raw in Regex.Split(range.Trim(), @"\s*,\s*|\s+(?=[<>=])"))
            {
                var match = Part.Match(raw.Trim());
                if (!match.Success || !TryVersion(match.Groups[2].Value, out var bound)) return false;
                var length = match.Groups[2].Value.Split('.').Length;
                var ok = match.Groups[1].Value switch
                {
                    ">=" => actual >= bound,
                    ">" => actual > bound,
                    "<=" => actual <= bound,
                    "<" => actual < bound,
                    // A bare or "=" version matches every release that starts with it: 3.12 is any 3.12.x.
                    _ => Prefix(actual, length) == Prefix(bound, length),
                };
                if (!ok) return false;
            }
            return true;
        }

        public static bool IsReadable(string range) =>
            string.IsNullOrWhiteSpace(range) ||
            Regex.Split(range.Trim(), @"\s*,\s*|\s+(?=[<>=])").All(p => Part.IsMatch(p.Trim()));

        static bool TryVersion(string text, out Version version)
        {
            var parts = (text ?? "").Split('.');
            while (parts.Length < 3) parts = parts.Append("0").ToArray();
            return Version.TryParse(string.Join('.', parts.Take(3)), out version);
        }

        static Version Prefix(Version v, int length) => length switch
        {
            1 => new Version(v.Major, 0, 0),
            2 => new Version(v.Major, v.Minor, 0),
            _ => new Version(v.Major, v.Minor, Math.Max(0, v.Build)),
        };
    }
}
