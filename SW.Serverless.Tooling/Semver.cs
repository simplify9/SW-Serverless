using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SW.Serverless.Tooling;

public static class Semver
{
    private static readonly Regex Release = new(@"^(\d+)\.(\d+)\.(\d+)$");
    private static readonly Regex ReleaseOrPreRelease = new(@"^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.-]+)?$");

    /// <summary>
    /// The version to publish next. <paramref name="mode"/> is 'major', 'minor', 'patch', or an
    /// explicit version, which may carry a pre-release suffix (1.4.0-beta.1).
    ///
    /// Only plain major.minor.patch keys count towards the highest version. Anything else in the
    /// folder — a pre-release, a stray file — is ignored rather than allowed to fail the publish,
    /// and a folder with no release keys at all is treated as empty.
    /// </summary>
    public static string GetNewVersion(string mode, List<string> olderVersions)
    {
        mode = mode?.Trim();
        if (string.IsNullOrEmpty(mode))
            throw new ArgumentException("A version is required: 'major', 'minor', 'patch' or a semantic version.");

        var existing = olderVersions ?? new List<string>();
        var releases = existing.Select(TryParseRelease).Where(v => v.HasValue).Select(v => v.Value).ToList();

        if (ReleaseOrPreRelease.IsMatch(mode))
        {
            // An explicit version never overwrites one that is already there.
            if (existing.Contains(mode))
                throw new ArgumentException($"Version {mode} has already been published.");

            if (releases.Count == 0) return mode;

            // Compared on major.minor.patch alone. A pre-release of the highest release sorts below
            // it, so for a pre-release too the core must be strictly higher.
            var highest = releases.Max();
            if (ParseCore(mode).CompareTo(highest) <= 0)
                throw new ArgumentException(
                    $"The provided version must be higher than all older versions (highest is {highest.Major}.{highest.Minor}.{highest.Patch}).");

            return mode;
        }

        var bump = mode.ToLowerInvariant();
        if (bump != "major" && bump != "minor" && bump != "patch")
            throw new ArgumentException(
                $"Invalid mode: {mode}. Use 'major', 'minor', 'patch' or a valid semantic version.");

        if (releases.Count == 0) return "1.0.0";

        var (major, minor, patch) = releases.Max();
        return bump switch
        {
            "major" => $"{major + 1}.0.0",
            "minor" => $"{major}.{minor + 1}.0",
            _ => $"{major}.{minor}.{patch + 1}",
        };
    }

    private static (int Major, int Minor, int Patch)? TryParseRelease(string version)
    {
        if (version == null || !Release.IsMatch(version)) return null;

        // A part too large for an int is not a version this tool can bump; it is ignored like any
        // other foreign key rather than allowed to crash the publish.
        try { return ParseCore(version); }
        catch (ArgumentException) { return null; }
    }

    private static (int Major, int Minor, int Patch) ParseCore(string version)
    {
        var match = ReleaseOrPreRelease.Match(version);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, out var major) ||
            !int.TryParse(match.Groups[2].Value, out var minor) ||
            !int.TryParse(match.Groups[3].Value, out var patch))
            throw new ArgumentException($"Invalid version format: {version}. Expected format is major.minor.patch.");

        return (major, minor, patch);
    }
}
