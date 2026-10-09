using Newtonsoft.Json;
using SW.PrimitiveTypes;
using SW.Serverless.Sdk;
using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;

namespace SW.Serverless.SampleAdapter2
{
    class Handler
    {
        string userName;
        public Handler()
        {
            Runner.Expect("UserName", "admin");
            Runner.Expect("Password");
        }

        public Task TestVoid(string input)
        {
            var cid = Runner.CorrelationId;
            return Task.CompletedTask;
        }

        public Task TestParameterless()
        {
            
            return Task.CompletedTask;
        }

        public Task<RemoteBlob> TestTypedReturn()
        {

            return Task.FromResult(new RemoteBlob 
            { 
                Location = "http://sample.com"
            });
        }

        public Task<object> TestStartupValue(string name) => Task.FromResult<object>(Runner.StartupValueOf(name));

        public Task<object> TestString(string value)
        {
            return Task.FromResult((object)value);
        }

        public Task<object> TestInt(int value)
        {
            return Task.FromResult((object)value);
        }

        public Task<object> TestObject(RemoteBlob value)
        {
            return Task.FromResult((object)value);
        }

        public Task TestException()
        {
            throw new NotImplementedException();
        }

        //async public Task<object> TestString2(string input)
        //{
        //    //await Task.Delay(TimeSpan.FromSeconds(40));

        //    using var cloudFiles = new CloudFilesService(Runner.ServerlessOptions.CloudFilesOptions);

        //    // adapter creates aramex/dhl label based on the input
        //    AdapterLogger.LogInformation("hello");

        //    var rb = await cloudFiles.WriteTextAsync("test from adapter", new WriteFileSettings
        //    {
        //        Public = true,
        //        ContentType = "text/plain",
        //        Key = "temp1/testadapteraccess.txt"
        //    });

        //    return JsonConvert.SerializeObject(rb);
        //}
    }
}
