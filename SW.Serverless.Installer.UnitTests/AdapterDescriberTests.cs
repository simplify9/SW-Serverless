using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.Serverless.Installer.Shared;

namespace SW.Serverless.Installer.UnitTests;

/// <summary>
/// What the installer stamps onto an adapter at publish time.
///
/// Before this, a host learned an adapter's kind from a naming convention baked into its id, and
/// its lifecycle not at all — so a resident adapter looked exactly like a classic one and could be
/// offered somewhere it could never run. Reading both from the assembly means the answer comes from
/// the code rather than from whoever typed the id.
///
/// These read REAL sample adapters, built by the solution, rather than a fixture assembly written
/// to agree with the describer.
/// </summary>
[TestClass]
public class AdapterDescriberTests
{
    /// <summary>
    /// The samples are built beside the tests, so their published output is next door rather than
    /// somewhere this has to be told about.
    /// </summary>
    private static string SampleDirectory(string projectName)
    {
        var here = Path.GetDirectoryName(typeof(AdapterDescriberTests).Assembly.Location)!;

        // …/SW.Serverless.Installer.UnitTests/bin/Debug/net8.0 → …/<project>/bin/Debug/<tfm>
        var repo = Path.GetFullPath(Path.Combine(here, "..", "..", "..", ".."));
        var bin = Path.Combine(repo, projectName, "bin");
        if (!Directory.Exists(bin)) return null;

        return Directory.GetDirectories(bin, "*", SearchOption.AllDirectories)
            .FirstOrDefault(d => File.Exists(Path.Combine(d, projectName + ".dll")));
    }

    [TestMethod]
    public void A_resident_adapter_is_described_as_resident()
    {
        var directory = SampleDirectory("SW.Serverless.Samples.Greedy");
        Assert.IsNotNull(directory, "the Greedy sample was not built beside the tests");

        var description = AdapterDescriber.Describe(directory, "SW.Serverless.Samples.Greedy.dll");

        Assert.AreEqual(AdapterDescription.ResidentLifecycle, description.Lifecycle);
    }

    /// <summary>
    /// And a classic one is not. Without this the lifecycle check could be returning "resident"
    /// for everything and every test above would still pass.
    /// </summary>
    [TestMethod]
    public void A_classic_adapter_is_described_as_classic()
    {
        var directory = SampleDirectory("SW.Serverless.Samples.Classic");
        Assert.IsNotNull(directory, "the Classic sample was not built beside the tests");

        var description = AdapterDescriber.Describe(directory, "SW.Serverless.Samples.Classic.dll");

        Assert.AreEqual(AdapterDescription.ClassicLifecycle, description.Lifecycle);
    }

    /// <summary>
    /// The roles an adapter declares reach the metadata, so a host can offer it in the right place
    /// without the kind being encoded in its id — and can reclassify it later without a rename,
    /// which is not free when every configuration stores that id.
    /// </summary>
    [TestMethod]
    public void The_kinds_an_adapter_declares_are_read()
    {
        var directory = SampleDirectory("SW.Serverless.Samples.Classic");
        var description = AdapterDescriber.Describe(directory, "SW.Serverless.Samples.Classic.dll");

        // Declared twice on the handler; both come through, sorted so the value is stable.
        Assert.AreEqual("handler,mapper", description.Kind);
    }

    /// <summary>
    /// An adapter that declares nothing gets no kind. Guessing one would put it in a menu where
    /// choosing it cannot work, which is the failure this whole change exists to remove.
    /// </summary>
    [TestMethod]
    public void An_adapter_that_declares_no_kind_gets_none()
    {
        var directory = SampleDirectory("SW.Serverless.Samples.Greedy");
        var description = AdapterDescriber.Describe(directory, "SW.Serverless.Samples.Greedy.dll");

        Assert.AreEqual("", description.Kind);
    }

    /// <summary>
    /// A publish must not fail because the description could not be read, so anything unreadable
    /// describes as the classic, kind-less adapter every adapter was before this existed.
    /// </summary>
    [TestMethod]
    public void A_missing_assembly_describes_as_classic_rather_than_throwing()
    {
        var description = AdapterDescriber.Describe(
            Path.GetTempPath(), $"not-here-{Guid.NewGuid():N}.dll");

        Assert.AreEqual(AdapterDescription.ClassicLifecycle, description.Lifecycle);
        Assert.AreEqual("", description.Kind);
    }

    [TestMethod]
    public void A_missing_directory_describes_as_classic_rather_than_throwing()
    {
        var description = AdapterDescriber.Describe(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), "anything.dll");

        Assert.AreEqual(AdapterDescription.ClassicLifecycle, description.Lifecycle);
    }

    /// <summary>Every provider the tool accepts is named in one place, so help and errors agree.</summary>
    [TestMethod]
    public void Every_storage_provider_is_offered()
    {
        CollectionAssert.AreEquivalent(
            new[] { "s3", "as", "oc", "gc", "local" },
            CloudFilesFactory.Providers.ToList());
    }

    [TestMethod]
    public void An_unknown_storage_provider_is_a_clear_error()
    {
        var error = Assert.ThrowsException<SW.PrimitiveTypes.SWException>(
            () => CloudFilesFactory.Create(new ServerlessUploadOptions { Provider = "dropbox" }));

        StringAssert.Contains(error.Message, "dropbox");
        StringAssert.Contains(error.Message, "gc");
    }
}
