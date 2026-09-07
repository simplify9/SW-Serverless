using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace SW.Serverless.Installer.Shared
{
    /// <summary>
    /// What an adapter IS, read from the assembly at publish time.
    /// </summary>
    public class AdapterDescription
    {
        /// <summary>"resident" when the adapter stays running, otherwise "classic".</summary>
        public string Lifecycle { get; set; } = ClassicLifecycle;

        /// <summary>
        /// The roles the adapter declared, comma separated. Empty when it declared none — this
        /// framework does not guess, because guessing wrong puts an adapter in a menu where
        /// choosing it cannot work.
        /// </summary>
        public string Kind { get; set; } = "";

        public const string ResidentLifecycle = "resident";
        public const string ClassicLifecycle = "classic";
    }

    /// <summary>
    /// Reads an adapter's lifecycle and declared kinds out of its compiled assembly.
    ///
    /// Both used to be things a host had to be told separately or infer from a naming convention,
    /// and a convention cannot express lifecycle at all — which is how a resident adapter ends up
    /// offered in a menu that can only run classic ones.
    ///
    /// Uses <see cref="MetadataLoadContext"/>, so the assembly is READ rather than loaded: nothing
    /// in the adapter executes, no static constructor runs, and a publish cannot be made to do
    /// anything by the code it is packaging.
    /// </summary>
    public static class AdapterDescriber
    {
        private const string ResidentInterface = "IResidentAdapter";
        private const string KindAttribute = "AdapterKindAttribute";

        /// <summary>
        /// Describes the entry assembly inside <paramref name="publishDirectory"/>. Never throws:
        /// a publish must not fail because the description could not be read, so an unreadable
        /// assembly simply describes as a classic adapter with no declared kind — which is what
        /// every adapter was before any of this existed.
        /// </summary>
        public static AdapterDescription Describe(string publishDirectory, string entryAssembly)
        {
            var description = new AdapterDescription();

            try
            {
                var assemblyPath = Path.Combine(publishDirectory, entryAssembly);
                if (!File.Exists(assemblyPath)) return description;

                // Everything beside it, plus the runtime, so interface and attribute types resolve.
                var assemblies = Directory.GetFiles(publishDirectory, "*.dll", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(
                        Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll"))
                    .Distinct()
                    .ToList();

                using var context = new MetadataLoadContext(new PathAssemblyResolver(assemblies));
                var assembly = context.LoadFromAssemblyPath(assemblyPath);

                var kinds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var type in SafeTypes(assembly))
                {
                    if (Implements(type, ResidentInterface))
                        description.Lifecycle = AdapterDescription.ResidentLifecycle;

                    foreach (var kind in KindsOn(type)) kinds.Add(kind);
                }

                description.Kind = string.Join(",", kinds);
            }
            catch
            {
                // Described as classic with no kind, which is the behaviour that predates this.
            }

            return description;
        }

        private static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            // A type whose dependency is missing must not stop the rest being read.
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
            catch { return Array.Empty<Type>(); }
        }

        /// <summary>
        /// Matched by NAME rather than by type identity: the installer packages assemblies built
        /// against whichever SDK version the author used, and a type loaded through a metadata
        /// context is never reference-equal to the one this tool was compiled with.
        /// </summary>
        private static bool Implements(Type type, string interfaceName)
        {
            try { return type.GetInterfaces().Any(i => i.Name == interfaceName); }
            catch { return false; }
        }

        private static IEnumerable<string> KindsOn(Type type)
        {
            IList<CustomAttributeData> attributes;
            try { attributes = type.GetCustomAttributesData(); }
            catch { yield break; }

            foreach (var attribute in attributes)
            {
                if (attribute.AttributeType.Name != KindAttribute) continue;

                var value = attribute.ConstructorArguments.FirstOrDefault().Value as string;
                if (!string.IsNullOrWhiteSpace(value)) yield return value.Trim();
            }
        }
    }
}
