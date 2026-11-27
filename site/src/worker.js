const GITHUB_OWNER = "lisanqiu9-ops";
const GITHUB_REPO = "sanqiu-tingji";
const GITHUB_BASE = `https://github.com/${GITHUB_OWNER}/${GITHUB_REPO}`;
const RAW_NOTICES_URLS = [
  `https://raw.githubusercontent.com/${GITHUB_OWNER}/${GITHUB_REPO}/feat/local-custom/notices.json`,
  `https://raw.githubusercontent.com/${GITHUB_OWNER}/${GITHUB_REPO}/main/notices.json`,
];

const RELEASE_FEED_RE = /^(releases\.[a-z0-9._-]+\.json|RELEASES)$/i;
const RELEASE_PKG_RE = /^SanqiuTingji\.App-([0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.]+)?)-(full|delta)\.nupkg$/;
const RELEASE_NOTES_RE = /^v([0-9]+\.[0-9]+\.[0-9]+)\.md$/;
const DOWNLOAD_ALLOWLIST = new Set([
  "SanqiuTingji-Setup.exe",
  "SanqiuTingji.App-win-Portable.zip",
]);
const MODEL_UPSTREAMS = {
  "sense-voice/model.int8.onnx": [
    "https://huggingface.co/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/resolve/main/model.int8.onnx",
    "https://www.modelscope.cn/models/gomodels/sherpa/resolve/master/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/model.int8.onnx",
  ],
  "sense-voice/tokens.txt": [
    "https://huggingface.co/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/resolve/main/tokens.txt",
    "https://www.modelscope.cn/models/gomodels/sherpa/resolve/master/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/tokens.txt",
  ],
  "silero_vad.onnx": [
    "https://huggingface.co/csukuangfj/vad/resolve/main/silero_vad.onnx",
  ],
  "3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx": [
    "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx",
    "https://huggingface.co/csukuangfj/speaker-embedding-models/resolve/main/3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx",
  ],
};

function corsHeaders(extra = {}) {
  return {
    "Access-Control-Allow-Origin": "*",
    "Access-Control-Allow-Methods": "GET, HEAD, OPTIONS",
    "X-Content-Type-Options": "nosniff",
    ...extra,
  };
}

async function fetchWithTimeout(url, init, timeoutMs) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    return await fetch(url, { ...init, signal: controller.signal });
  } finally {
    clearTimeout(timer);
  }
}

async function handleNotices(request, env, ctx) {
  const cache = caches.default;
  const cacheKey = new Request(new URL("/notices.json", request.url).toString(), { method: "GET" });
  const cached = await cache.match(cacheKey);
  if (cached) {
    return request.method === "HEAD" ? new Response(null, cached) : cached;
  }

  for (const rawUrl of RAW_NOTICES_URLS) {
    try {
      const upstream = await fetchWithTimeout(
        rawUrl,
        { headers: { "User-Agent": "SanqiuTingji-Edge/1.0" } },
        3500
      );
      if (upstream.ok) {
        const text = await upstream.text();
        JSON.parse(text); // Ensure valid JSON before caching
        const resp = new Response(text, {
          status: 200,
          headers: corsHeaders({
            "Content-Type": "application/json; charset=utf-8",
            "Cache-Control": "public, max-age=120, s-maxage=300",
          }),
        });
        ctx.waitUntil(cache.put(cacheKey, resp.clone()));
        return request.method === "HEAD" ? new Response(null, resp) : resp;
      }
    } catch (_) {
      // Fall through to next raw URL or bundled static notices.json
    }
  }

  // Fallback to bundled static notices.json in ./public
  const assetResp = await env.ASSETS.fetch(new Request(new URL("/notices.json", request.url), request));
  const headers = new Headers(assetResp.headers);
  Object.entries(corsHeaders({ "Cache-Control": "public, max-age=60" })).forEach(([k, v]) => headers.set(k, v));
  return new Response(request.method === "HEAD" ? null : assetResp.body, {
    status: assetResp.status,
    headers,
  });
}

