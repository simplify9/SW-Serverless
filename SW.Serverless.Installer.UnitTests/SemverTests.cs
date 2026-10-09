using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.Serverless.Tooling;

namespace SW.Serverless.UnitTests;

[TestClass]
public class SemverTests
{
    [TestMethod]
    public void TestNoPreviousVersionsFound1()
    {
        var version = Semver.GetNewVersion("major", new List<string>());
        Assert.AreEqual("1.0.0", version);
    }

    [TestMethod]
    public void TestNoPreviousVersionsFound2()
    {
        var version = Semver.GetNewVersion("major", new List<string>());
        Assert.AreEqual("1.0.0", version);
    }


    [TestMethod]
    public void TestHardcodedVersionWithNoOlderVersions()
    {
        var version = Semver.GetNewVersion("1.3.4", new List<string>());
        Assert.AreEqual("1.3.4", version);
    }

    [TestMethod]
    public void TestMajorVersionBump()
    {
        var version = Semver.GetNewVersion("major", new List<string>
        {
            "1.1.1",
            "1.1.2",
            "1.1.3",
            "1.1.4",
            "1.2.5",
        });
        Assert.AreEqual("2.0.0", version);
    }

    [TestMethod]
    public void TestMinorVersionBump()
    {
        var version = Semver.GetNewVersion("minor", new List<string>
        {
            "1.1.1",
            "1.1.2",
            "1.1.3",
            "1.1.4",
            "1.2.5",
        });
        Assert.AreEqual("1.3.0", version);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void TestDowngrade()
    {
        var version = Semver.GetNewVersion("1.0.5", new List<string>
        {
            "1.1.1",
            "1.1.2",
            "1.1.3",
            "1.1.4",
            "1.2.5",
        });
    }
    
    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void TestEqual()
    {
        var version = Semver.GetNewVersion("1.2.5", new List<string>
        {
            "1.1.1",
            "1.1.2",
            "1.1.3",
            "1.1.4",
            "1.2.5",
        });
    }
}