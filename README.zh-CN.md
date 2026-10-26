# VideoCatcher

[English](README.md) | 简体中文

一个内置浏览器的 Windows 视频抓取工具：在里面正常浏览网页，播放过的视频会自动出现在侧边栏里，点「下载」即可保存为 PotPlayer 能直接播放的文件。

基于 WPF + WebView2（Edge 内核）+ ffmpeg。

## 功能

- **自动抓取**：监听页面的网络响应，识别 `video/*`、`audio/*`、m3u8（HLS）、mpd（DASH）以及常见视频后缀；按大小过滤小文件，同一文件的分段请求只显示一条。m3u8 会显示估算的大小、分辨率和时长；master 列表下的各清晰度子列表不再单独列出。
- **并行下载**：HLS 分片同时下载多个（支持 AES-128 解密）；服务器支持断点续传的直链文件分成多段同时下载。连接数在设置里配置，默认 8。之后 ffmpeg 只在本地合并。
- **自动命名**：用源文件名——优先取服务器 `Content-Disposition` 里的文件名，其次取 URL 最后一段；没有文件名或是 `index`、`master`、`playlist` 这类通用名时，生成 8 位随机字母。清洗非法字符，重名自动加 ` (1)`。
- **自动转封装**：统一用 ffmpeg `-c copy`，只换容器不重新编码，画质无损。
- **代理**：支持 `http://` 和 `socks5://`（可带用户名密码），浏览和下载都走代理；工具栏一键开关，立即生效，无需重启。
- Edge 风格界面（跟随系统浅色/深色）：标签页在标题栏、地址栏自动补全 https、多标签、全屏播放、登录状态持久保存。

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

1. 新标签页是空白页，光标自动在地址栏里。打开视频页面并开始播放。地址栏的规则和 Edge 一样：带协议的原样打开；像网址的（如 `example.com`、`localhost:3000`）先用 `https://`，连不上再自动改用 `http://`；其他内容用 Bing 搜索。
2. 右侧侧边栏上半部分是当前页抓到的视频，下半部分是下载列表，中间的分隔线可以拖动调整两部分的高度。点视频旁的「下载」，进度显示在下载列表里。
3. 点侧边栏的箭头按钮可以收起侧边栏，收起后只剩一个图标，显示抓到的视频数量，点它重新展开；拖动侧边栏左边缘可以调整宽度。展开状态和宽度都会记住。
4. 下载完成后双击即可用系统默认播放器打开；右键可以「在文件夹中显示」或「从列表移除」；下载区标题栏的「清空列表」可以一次移除所有已完成、失败和已取消的条目（都不会删除文件）。已完成的下载在重启程序后仍会保留。
5. 「设置」里可修改保存目录、代理地址、最小文件大小和下载线程数（每个下载 1 到 32 个连接）。工具栏的「代理」按钮开关代理：开启时按钮填充为强调色，并显示「代理已开」。

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

工具栏「代理」按钮高亮表示开启，关闭或未填写时直连。程序内部在 `127.0.0.1` 起一个中转代理，WebView2 和 ffmpeg 都连它，再由它转发到你填写的上游，所以切换代理不用重启，ffmpeg 也能用 socks5。切换时正在进行的下载会中断。

## 已知限制

- DRM（Widevine）加密内容无法抓取。
- 音视频分离的站点（如 B 站）会列出两条 `.m4s`，目前只能分别下载，暂不支持合并。
- 直播 HLS、DASH（mpd）以及 AES-128 以外的加密，仍由 ffmpeg 自己逐个分片下载。
- YouTube 等使用私有传输协议的站点基本抓不到可用地址。
- 部分站点不允许在内嵌浏览器中登录 Google 账号。
- 字幕支持有限：内置的精简版 ffmpeg 只认 HLS 中的 WebVTT 字幕；带这种字幕时会保存为 `.mkv`，因为 mp4 装不下。

## 数据位置

| 内容 | 路径 |
|---|---|
| 设置 | `%AppData%\VideoCatcher\settings.json` |
| 下载记录 | `%AppData%\VideoCatcher\downloads.json` |
| 浏览器数据（Cookie、登录状态） | `%LocalAppData%\VideoCatcher\WebView2` |

## 从源码构建

需要 .NET 10 SDK：

```bash
dotnet publish VideoCatcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

把 `ffmpeg.exe` 放到 `publish/` 下与 `VideoCatcher.exe` 同目录（找不到时会使用 PATH 中的 `ffmpeg`）。任何完整版 ffmpeg 都可以；要和安装包里的精简版一致，在 Linux 或 WSL 上安装 `gcc-mingw-w64-x86-64` 和 `cmake` 后运行：

```bash
bash ffmpeg/build.sh ffmpeg-out
```

## 发布

推送 `v*` 格式的 tag 会触发 [release.yml](.github/workflows/release.yml)：在 Ubuntu 上交叉编译精简版 ffmpeg（结果按 `build.sh` 的内容缓存）并用公开测试流做冒烟测试，再分别发布自包含版和依赖框架版、用 Inno Setup 打出两个安装包，并创建 GitHub Release。

```bash
git tag vX.Y.Z
git push origin vX.Y.Z
```

## 第三方组件

- [FFmpeg](https://ffmpeg.org/)：安装包内附带由 [ffmpeg/build.sh](ffmpeg/build.sh) 从官方源码编译的精简 LGPL 构建，只保留流复制（`-c copy`）所需的协议、封装和解析组件，不含编码器。`ffmpeg -version` 可查看完整编译参数，许可证见安装目录下的 `ffmpeg-LICENSE.txt`。
- [Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/)
