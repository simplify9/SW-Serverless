using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using SW.PrimitiveTypes;

namespace SW.Serverless.Installer
{
    /// <summary>The storage flags a command line takes: -p, -a, -s, -b, -u and -c in sw-serverless.</summary>
    public class StorageFlags
    {
        public string Provider { get; set; }
        public string AccessKeyId { get; set; }
        public string SecretAccessKey { get; set; }
        public string BucketName { get; set; }
        public string ServiceUrl { get; set; }

        /// <summary>A JSON file with a "CloudFiles" section; Oracle Cloud needs one, Google Cloud one or the SWSL_GC_* variables.</summary>
        public string CloudFilesConfigPath { get; set; }

        /// <summary>The storage these flags, the config file they name and the SWSL_* variables point at.</summary>
        public ServerlessUploadOptions Resolve(Func<string, string> environment = null) =>
            StorageResolver.Resolve(this, CloudFilesConfigPath == null ? null : System.IO.File.ReadAllText(CloudFilesConfigPath), environment);
    }

    /// <summary>The config file -c names: its "CloudFiles" section.</summary>
    public class FileData
    {
        public ServerlessUploadOptions CloudFiles { get; set; }
    }

    /// <summary>
    /// Works out where an adapter is published to, from three sources in order of precedence:
    /// command-line flags, then the JSON config file (-c), then SWSL_* environment variables.
    ///
    /// Environment variables are the last resort so a CI job can keep its keys in secrets rather
    /// than on a command line, where they end up in shell history and process listings.
    /// </summary>
    public static class StorageResolver
    {
        /// <summary>Every environment variable read, with what it supplies. Used by the README.</summary>
        public static readonly IReadOnlyDictionary<string, string> EnvironmentVariables = new Dictionary<string, string>
        {
            [Env.Provider] = "Storage provider (s3, as, oc, gc, local)",
            [Env.AccessKey] = "Access key",
            [Env.SecretKey] = "Secret access key",
            [Env.Bucket] = "Bucket (container) name",
            [Env.ServiceUrl] = "Service URL",
            [Env.Region] = "Region",
            [Env.GcProjectId] = "Google Cloud project_id",
            [Env.GcPrivateKeyId] = "Google Cloud private_key_id",
            [Env.GcPrivateKey] = "Google Cloud private_key (PEM; literal \\n is accepted for line breaks)",
            [Env.GcClientEmail] = "Google Cloud client_email",
            [Env.GcClientId] = "Google Cloud client_id",
            [Env.GcClientX509CertUrl] = "Google Cloud client_x509_cert_url",
            [Env.PublishedBy] = "Who is publishing, recorded in the catalog (then GITHUB_ACTOR, then the user name)",
        };

        public static class Env
        {
            public const string Provider = "SWSL_PROVIDER";
            public const string AccessKey = "SWSL_ACCESS_KEY";
            public const string SecretKey = "SWSL_SECRET_KEY";
            public const string Bucket = "SWSL_BUCKET";
            public const string ServiceUrl = "SWSL_SERVICE_URL";
            public const string Region = "SWSL_REGION";
            public const string GcProjectId = "SWSL_GC_PROJECT_ID";
            public const string GcPrivateKeyId = "SWSL_GC_PRIVATE_KEY_ID";
            public const string GcPrivateKey = "SWSL_GC_PRIVATE_KEY";
            public const string GcClientEmail = "SWSL_GC_CLIENT_EMAIL";
            public const string GcClientId = "SWSL_GC_CLIENT_ID";
            public const string GcClientX509CertUrl = "SWSL_GC_CLIENT_X509_CERT_URL";
            public const string PublishedBy = "SWSL_PUBLISHED_BY";
            public const string GitHubActor = "GITHUB_ACTOR";
        }

        /// <param name="options">The storage flags a command was given.</param>
        /// <param name="configJson">The contents of the -c file, or null when none was given.</param>
        /// <param name="environment">Reads an environment variable; null for the process environment.</param>
        public static ServerlessUploadOptions Resolve(
            StorageFlags options, string configJson, Func<string, string> environment = null)
        {
            environment ??= Environment.GetEnvironmentVariable;

            ServerlessUploadOptions file = null;
            if (configJson != null)
            {
                if (string.IsNullOrWhiteSpace(configJson))
                    throw new SWException($"Invalid cloud Files config path, {options.CloudFilesConfigPath}");

                file = JsonConvert.DeserializeObject<FileData>(configJson)?.CloudFiles
                       ?? throw new SWException(
                           $"The cloud files config {options.CloudFilesConfigPath} has no \"CloudFiles\" section.");
            }

            string Pick(string flag, string fromFile, string envName) =>
                FirstSet(flag, fromFile, envName == null ? null : environment(envName));

            return new ServerlessUploadOptions
            {
                Provider = Pick(options.Provider, file?.Provider, Env.Provider),
                AccessKeyId = Pick(options.AccessKeyId, file?.AccessKeyId, Env.AccessKey),
                SecretAccessKey = Pick(options.SecretAccessKey, file?.SecretAccessKey, Env.SecretKey),
                BucketName = Pick(options.BucketName, file?.BucketName, Env.Bucket),
                ServiceUrl = Pick(options.ServiceUrl, file?.ServiceUrl, Env.ServiceUrl),
                Region = Pick(null, file?.Region, Env.Region),

                // Oracle: config file only, as before.
                FingerPrint = file?.FingerPrint,
                TenantId = file?.TenantId,
                UserId = file?.UserId,
                RSAKey = file?.RSAKey,
                NamespaceName = file?.NamespaceName,

                // Google Cloud service account. These were accepted in the config file but never
                // passed on, so a gc publish always failed to authenticate.
                ProjectId = Pick(null, file?.ProjectId, Env.GcProjectId),
                PrivateKeyId = Pick(null, file?.PrivateKeyId, Env.GcPrivateKeyId),
                PrivateKey = UnescapeNewlines(Pick(null, file?.PrivateKey, Env.GcPrivateKey)),
                ClientEmail = Pick(null, file?.ClientEmail, Env.GcClientEmail),
                ClientId = Pick(null, file?.ClientId, Env.GcClientId),
                ClientX509CertUrl = Pick(null, file?.ClientX509CertUrl, Env.GcClientX509CertUrl),
            };
        }

        /// <summary>
        /// Who a version is recorded as published by. Informational only — nothing is authorised
        /// by it — so it falls back as far as the user name rather than failing a publish.
        /// </summary>
        public static string ResolvePublishedBy(string flag, Func<string, string> environment = null)
        {
            environment ??= Environment.GetEnvironmentVariable;
            return FirstSet(flag, environment(Env.PublishedBy), environment(Env.GitHubActor), Environment.UserName);
        }

        private static string FirstSet(params string[] values)
        {
            foreach (var value in values)
                if (!string.IsNullOrWhiteSpace(value)) return value;
            return null;
        }

        /// <summary>
        /// A PEM key copied out of a service-account JSON file keeps its newlines as literal "\n",
        /// which is how it usually arrives in an environment variable or CI secret.
        /// </summary>
        private static string UnescapeNewlines(string key) =>
            key != null && !key.Contains('\n') && key.Contains("\\n") ? key.Replace("\\n", "\n") : key;
    }
}
