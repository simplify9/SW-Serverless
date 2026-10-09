using System;

namespace SW.Serverless.Sdk
{
    /// <summary>
    /// A contract this adapter implements, and its version — for Bitween, "bitween" 1. Reported
    /// in the adapter's description and handshake, and checked by <c>serverless test</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class AdapterContractAttribute : Attribute
    {
        public AdapterContractAttribute(string name, int version)
        {
            Name = name;
            Version = version;
        }

        public string Name { get; }
        public int Version { get; }
    }
}
