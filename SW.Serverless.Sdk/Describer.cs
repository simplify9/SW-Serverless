using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Threading.Tasks;
using SW.Serverless.Contract.Catalog;

namespace SW.Serverless.Sdk
{
    /// <summary>
    /// Answers <c>--describe</c>: the adapter's settings, commands, kinds and contracts as an
    /// <see cref="AdapterSelfDescription"/> on stdout, without connecting to any host.
    /// </summary>
    internal static class Describer
    {
        public static bool Requested(string[] args) =>
            args.Length > 1 && args[1] == AdapterSelfDescription.Flag;

        // Payloads travel as Newtonsoft writes them — property names as declared — so the schema
        // names them the same way.
        static readonly JsonSerializerOptions SchemaOptions = new(JsonSerializerDefaults.General)
        {
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        };

        /// <param name="buildHandler">
        /// Builds the handler, which is what runs its Runner.Expect declarations. Without its
        /// settings a handler may refuse to be built; the description is then completed without
        /// them and says why.
        /// </param>
        public static AdapterSelfDescription Describe(Type handlerType, Func<object> buildHandler, string lifecycle,
            Func<object, IEnumerable<(string Name, Type Parameter, Type Result, string Description)>> commandsOf)
        {
            var description = new AdapterSelfDescription
            {
                SdkLanguage = "dotnet",
                SdkVersion = SdkInfo.Version,
                Lifecycle = lifecycle,
                Protocol = lifecycle == AdapterManifest.ResidentLifecycle
                    ? new AdapterProtocolRange { Min = 2, Max = 2 }
                    : new AdapterProtocolRange { Min = 1, Max = 1 },
            };

            object handler = null;
            try
            {
                handler = buildHandler?.Invoke();
            }
            catch (Exception ex)
            {
                description.Warnings.Add(
                    $"The handler could not be built without its settings, so settings it declares when built may be missing: {ex.GetBaseException().Message}");
            }
            handlerType ??= handler?.GetType();
            var commands = commandsOf(handler).ToList();

            foreach (var (name, value) in Runner.DeclaredStartupValues)
                description.Settings.Add(new DescribedSetting
                {
                    Name = name,
                    Description = value.Description,
                    Type = string.IsNullOrEmpty(value.Type) ? AdapterProperty.TextType : value.Type,
                    Required = !value.Optional,
                    Secret = value.Private,
                    Default = value.Private ? null : value.Default,
                });

            foreach (var (name, parameter, result, text) in commands.OrderBy(c => c.Name, StringComparer.Ordinal))
                description.Commands.Add(new DescribedCommand
                {
                    Name = name,
                    Description = string.IsNullOrEmpty(text) ? null : text,
                    InputSchema = SchemaOf(parameter),
                    OutputSchema = SchemaOf(result),
                    ReturnsValue = result != null,
                });

            if (handlerType != null)
            {
                description.Kinds = handlerType.GetCustomAttributes<AdapterKindAttribute>(true)
                    .Select(k => k.Kind).Distinct().ToList();
                description.Contracts = handlerType.GetCustomAttributes<AdapterContractAttribute>(true)
                    .GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.Max(c => c.Version));
            }

            return description;
        }

        /// <summary>The command's result type: what its Task carries, or null for a bare Task.</summary>
        public static Type ResultOf(MethodInfo method)
        {
            var type = method.ReturnType;
            if (type == typeof(Task) || type == typeof(void)) return null;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var result = type.GetGenericArguments()[0];
                // Task<object> says nothing about its shape; describe it as anything.
                return result;
            }
            return type;
        }

        static JsonElement? SchemaOf(Type type)
        {
            if (type == null) return null;
            try
            {
                JsonNode schema = type == typeof(object)
                    ? new JsonObject()
                    : SchemaOptions.GetJsonSchemaAsNode(type);
                return JsonSerializer.SerializeToElement(schema);
            }
            catch
            {
                // Describing an argument must never stop the description.
                return JsonSerializer.SerializeToElement(new JsonObject());
            }
        }

        public static Task<int> Print(AdapterSelfDescription description)
        {
            Console.Out.WriteLine(description.ToJson());
            Console.Out.Flush();
            return Task.FromResult(0);
        }
    }
}
