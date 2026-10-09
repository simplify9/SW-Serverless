using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SW.Serverless.Tooling.Building
{
    /// <summary>Something in a source file that looks like a secret.</summary>
    public record SecretFinding(string File, int Line, string Kind);

    /// <summary>
    /// Looks through the source a package is about to carry for what shouldn't travel with it:
    /// private keys, cloud and service tokens, and passwords or keys written into code or
    /// configuration. Errs towards reporting; a false positive is let through with --allow.
    /// </summary>
    public static class SecretScanner
    {
        static readonly (string Kind, Regex Pattern)[] Patterns =
        {
            ("a private key", new Regex(@"-----BEGIN ([A-Z ]+ )?PRIVATE KEY-----")),
            ("an AWS access key", new Regex(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b")),
            ("a GitHub token", new Regex(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b")),
            ("a Slack token", new Regex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}")),
            ("a Google API key", new Regex(@"\bAIza[0-9A-Za-z_-]{35}\b")),
            ("a Stripe key", new Regex(@"\b(sk|rk)_live_[0-9A-Za-z]{20,}\b")),
            ("an Azure storage key", new Regex(@"AccountKey=[A-Za-z0-9+/=]{40,}", RegexOptions.IgnoreCase)),
            // Written as connection strings write it — Password=value, no spaces — which tells it
            // apart from code assigning a variable called password.
            ("a password in a connection string", new Regex(@"(?<![A-Za-z])(Password|Pwd)=[^;""'\s{}]{4,}", RegexOptions.IgnoreCase)),
            ("a JSON web token", new Regex(@"\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}")),
            ("a secret assigned in code", new Regex(
                @"(?i)\b[\w.-]*(password|passwd|secret|api[_-]?key|access[_-]?key|auth[_-]?token|client[_-]?secret)[\w.-]*[""']?\s*[:=]\s*[""']([^""'\s]{8,})[""']")),
        };

        /// <summary>Values that are plainly placeholders, never reported.</summary>
        static readonly Regex Placeholder = new(
            @"(?i)^(\*+|x+|changeme|change[-_]?me|your[-_].*|example.*|placeholder|dummy|test|password|secret|<.*>|\$\{.*\}|\{\{.*\}\})$");

        public static IEnumerable<SecretFinding> Scan(string relativePath, string text)
        {
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
                foreach (var (kind, pattern) in Patterns)
                {
                    var match = pattern.Match(lines[i]);
                    if (!match.Success) continue;
                    var value = match.Groups.Count > 2 ? match.Groups[match.Groups.Count - 1].Value : match.Value;
                    if (Placeholder.IsMatch(value.Trim())) continue;
                    yield return new SecretFinding(relativePath, i + 1, kind);
                    break;
                }
        }

        /// <summary>Whether a file is text worth scanning: small enough, and no NUL bytes in its start.</summary>
        public static bool IsText(byte[] bytes) =>
            bytes.Length <= 2 * 1024 * 1024 && !bytes.Take(8000).Contains((byte)0);
    }
}
