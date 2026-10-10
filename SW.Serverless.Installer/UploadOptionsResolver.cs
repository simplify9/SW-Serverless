using System;
using System.Collections.Generic;

namespace SW.Serverless.Installer
{
    /// <summary>
    /// The command line's storage flags, resolved by <see cref="StorageResolver"/> in Tooling, which
    /// other tools built on it share: flags, then the -c file, then SWSL_* environment variables.
    /// </summary>
    public static class UploadOptionsResolver
    {
        public static IReadOnlyDictionary<string, string> EnvironmentVariables => StorageResolver.EnvironmentVariables;

        public static ServerlessUploadOptions Resolve(StorageCliOptions options, string configJson, Func<string, string> environment = null)
        {
            var resolved = StorageResolver.Resolve(options.ToFlags(), configJson, environment);
            if (options is CliOptions publish)
            {
                resolved.Version = publish.Version;
                resolved.AdapterId = publish.AdapterId;
                resolved.Kind = publish.Kind;
            }
            return resolved;
        }

        public static string ResolvePublishedBy(string flag, Func<string, string> environment = null) =>
            StorageResolver.ResolvePublishedBy(flag, environment);
    }
}
