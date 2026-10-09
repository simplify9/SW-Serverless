namespace SW.Serverless.Installer
{
    /// <summary>Where adapters are published: the storage provider and how to reach it.</summary>
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
