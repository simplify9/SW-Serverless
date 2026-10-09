using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SW.Serverless.Tooling.Building
{
    /// <summary>
    /// Which files of a project stay out of the source a package carries: a built-in list — build
    /// output, dependencies, editor folders and the kinds of file that hold secrets — then the
    /// project's .gitignore and .serverlessignore, read with gitignore's rules (*, **, a trailing /
    /// for folders, a leading / to anchor, ! to bring a file back).
    /// </summary>
    public class IgnoreRules
    {
        /// <summary>Always left out, whatever the project's ignore files say.</summary>
        public static readonly IReadOnlyList<string> BuiltIn = new[]
        {
            // Build output and downloaded dependencies.
            "bin/", "obj/", "dist/", "build/", "out/", "target/", "__pycache__/", "*.pyc", ".venv/", "venv/",
            "node_modules/", ".pytest_cache/", ".mypy_cache/", "packages/", "*.nupkg",
            // Version control and editors.
            ".git/", ".vs/", ".idea/", ".vscode/", "*.user", "*.suo", ".DS_Store",
            // What holds secrets.
            ".env", ".env.*", "*.pem", "*.key", "*.pfx", "*.p12", "*.jks", "id_rsa*", "id_ed25519*", "id_ecdsa*",
            "secrets.json", "*.publishsettings", "*.pubxml", "*.pubxml.user", "appsettings.*.json",
        };

        readonly List<(Regex Pattern, bool Negated, bool DirectoryOnly)> rules = new();

        public IgnoreRules(IEnumerable<string> lines)
        {
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                var negated = line.StartsWith('!');
                if (negated) line = line[1..];
                var directoryOnly = line.EndsWith('/');
                line = line.TrimEnd('/');
                var anchored = line.StartsWith('/') || line.Contains('/');
                line = line.TrimStart('/');

                rules.Add((new Regex("^" + (anchored ? "" : "(.*/)?") + Translate(line) + (directoryOnly ? "(/.*)?$" : "(/.*)?$"),
                    RegexOptions.CultureInvariant), negated, directoryOnly));
            }
        }

        /// <summary>The built-in list plus the project's own .gitignore and .serverlessignore.</summary>
        public static IgnoreRules For(string projectDirectory)
        {
            var lines = new List<string>(BuiltIn);
            foreach (var file in new[] { ".gitignore", ".serverlessignore" })
            {
                var path = Path.Combine(projectDirectory, file);
                if (File.Exists(path)) lines.AddRange(File.ReadAllLines(path));
            }
            return new IgnoreRules(lines);
        }

        /// <summary>Whether a path, relative to the project and with forward slashes, is left out.</summary>
        public bool Ignores(string relativePath)
        {
            var path = relativePath.Replace('\\', '/').TrimStart('/');
            var ignored = false;
            foreach (var (pattern, negated, _) in rules)
                if (pattern.IsMatch(path)) ignored = !negated;
            return ignored;
        }

        static string Translate(string glob)
        {
            var result = new System.Text.StringBuilder();
            for (var i = 0; i < glob.Length; i++)
            {
                var c = glob[i];
                if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    // ** crosses folders; "**/" may also match nothing.
                    if (i + 2 < glob.Length && glob[i + 2] == '/') { result.Append("(.*/)?"); i += 2; }
                    else { result.Append(".*"); i++; }
                }
                else if (c == '*') result.Append("[^/]*");
                else if (c == '?') result.Append("[^/]");
                else result.Append(Regex.Escape(c.ToString()));
            }
            return result.ToString();
        }
    }
}
