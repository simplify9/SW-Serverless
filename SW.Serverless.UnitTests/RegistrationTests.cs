using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.Serverless.Resident;
using SW.Serverless.UnitTests.Fixtures;

namespace SW.Serverless.UnitTests
{
    /// <summary>What a host has to register for the parts it uses, and no more.</summary>
    [TestClass]
    public class RegistrationTests
    {
        [TestMethod]
        public async System.Threading.Tasks.Task Resident_adapters_given_by_path_need_neither_AddServerless_nor_storage()
        {
            var services = new ServiceCollection();
            services.AddLogging(l => l.ClearProviders());
            services.AddResidentAdapters<TestEventSink>();

            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });

            Assert.IsNotNull(provider.GetRequiredService<IResidentAdapterHost>());
        }
    }
}
