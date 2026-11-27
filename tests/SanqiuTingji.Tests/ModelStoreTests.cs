using SanqiuTingji.Speech;
using Xunit;

namespace SanqiuTingji.Tests;

/// A model published again upstream (same weights, new bytes) must still install, and a copy of the
/// earlier file already on disk must stay accepted.
public class ModelStoreTests
{
    private static ModelStore.ModelFile Speaker => ModelStore.Files.Single(f => f.RelativePath.Contains("campplus"));

    [Fact]
    public void TheSpeakerModelAcceptsTheFileHostedNowAndTheEarlierOne()
    {
        Assert.True(Speaker.Matches(28_281_139, "e6af4d4c444f3dab01d991bb9b7d5af98eb979e329f6c8880a19d5553561ed25"));
        Assert.True(Speaker.Matches(28_281_138, "f682b514c05d947ee3fa91cd6ec6c5c7543479a128373fa29b1faedccd21fd11"));
    }

    [Fact]
    public void AnyOtherFileIsRefused()
    {
        // Size and hash have to belong to the same version.
        Assert.False(Speaker.Matches(28_281_139, "f682b514c05d947ee3fa91cd6ec6c5c7543479a128373fa29b1faedccd21fd11"));
        Assert.False(Speaker.Matches(28_281_139, new string('0', 64)));
        Assert.False(Speaker.Fits(28_281_140));
    }

    [Fact]
    public void EveryModelPinsAFullHash()
    {
        foreach (var f in ModelStore.Files)
            Assert.All(f.Versions, v => Assert.Matches("^[0-9a-f]{64}$", v.Sha256));
    }

    [Fact]
    public void EveryModelTriesChinaDirectMirrorBeforeForeignHosts()
    {
        foreach (var f in ModelStore.Files)
        {
            Assert.NotEmpty(f.Urls);
            var firstHost = new Uri(f.Urls[0]).Host;
            Assert.True(
                firstHost.EndsWith("modelscope.cn", StringComparison.OrdinalIgnoreCase)
                || firstHost.Equals("tingji.liwu.dpdns.org", StringComparison.OrdinalIgnoreCase),
                $"{f.RelativePath} first host was {firstHost}");
            Assert.Contains(f.Urls, u => new Uri(u).Host.Equals("tingji.liwu.dpdns.org", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task BundledModelsDirectoryCopiesVerifiedFilesOffline()
    {
        var temp = Path.Combine(Path.GetTempPath(), "sanqiutingji-models-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var targetRoot = Path.Combine(temp, "target");
            var seedRoot = Path.Combine(temp, "seed");
            Directory.CreateDirectory(seedRoot);
            // Put a bad file (wrong size/hash) in seed: must be ignored, never copied.
            File.WriteAllBytes(Path.Combine(seedRoot, "silero_vad.onnx"), [1, 2, 3, 4]);

            var store = new ModelStore(targetRoot, seedRoot);
            Assert.Equal(0, await store.TryImportBundledAsync());
            Assert.False(File.Exists(store.Vad));
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
        }
    }
}
