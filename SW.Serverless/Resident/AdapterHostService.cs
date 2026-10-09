using Grpc.Core;
using Microsoft.Extensions.Logging;
using SW.Serverless.Contract;
using System.Linq;
using System.Threading.Tasks;

namespace SW.Serverless.Resident
{
    /// <summary>
    /// The gRPC endpoint the adapter dials. It listens only on a Unix domain socket or a named
    /// pipe — no port, no bind address, no firewall rule.
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

            if (!ProtocolVersions.Supports(first.Hello.ProtocolVersion))
            {
                logger.LogError(
                    "Adapter {AdapterId}/{InstanceKey} speaks protocol {Protocol}; this host speaks {Min} to {Max}.",
                    instance.AdapterId, instance.InstanceKey, first.Hello.ProtocolVersion,
                    ProtocolVersions.Min, ProtocolVersions.Max);
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    $"Protocol {first.Hello.ProtocolVersion} is not supported; this host speaks {ProtocolVersions.Min} to {ProtocolVersions.Max}."));
            }

            instance.Capabilities = first.Hello.Capabilities.ToArray();
            instance.Commands = first.Hello.Capabilities
                .Where(c => c.StartsWith("command:", System.StringComparison.OrdinalIgnoreCase))
                .Select(c => c["command:".Length..])
                .OrderBy(c => c)
                .ToArray();
            instance.CommandDetails = first.Hello.Commands
                .Select(c => new AdapterCommand
                {
                    Name = c.Name,
                    ParameterType = c.ParameterType,
                    ParameterSchema = c.ParameterSchema,
                    ReturnsValue = c.ReturnsValue,
                    Description = c.Description,
                    InputSchema = c.InputSchema,
                    OutputSchema = c.OutputSchema
                })
                .OrderBy(c => c.Name)
                .ToArray();

            instance.SdkVersion = first.Hello.SdkVersion;
            // Empty from an SDK that predates them, which is fine: they describe, they don't decide.
            instance.SdkLanguage = first.Hello.SdkLanguage;
            instance.Settings = first.Hello.Settings
                .Select(x => new AdapterSetting
                {
                    Name = x.Name, Description = x.Description, Required = x.Required, Secret = x.Secret,
                    DefaultValue = x.DefaultValue, Type = x.Type
                })
                .ToArray();
            instance.Kinds = first.Hello.Kinds.ToArray();
            instance.Contracts = first.Hello.Contracts.ToDictionary(kv => kv.Key, kv => kv.Value);
            instance.ProtocolVersion = first.Hello.ProtocolVersion;

            logger.LogInformation(
                "Adapter {AdapterId}/{InstanceKey} attached. SDK {SdkVersion}, protocol {Protocol}.",
                instance.AdapterId, instance.InstanceKey, first.Hello.SdkVersion, first.Hello.ProtocolVersion);

            await instance.RunStreamAsync(requestStream, responseStream, context.CancellationToken);
        }
    }
}
