# SafariPark.Web — 网页版野生动物园

`SafariPark.Core` 模拟层的浏览器宿主：.NET WebAssembly + `Paradise.Rendering.Browser`
（WebGPU）渲染，DOM 面板做 HUD，键盘/鼠标经 `[JSExport]` 桥接进游戏。

## 构建与发布

```bash
dotnet publish SafariPark.Web/SafariPark.Web.csproj -c Release
# 产物: SafariPark.Web/bin/Release/net10.0/publish/wwwroot  (~15 MB wasm)
```

本地预览（任意静态服务器；`serve.mjs` 支持 .br/.gz 预压缩和 wasm MIME）：

```bash
node SafariPark.Web/serve.mjs 8756 SafariPark.Web/bin/Release/net10.0/publish/wwwroot
# http://127.0.0.1:8756
```

## Cloudflare Tunnel

快速隧道（无需账号，域名随机，进程退出即失效）：

```bash
cloudflared tunnel --url http://127.0.0.1:8756 --protocol http2 --no-autoupdate
# 输出里找 https://<random>.trycloudflare.com
```

本机网络 QUIC/UDP 7844 不通，`--protocol http2` 必传。长期公开请改用
[named tunnel](https://developers.cloudflare.com/cloudflare-one/connections/connect-apps/)
（需要 Cloudflare 账号，固定域名+进程托管）。

## QA 调试接口（JSExport）

页面上 `window.__safariProgram` 暴露全部 `[JSExport]`：

```js
__safariProgram.DebugSpawnAhead(7);    // 0=猫 1=鹿 2=兔 3=狐 4=熊 5=松鼠 6=鸭 7=人，生于视野前方
__safariProgram.DebugSpawnItem(0);     // 0=松果 1=橡果 2=木棍 3=石块，生于玩家脚前
__safariProgram.DebugTeleport(x, z);   // 传送受控动物
__safariProgram.DebugTeleportToTree(); // 传送猫到最近可爬树旁
__safariProgram.DebugCatState();       // JSON: {x,y,z,cling,held,items,wanderers}
```

## 验证过的行为

- `SAFARI-OK frames=300`：300 帧无 WebGPU 异步错误（与引擎 sample 同一验收门槛）
- E 拾取/攀爬树干，W/S 上下攀爬，顶端 pop 到树冠，Space 弹射下树
- E+F 拾取投掷物砸访客：松果飞行命中→访客倒地，HUD 闪"打中!"
- 林内随机生成路过的小动物和人类访客；Tab/Q/roster 切换控制动物

## 页面结构

- `wwwroot/index.html` — canvas + HUD 布局
- `wwwroot/main.js` — dotnet 引导、rAF 泵、DOM→managed 输入桥
- `wwwroot/safari-host.js` — `[JSImport]` DOM 助手（状态/roster 列表）
- `Program.cs` — `[JSExport]` 宿主 API：Init/帧泵/输入/切换/roster 推送
- `serve.mjs` — 发布目录静态服务器（br/gz/wasm MIME）

浏览器要求：支持 WebGPU 的 Chrome/Edge 113+（HTTPS 或 localhost 才暴露 `navigator.gpu`）。
