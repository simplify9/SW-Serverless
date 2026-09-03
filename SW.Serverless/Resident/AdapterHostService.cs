using Grpc.Core;
using Microsoft.Extensions.Logging;
using SW.Serverless.Contract;
using System.Threading.Tasks;

namespace SW.Serverless.Resident
{
    /// <summary>
    /// The gRPC endpoint the adapter dials. It listens only on a Unix domain socket or a named
    /// pipe — no port, no bind address, no firewall rule (design doc 15.1).
    /// </summary>
    internal class AdapterHostService : Contract.AdapterHost.AdapterHostBase
    {
        readonly ResidentAdapterRegistry registry;
        readonly ILogger<AdapterHostService> logger;

        public AdapterHostService(ResidentAdapterRegistry registry, ILogger<AdapterHostService> logger)
        {
            this.registry = registry;
            this.logger = logger;
        }

        public override async Task Attach(IAsyncStreamReader<AdapterFrame> requestStream,
            IServerStreamWriter<HostFrame> responseStream, ServerCallContext context)
        {
            if (!await requestStream.MoveNext(context.CancellationToken))
                return;

            var first = requestStream.Current;
            if (first.BodyCase != AdapterFrame.BodyOneofCase.Hello)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "First frame must be Hello."));

            // The one-time token is what proves this is the child we just spawned.
            var instance = registry.Claim(first.Hello.Token);
            if (instance == null)
            {
                logger.LogWarning("Rejected an Attach with an unknown token from adapter {AdapterId}.",
                    first.Hello.AdapterId);
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Unknown or already-used token."));
            }

            logger.LogInformation(
                "Adapter {AdapterId}/{InstanceKey} attached. SDK {SdkVersion}, protocol {Protocol}.",
                instance.AdapterId, instance.InstanceKey, first.Hello.SdkVersion, first.Hello.ProtocolVersion);

            await instance.RunStreamAsync(requestStream, responseStream, context.CancellationToken);
        }
    }
}
