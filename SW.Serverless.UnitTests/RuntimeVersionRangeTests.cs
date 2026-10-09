using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.Serverless.Runtimes;

namespace SW.Serverless.UnitTests
{
    [TestClass]
    public class RuntimeVersionRangeTests
    {
        [DataTestMethod]
        [DataRow(null, "3.11.0", true)]
        [DataRow(">=3.12", "3.12.0", true)]
        [DataRow(">=3.12", "3.13.1", true)]
        [DataRow(">=3.12", "3.11.9", false)]
        [DataRow(">=22, <24", "22.9.0", true)]
        [DataRow(">=22, <24", "24.0.0", false)]
        [DataRow(">=22 <24", "23.1.0", true)]
        [DataRow("3.12", "3.12.7", true)]
        [DataRow("3.12", "3.13.0", false)]
        [DataRow("=22", "22.11.0", true)]
        [DataRow(">3.12.0", "3.12.0", false)]
        [DataRow("<=3.12", "3.12.0", true)]
        [DataRow("v22", "22.1.0", true)]
        [DataRow("lots", "3.12.0", false)]
        [DataRow(">=3.12", "not-a-version", false)]
        public void A_runtime_version_is_in_or_out_of_range(string range, string version, bool expected) =>
            Assert.AreEqual(expected, RuntimeVersionRange.Satisfies(range, version), $"{version} in '{range}'");
    }
}
