using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SW.CloudFiles.AS.Extensions;
// GC, OC, S3 and LocalTests all publish their registration extensions into this one namespace.
using SW.CloudFiles.Extensions;
using SW.PrimitiveTypes;
using SW.Serverless.Installer;

namespace SW.Serverless.Tooling
{
    /// <summary>
    /// Builds the storage provider an adapter is published to.
    ///
    /// Each provider is constructed through its OWN registration extension rather than by newing
    /// its service up here. That is not ceremony: those extensions create the bucket if it is
    /// missing and set the temp1/temp7/temp30/temp365 lifecycle rules, so hand-rolling the
    /// construction would publish into a bucket that quietly never expires anything.
    /// </summary>
    public static class CloudFilesFactory
    {
        /// <summary>Every provider name this tool accepts, for help text and error messages.</summary>
        public static readonly IReadOnlyList<string> Providers = new[] { "s3", "as", "oc", "gc", "local" };

        /// <summary>
        /// Extra providers, by name, for tests only. The filesystem provider stores a key as a file,
        /// so it cannot hold <c>adapters/{id}</c> and <c>adapters/{id}/{version}</c> side by side the
        /// way every object store does; the tests register a store with object-store semantics here
        /// rather than needing a cloud account to exercise the real layout.
        /// </summary>
        static readonly ConcurrentDictionary<string, Func<ServerlessUploadOptions, ICloudFilesService>> registered =
            new(StringComparer.OrdinalIgnoreCase);

        internal static void Register(string provider, Func<ServerlessUploadOptions, ICloudFilesService> factory) =>
            registered[provider] = factory;

        public static ICloudFilesService Create(ServerlessUploadOptions options)
        {
            if (options.Provider != null && registered.TryGetValue(options.Provider, out var custom))
                return custom(options);

            var services = new ServiceCollection();

            // The registration extensions bind their options from configuration, so a container
            // without one throws before anything is registered. Empty is fine: everything these
            // need has already been supplied on the command line or in the config file.
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

            var provider = (options.Provider ?? "s3").ToLowerInvariant();

            switch (provider)
            {
                case "as":
                    services.AddAsCloudFiles(o =>
                    {
                        o.BucketName = options.BucketName;
                        o.AccessKeyId = options.AccessKeyId;
                        o.SecretAccessKey = options.SecretAccessKey;
                        o.ServiceUrl = options.ServiceUrl;
                    });
                    break;

                case "oc":
                    services.AddOracleCloudFiles(o =>
                    {
                        o.BucketName = options.BucketName;
                        o.AccessKeyId = options.AccessKeyId;
                        o.SecretAccessKey = options.SecretAccessKey;
                        o.ServiceUrl = options.ServiceUrl;
                        o.Region = options.Region;
                        o.FingerPrint = options.FingerPrint;
                        o.TenantId = options.TenantId;
                        o.UserId = options.UserId;
                        o.RSAKey = options.RSAKey;
                        o.NamespaceName = options.NamespaceName;
                    });
                    break;

                case "gc":
                    services.AddGoogleCloudFiles(o =>
                    {
                        o.BucketName = options.BucketName;
                        o.ProjectId = options.ProjectId;
                        o.PrivateKeyId = options.PrivateKeyId;
                        o.PrivateKey = options.PrivateKey;
                        o.ClientEmail = options.ClientEmail;
                        o.ClientId = options.ClientId;
                        if (!string.IsNullOrWhiteSpace(options.ClientX509CertUrl))
                            o.ClientX509CertUrl = options.ClientX509CertUrl;
                    });
                    break;

                case "local":
                    // The filesystem provider, for a developer running Bitween against a local
                    // store. Publishing to it is the same command with a different -p, rather than
                    // the hand-built zip and .meta.json it used to take.
                    services.AddLocalTestsCloudFiles(o =>
                    {
                        if (!string.IsNullOrWhiteSpace(options.BucketName))
                            o.BucketName = options.BucketName;
                        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
                            o.StoragePath = options.ServiceUrl;
                    });
                    break;

                case "s3":
                    services.AddS3CloudFiles(o =>
                    {
                        o.BucketName = options.BucketName;
                        o.AccessKeyId = options.AccessKeyId;
                        o.SecretAccessKey = options.SecretAccessKey;
                        o.ServiceUrl = options.ServiceUrl;
                    });
                    break;

                default:
                    throw new SWException(
                        $"Unknown storage provider '{options.Provider}'. "
                        + $"Expected one of: {string.Join(", ", Providers)}.");
            }

            return services.BuildServiceProvider().GetRequiredService<ICloudFilesService>();
        }
    }
}
