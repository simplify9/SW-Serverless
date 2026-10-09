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

    /// <summary>
    /// Where the adapters live. Shared by publishing and by the promote, versions and withdraw
    /// commands, so every command finds the store the same way: flags, then -c, then SWSL_*.
    /// </summary>
    public class StorageCliOptions
    {
        [Option('p', "provider", HelpText = "Storage provider: s3, as (Azure), oc (Oracle), gc (Google), or local (filesystem, for development).")]
        public string Provider { get; set; }

        [Option('a', "accesskey", HelpText = "Access key for storage.")]
        public string AccessKeyId { get; set; }

        [Option('s', "secret", HelpText = "Secret access key for storage.")]
        public string SecretAccessKey { get; set; }

        [Option('b', "bucketname", HelpText = "Bucket name for storage.")]
        public string BucketName { get; set; }


        [Option('u', "url", HelpText = "Service Url for storage.")]
        public string ServiceUrl { get; set; }


        [Option('c', "cloudfilesconfigpath", HelpText = "Json cloud files config path. Oracle Cloud needs one; Google Cloud needs one or the SWSL_GC_* environment variables.")]
        public string CloudFilesConfigPath { get; set; }
    }

    public class CliOptions : StorageCliOptions
    {
        //[Option('v', "verbose", Required = false, HelpText = "Set output to verbose messages.")]
        //public bool Verbose { get; set; }

        [Option('k', "kind",
            HelpText = "Roles this adapter serves, comma separated (e.g. handler,mapper). " +
                       "Only needed when the adapter does not declare them with [AdapterKind] or in adapter.json.")]
        public string Kind { get; set; }

        [Option('v', "version",
            HelpText =
                "Semantic version in the format major.minor.patch, or specify 'major', 'minor', or 'patch' to auto-increment the respective part.")]
        public string Version { get; set; }

        [Option("no-promote",
            HelpText = "With -v: upload the version without making it the one that runs. Promote it later with 'sw-serverless promote'.")]
        public bool NoPromote { get; set; }

        [Option("notes", HelpText = "Release notes for this version (Markdown). Overrides releaseNotes in adapter.json.")]
        public string Notes { get; set; }

        [Option("published-by",
            HelpText = "Who is publishing, for the catalog. Defaults to SWSL_PUBLISHED_BY, then GITHUB_ACTOR, then the user name.")]
        public string PublishedBy { get; set; }

        [Option("no-probe",
            HelpText = "Do not start a classic adapter to ask which startup values it expects. Its properties are then only what adapter.json declares.")]
        public bool NoProbe { get; set; }

        [Value(0, Required = true, MetaName = "project", HelpText = "Path to project file (csproj)")]
        public string ProjectPath { get; set; }

        [Value(1, Required = true, MetaName = "adapter-id",
            HelpText = "Adapter Id: lowercase letters, digits, '.', '_' and '-'.")]
        public string AdapterId { get; set; }
    }

    /// <summary><c>sw-serverless promote &lt;id&gt; &lt;version&gt;</c></summary>
    public class PromoteCliOptions : StorageCliOptions
    {
        [Value(0, Required = true, MetaName = "adapter-id", HelpText = "Adapter Id.")]
        public string AdapterId { get; set; }

        [Value(1, Required = true, MetaName = "version", HelpText = "The published version to make current, e.g. 1.4.0.")]
        public string Version { get; set; }
    }

    /// <summary><c>sw-serverless versions &lt;id&gt;</c></summary>
    public class VersionsCliOptions : StorageCliOptions
    {
        [Value(0, Required = true, MetaName = "adapter-id", HelpText = "Adapter Id.")]
        public string AdapterId { get; set; }
    }

    /// <summary><c>sw-serverless withdraw &lt;id&gt; &lt;version&gt;</c></summary>
    public class WithdrawCliOptions : StorageCliOptions
    {
        [Value(0, Required = true, MetaName = "adapter-id", HelpText = "Adapter Id.")]
        public string AdapterId { get; set; }

        [Value(1, Required = true, MetaName = "version", HelpText = "The version to take out of use.")]
        public string Version { get; set; }
    }

}
