# 三秋听记官网与国内直连分发节点 (`tingji.liwu.dpdns.org`)

本目录采用 **架构 A（Cloudflare Worker + Static Assets）**，单次部署同时提供：

1. `/` — 三秋听记产品官网落地页（`public/index.html`）
2. `/notices.json` — 客户端左上角「消息与更新」静态只读公告源（边缘缓存 GitHub Raw，异常时自动回退内置 `public/notices.json`）
3. `/releases/*` — 客户端 Velopack 国内直连更新源（边缘代理并缓存 `releases.win.json`、增量包 `*-delta.nupkg` 与完整包 `*-full.nupkg`，支持 Range 断点续传）
4. `/download/*` — 落地页安装包 `SanqiuTingji-Setup.exe` 与便携版 `SanqiuTingji.App-win-Portable.zip` 国内直连下载入口

## 首次授权与一键部署

```powershell
cd site
npm install
npx wrangler login   # 浏览器点击 Allow 授权（仅首次需要）
npm run deploy       # 自动同步 ../notices.json 并部署绑定 tingji.liwu.dpdns.org
```
