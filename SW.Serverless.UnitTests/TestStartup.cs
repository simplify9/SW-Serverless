using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SW.CloudFiles.Extensions;
using SW.Serverless.UnitTests.Fixtures;
using System.IO;

namespace SW.Serverless.UnitTests
{
    public class TestStartup
    {
        public TestStartup(IConfiguration configuration) => Configuration = configuration;

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            // Local filesystem ICloudFilesService. The tests exercise the real
            // download-and-extract install path with no cloud account and no credentials.
            services.AddLocalTestsCloudFiles(o => o.BucketName = TestStore.BucketName);

            services.AddServerless(o =>
            {
                o.AdapterRemotePath = "adapters";
                o.AdapterLocalPath = Path.Combine(Path.GetTempPath(), "swsl-unittests", "installed");
                o.AdapterMetadataCacheDuration = 1;
                o.CommandTimeout = 30;
            });
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env) { }
    }
}
