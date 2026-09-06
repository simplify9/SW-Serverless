using CommandLine;
using System;
using System.Collections.Generic;
using System.Text;
using SW.CloudFiles.OC;

namespace SW.Serverless.Installer
{
    public class FileData
    {
        public ServerlessUploadOptions CloudFiles { get; set; }
    }

    public class CliOptions
    {
        //[Option('v', "verbose", Required = false, HelpText = "Set output to verbose messages.")]
        //public bool Verbose { get; set; }
        [Option('p', "provider", HelpText = "Storage provider: s3, as (Azure), oc (Oracle), gc (Google), or local (filesystem, for development).")]
        public string Provider { get; set; }

        [Option('k', "kind",
            HelpText = "Roles this adapter serves, comma separated (e.g. handler,mapper). " +
                       "Only needed when the adapter does not declare them with [AdapterKind].")]
        public string Kind { get; set; }

        [Option('a', "accesskey", HelpText = "Access key for storage.")]
        public string AccessKeyId { get; set; }

        [Option('s', "secret", HelpText = "Secret access key for storage.")]
        public string SecretAccessKey { get; set; }

        [Option('b', "bucketname", HelpText = "Bucket name for storage.")]
        public string BucketName { get; set; }


        [Option('u', "url", HelpText = "Service Url for storage.")]
        public string ServiceUrl { get; set; }


        [Option('c', "cloudfilesconfigpath", HelpText = "Json cloud files config path. (required for Oracle Cloud)")]
        public string CloudFilesConfigPath { get; set; }

        [Option('v', "version",
            HelpText =
                "Semantic version in the format major.minor.patch, or specify 'major', 'minor', or 'patch' to auto-increment the respective part.")]
        public string Version { get; set; }

        [Value(0, HelpText = "Path to project file (csproj)")]
        public string ProjectPath { get; set; }

        [Value(1, HelpText = "Adapter Id")] public string AdapterId { get; set; }
    }

    public class ServerlessUploadOptions
    {
        public string Provider { get; set; }

        public string AccessKeyId { get; set; }

        public string SecretAccessKey { get; set; }

        public string BucketName { get; set; }

        public string ServiceUrl { get; set; }


        public string RSAKey { get; set; }

        public string UserId { get; set; }

        public string FingerPrint { get; set; }

        public string TenantId { get; set; }

        public string Region { get; set; }


        public string Version { get; set; }


        public string AdapterId { get; set; }
        public string NamespaceName { get; set; }

        // ——— Google Cloud service account ———
        public string ProjectId { get; set; }
        public string PrivateKeyId { get; set; }
        public string PrivateKey { get; set; }
        public string ClientEmail { get; set; }
        public string ClientId { get; set; }
        public string ClientX509CertUrl { get; set; }

        /// <summary>
        /// Roles this adapter serves, comma separated. Overrides what the assembly declared, for
        /// an adapter whose author has not added [AdapterKind] to it.
        /// </summary>
        public string Kind { get; set; }
    }
}