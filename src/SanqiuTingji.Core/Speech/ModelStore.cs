using SanqiuTingji.Localization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace SanqiuTingji.Speech;

/// The on-device models: SenseVoice (speech recognition for zh/en/ja/ko/yue, k2-fsa, Apache-2.0),
/// Silero VAD (MIT), and 3D-Speaker CAM++ (Apache-2.0). Downloaded on first use with China-direct
/// mirrors first (ModelScope + tingji.liwu.dpdns.org edge proxy) and automatic resume, or reused
/// directly from an adjacent offline `models/` directory when distributed alongside the app.
public sealed class ModelStore
{
    /// `Also`: the same model as published again upstream (re-exported, same weights), any of which
    /// is accepted: the hosts serve only the latest, so pinning one alone fails every new install.
    public sealed record ModelFile(string RelativePath, string[] Urls, long Size, string Sha256, params (long Size, string Sha256)[] Also)
    {
        public IEnumerable<(long Size, string Sha256)> Versions => Also.Prepend((Size, Sha256));
        public bool Fits(long length) => Versions.Any(v => v.Size == length);
        public bool Matches(long length, string sha256) => Versions.Any(v => v.Size == length && v.Sha256 == sha256);
    }

    private const string MirrorBase = "https://tingji.liwu.dpdns.org/models/";
    private const string ModelScopeSenseVoice = "https://www.modelscope.cn/models/gomodels/sherpa/resolve/master/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/";
    private const string SenseVoice = "csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/resolve/main/";
    private const string SpeakerFile = "3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx";

    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    public static readonly ModelFile[] Files =
    [
        new("sense-voice/model.int8.onnx",
            [
                $"{ModelScopeSenseVoice}model.int8.onnx",
                $"{MirrorBase}sense-voice/model.int8.onnx",
                $"https://hf-mirror.com/{SenseVoice}model.int8.onnx",
                $"https://huggingface.co/{SenseVoice}model.int8.onnx",
            ],
            239_233_841, "c71f0ce00bec95b07744e116345e33d8cbbe08cef896382cf907bf4b51a2cd51"),
        new("sense-voice/tokens.txt",
            [
                $"{ModelScopeSenseVoice}tokens.txt",
                $"{MirrorBase}sense-voice/tokens.txt",
                $"https://hf-mirror.com/{SenseVoice}tokens.txt",
                $"https://huggingface.co/{SenseVoice}tokens.txt",
            ],
            315_894, "f449eb28dc567533d7fa59be34e2abca8784f771850c78a47fb731a31429a1dc"),
        // Silero VAD: try China-direct edge proxy first (hf-mirror currently 308-redirects back to huggingface.co).
        new("silero_vad.onnx",
            [
                $"{MirrorBase}silero_vad.onnx",
                "https://hf-mirror.com/csukuangfj/vad/resolve/main/silero_vad.onnx",
                "https://huggingface.co/csukuangfj/vad/resolve/main/silero_vad.onnx",
            ],
            1_807_522, "a35ebf52fd3ce5f1469b2a36158dba761bc47b973ea3382b3186ca15b1f5af28"),
        // Voiceprints for telling speakers apart: 3D-Speaker CAM++, trained on ~200k Chinese speakers
        // (Apache-2.0). China-direct edge proxy first, then mirror / GitHub Releases fallback.
        new(SpeakerFile,
            [
                $"{MirrorBase}{SpeakerFile}",
                $"https://hf-mirror.com/csukuangfj/speaker-embedding-models/resolve/main/{SpeakerFile}",
                $"https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/{SpeakerFile}",
            ],
            28_281_139, "e6af4d4c444f3dab01d991bb9b7d5af98eb979e329f6c8880a19d5553561ed25",
            // The November 2026 re-export changed only AveragePool's count_include_pad flag; its
            // voiceprints equal the earlier file's (1.0000 on 120 clips), so voices kept from it still match.
            (28_281_138, "f682b514c05d947ee3fa91cd6ec6c5c7543479a128373fa29b1faedccd21fd11")),
    ];

    public string Root { get; }
    /// Optional offline seed directory (e.g. `<AppDir>/models`) checked before hitting the network.
    public string? BundledRoot { get; }

    public ModelStore(string? root = null, string? bundledRoot = null)
    {
        Root = root ?? Paths.Models;
        BundledRoot = bundledRoot ?? Path.Combine(AppContext.BaseDirectory, "models");
    }

    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
    public string SenseVoiceModel => PathOf("sense-voice/model.int8.onnx");
    public string SenseVoiceTokens => PathOf("sense-voice/tokens.txt");
    public string Vad => PathOf("silero_vad.onnx");
    public string Speaker => PathOf(SpeakerFile);

    public long TotalBytes => Files.Sum(f => f.Size);

    /// Present with the right size; the hash is checked once after download and remembered.
    public bool Ready => Files.All(f => new FileInfo(PathOf(f.RelativePath)) is { Exists: true } i && f.Fits(i.Length)
                                        && File.Exists(PathOf(f.RelativePath) + ".ok"));