async function proxyReleaseFile(request, candidateUrls, fileName, { ttlSeconds, forceDownload }) {
  const forwardHeaders = new Headers({
    "User-Agent": "SanqiuTingji-EdgeProxy/1.0",
  });
  const range = request.headers.get("Range");
  if (range) forwardHeaders.set("Range", range);
  const ifNoneMatch = request.headers.get("If-None-Match");
  if (ifNoneMatch) forwardHeaders.set("If-None-Match", ifNoneMatch);

  let lastResp = null;
  for (const targetUrl of candidateUrls) {
    const upstream = await fetch(targetUrl, {
      method: request.method === "HEAD" ? "HEAD" : "GET",
      headers: forwardHeaders,
      redirect: "follow",
      cf: {
        cacheEverything: true,
        cacheTtlByStatus: {
          "200-299": ttlSeconds,
          "404": 30,
          "500-599": 0,
        },
      },
    });

    if (upstream.ok || upstream.status === 206 || upstream.status === 304) {
      const outHeaders = new Headers();
      for (const h of [
        "Content-Type",
        "Content-Length",
        "Content-Range",
        "Accept-Ranges",
        "ETag",
        "Last-Modified",
      ]) {
        const val = upstream.headers.get(h);
        if (val) outHeaders.set(h, val);
      }

      if (fileName.endsWith(".json")) {
        outHeaders.set("Content-Type", "application/json; charset=utf-8");
      } else if (forceDownload) {
        outHeaders.set("Content-Disposition", `attachment; filename="${fileName}"`);
      }

      const browserMaxAge = Math.min(ttlSeconds, 300);
      outHeaders.set("Cache-Control", `public, max-age=${browserMaxAge}, s-maxage=${ttlSeconds}`);
      Object.entries(corsHeaders()).forEach(([k, v]) => outHeaders.set(k, v));

      return new Response(request.method === "HEAD" ? null : upstream.body, {
        status: upstream.status,
        headers: outHeaders,
      });
    }
    lastResp = upstream;
  }

  return new Response("Release asset not found", {
    status: lastResp ? lastResp.status : 404,
    headers: corsHeaders({ "Content-Type": "text/plain; charset=utf-8", "Cache-Control": "no-store" }),
  });
}

export default {
  async fetch(request, env, ctx) {
    if (request.method === "OPTIONS") {
      return new Response(null, { status: 204, headers: corsHeaders() });
    }
    if (request.method !== "GET" && request.method !== "HEAD") {
      return new Response("Method Not Allowed", { status: 405, headers: corsHeaders() });
    }

    const url = new URL(request.url);
    const path = url.pathname;

    // 1. Static notices feed (edge cached GitHub Raw -> bundled public/notices.json fallback)
    if (path === "/notices.json") {
      return handleNotices(request, env, ctx);
    }

    // 2. Release notes markdown for in-app "What's new" fallback: /releases/notes/v1.0.5.md
    if (path.startsWith("/releases/notes/")) {
      const noteFile = decodeURIComponent(path.slice("/releases/notes/".length));
      const m = RELEASE_NOTES_RE.exec(noteFile);
      if (!m) {
        return new Response("Not Found", { status: 404, headers: corsHeaders() });
      }
      const tag = `v${m[1]}`;
      return proxyReleaseFile(
        request,
        [
          `https://raw.githubusercontent.com/${GITHUB_OWNER}/${GITHUB_REPO}/${tag}/release-notes/${noteFile}`,
          `https://raw.githubusercontent.com/${GITHUB_OWNER}/${GITHUB_REPO}/feat/local-custom/release-notes/${noteFile}`,
        ],
        noteFile,
        { ttlSeconds: 3600, forceDownload: false }
      );
    }

    // 3. Velopack SimpleWebSource endpoint: /releases/releases.win.json & /releases/*.nupkg
    if (path.startsWith("/releases/")) {
      const fileName = decodeURIComponent(path.slice("/releases/".length));
      if (RELEASE_FEED_RE.test(fileName)) {
        return proxyReleaseFile(
          request,
          [`${GITHUB_BASE}/releases/latest/download/${fileName}`],
          fileName,
          { ttlSeconds: 180, forceDownload: false }
        );
      }

      const pkgMatch = RELEASE_PKG_RE.exec(fileName);
      if (pkgMatch) {
        const ver = pkgMatch[1];
        return proxyReleaseFile(
          request,
          [
            `${GITHUB_BASE}/releases/latest/download/${fileName}`,
            `${GITHUB_BASE}/releases/download/v${ver}/${fileName}`,
          ],
          fileName,
          { ttlSeconds: 604800, forceDownload: false }
        );
      }

      return new Response("Not Found", { status: 404, headers: corsHeaders() });
    }

    // 4. Direct installer & portable downloads from landing page: /download/<file>
    if (path.startsWith("/download/")) {
      const fileName = decodeURIComponent(path.slice("/download/".length));
      if (!DOWNLOAD_ALLOWLIST.has(fileName)) {
        return new Response("Not Found", { status: 404, headers: corsHeaders() });
      }
      return proxyReleaseFile(
        request,
        [`${GITHUB_BASE}/releases/latest/download/${fileName}`],
        fileName,
        { ttlSeconds: 1800, forceDownload: true }
      );
    }

    // 5. China-direct speech & voiceprint model mirror: /models/<relativePath>
    if (path.startsWith("/models/")) {
      const modelRel = decodeURIComponent(path.slice("/models/".length));
      const upstreams = MODEL_UPSTREAMS[modelRel];
      if (!upstreams) {
        return new Response("Not Found", { status: 404, headers: corsHeaders() });
      }
      const fileName = modelRel.split("/").pop() || "model.bin";
      return proxyReleaseFile(request, upstreams, fileName, {
        ttlSeconds: 2592000,
        forceDownload: false,
      });
    }

    // 6. Landing page & static assets
    return env.ASSETS.fetch(request);
  },
};
