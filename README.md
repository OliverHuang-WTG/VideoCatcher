# VideoCatcher

一个内置浏览器的 Windows 视频抓取工具：在里面正常浏览网页，播放过的视频会自动出现在右侧列表，点「下载」即可保存为 PotPlayer 能直接播放的文件。

基于 WPF + WebView2（Edge 内核）+ ffmpeg。

## 功能

- **自动抓取**：监听页面的网络响应，识别 `video/*`、`audio/*`、m3u8（HLS）、mpd（DASH）以及常见视频后缀；按大小过滤小文件，同一文件的分段请求只显示一条。
- **自动命名**：取 `og:title` 或网页标题，去掉站点后缀（如 ` - YouTube`、`_哔哩哔哩_bilibili`），清洗非法字符，重名自动加 ` (1)`。取不到标题时用 `域名_时间戳`。
- **自动转封装**：统一用 ffmpeg `-c copy`，只换容器不重新编码，画质无损。
- **代理**：支持 `http://` 和 `socks5://`（可带用户名密码），浏览和下载都走代理，修改后立即生效，无需重启。
- 多标签、全屏播放、登录状态持久保存。

## 安装

从 [Releases](../../releases) 下载任一安装包运行：

| 安装包 | 说明 |
|---|---|
| `VideoCatcher-Setup-x.y.z.exe` | 自带 .NET 运行时，无需另装任何东西 |
| `VideoCatcher-Setup-x.y.z-NoRuntime.exe` | 体积更小，需要先装 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)（x64）；没装时首次启动会弹出下载提示 |

- 两个安装包都自带 ffmpeg，装的是同一个程序，后装的会覆盖先装的。
- 按用户安装到 `%LocalAppData%\Programs\VideoCatcher`，不需要管理员权限。
- 依赖 WebView2 Runtime（Windows 11 自带，Windows 10 一般也已安装）。
- 安装包未签名，SmartScreen 提示「未知发布者」时选「更多信息 → 仍要运行」。

## 使用

1. 在地址栏打开视频页面并开始播放。
2. 右侧「当前页视频」出现条目后点「下载」，进度显示在下方「下载」列表。
3. 「设置」里可修改保存目录、代理、最小文件大小。

| 快捷键 | 作用 |
|---|---|
| Ctrl+T | 新标签 |
| Ctrl+W / 鼠标中键点标签 | 关闭标签 |
| Ctrl+L | 定位到地址栏 |
| F5 | 刷新 |

### 输出格式

| 抓到的资源 | 输出 |
|---|---|
| mp4 / mkv / webm / flv / ts / mov / avi / wmv / m4v | 保持原格式 |
| m3u8 / mpd / m4s / 其他 | `.mp4` |
| 音频 | `.m4a` |
| 上面的格式写入失败（容器装不下该编码） | 自动改用 `.mkv` 重试 |

### 代理

设置里填写：

```text
http://127.0.0.1:7890
socks5://user:pass@127.0.0.1:1080
```

留空为直连。程序内部在 `127.0.0.1` 起一个中转代理，WebView2 和 ffmpeg 都连它，再由它转发到你填写的上游，所以切换代理不用重启，ffmpeg 也能用 socks5。切换时正在进行的下载会中断。

## 已知限制

- DRM（Widevine）加密内容无法抓取。
- 音视频分离的站点（如 B 站）会列出两条 `.m4s`，目前只能分别下载，暂不支持合并。
- YouTube 等使用私有传输协议的站点基本抓不到可用地址。
- 部分站点不允许在内嵌浏览器中登录 Google 账号。

## 数据位置

| 内容 | 路径 |
|---|---|
| 设置 | `%AppData%\VideoCatcher\settings.json` |
| 浏览器数据（Cookie、登录状态） | `%LocalAppData%\VideoCatcher\WebView2` |

## 从源码构建

需要 .NET 10 SDK：

```bash
dotnet publish VideoCatcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

把 `ffmpeg.exe` 放到 `publish/` 下与 `VideoCatcher.exe` 同目录（找不到时会使用 PATH 中的 `ffmpeg`）。

## 发布

推送 `v*` 格式的 tag 会触发 [release.yml](.github/workflows/release.yml)：分别发布自包含版和依赖框架版、下载 ffmpeg、用 Inno Setup 打出两个安装包，并创建 GitHub Release。

```bash
git tag v0.1.1
git push origin v0.1.1
```

## 第三方组件

- [FFmpeg](https://ffmpeg.org/)：安装包内附带 [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) 的 LGPL 构建，许可证见安装目录下的 `ffmpeg-LICENSE.txt`。
- [Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/)