    /// Copies verified model files from an adjacent offline `models/` folder when present.
    public async Task<int> TryImportBundledAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(BundledRoot) || !Directory.Exists(BundledRoot)) return 0;
        var fullRoot = Path.GetFullPath(Root);
        var fullBundled = Path.GetFullPath(BundledRoot);
        if (string.Equals(fullRoot, fullBundled, StringComparison.OrdinalIgnoreCase)) return 0;

        var imported = 0;
        foreach (var f in Files)
        {
            var target = PathOf(f.RelativePath);
            if (new FileInfo(target) is { Exists: true } info && f.Fits(info.Length) && File.Exists(target + ".ok"))
                continue;
            var seed = Path.Combine(fullBundled, f.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (new FileInfo(seed) is not { Exists: true } seedInfo || !f.Fits(seedInfo.Length))
                continue;
            var hash = await Sha256Of(seed, ct);
            if (!f.Matches(seedInfo.Length, hash)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(seed, target, overwrite: true);
            await File.WriteAllTextAsync(target + ".ok", hash, ct);
            imported++;
            Log.Debug($"models: {f.RelativePath} imported from local seed directory");
        }
        return imported;
    }

    /// Downloads what is missing. `progress` gets (bytes done, bytes total).
    public async Task Download(IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        await TryImportBundledAsync(ct);

        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = HeaderTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SanqiuTingji/1.0");
        long doneBefore = 0;
        foreach (var f in Files)
        {
            var target = PathOf(f.RelativePath);
            if (new FileInfo(target) is { Exists: true } info && f.Fits(info.Length) && File.Exists(target + ".ok"))
            {
                doneBefore += f.Size;
                progress?.Report((doneBefore, TotalBytes));
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // A file already there (put by hand, or from before) only needs its hash checked.
            if (File.Exists(target) && new FileInfo(target).Length is var have && f.Fits(have)
                && await Sha256Of(target, ct) is var haveHash && f.Matches(have, haveHash))
            {
                await File.WriteAllTextAsync(target + ".ok", haveHash, ct);
                doneBefore += f.Size;
                progress?.Report((doneBefore, TotalBytes));
                continue;
            }
            Exception? last = null;
            var ok = false;
            var part = target + ".part";
            foreach (var url in f.Urls)
            {
                try
                {
                    await DownloadOneUrlAsync(http, url, f, part, doneBefore, progress, ct);
                    var hash = await Sha256Of(part, ct);
                    var length = new FileInfo(part).Length;
                    if (!f.Matches(length, hash))
                    {
                        try { File.Delete(part); } catch { }
                        throw new InvalidDataException($"checksum mismatch for {f.RelativePath}: {length} bytes, {hash}");
                    }
                    File.Move(part, target, true);
                    await File.WriteAllTextAsync(target + ".ok", hash, ct);
                    ok = true;
                    Log.Debug($"models: {f.RelativePath} downloaded from {new Uri(url).Host}");
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    last = e;
                    Log.Debug($"models: {f.RelativePath} from {new Uri(url).Host} failed: {e.Message}");
                }
            }
            if (!ok) throw new IOException(L.F("下载 {0} 失败", f.RelativePath), last);
            doneBefore += f.Size;
        }
    }

    private async Task DownloadOneUrlAsync(
        HttpClient http,
        string url,
        ModelFile f,
        string part,
        long doneBefore,
        IProgress<(long Done, long Total)>? progress,
        CancellationToken ct)
    {
        var maxExpected = f.Versions.Max(v => v.Size);
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (existing < 0 || existing > maxExpected)
        {
            try { File.Delete(part); } catch { }
            existing = 0;
        }

        // If a previous interrupted run already finished writing all bytes, just verify it.
        if (existing > 0 && f.Fits(existing))
        {
            progress?.Report((doneBefore + existing, TotalBytes));
            return;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);

        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerCts.CancelAfter(HeaderTimeout);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headerCts.Token);

        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            try { File.Delete(part); } catch { }
            throw new IOException("range not satisfiable, retrying clean");
        }
        resp.EnsureSuccessStatusCode();

        var resumed = existing > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
        long got = resumed ? existing : 0;
        progress?.Report((doneBefore + got, TotalBytes));

        await using var src = await resp.Content.ReadAsStreamAsync(headerCts.Token);
        await using var dst = new FileStream(
            part,
            resumed ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1 << 16,
            useAsync: true);

        var buffer = new byte[1 << 16];
        while (true)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(StallTimeout);
            int n;
            try
            {
                n = await src.ReadAsync(buffer, readCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"download stalled (> {(int)StallTimeout.TotalSeconds}s without data)");
            }
            if (n <= 0) break;
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            got += n;
            if (got > maxExpected + 4096)
            {
                dst.Close();
                try { File.Delete(part); } catch { }
                throw new InvalidDataException($"unexpected response size for {f.RelativePath} (> {maxExpected} bytes)");
            }
            progress?.Report((doneBefore + Math.Min(got, f.Size), TotalBytes));
        }
    }

    private static async Task<string> Sha256Of(string file, CancellationToken ct)
    {
        await using var s = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(s, ct)).ToLowerInvariant();
    }
}
