# 贡献指南

感谢关注 Pico Vibe。本仓库开源的是 **Pico Vibe 桌面应用**（`app/`）；设备固件保持闭源。

## 欢迎的改动

- Cursor / Codex / Claude Code 钩子适配与回归测试
- macOS / Windows UI、灯效、无障碍与本地化
- Bug 修复与测试补充

## 不接受的请求

- 要求公开或逆向固件、OTA、OTP、量产工具
- 绕过 AI 客户端 hook 信任机制，或伪造授权结果
- 提交安装包、签名证书、本机配置/日志、密钥材料

## 开发前检查

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet test app/PicoVibe.Tests/PicoVibe.Tests.csproj -c Release
dotnet test app/PicoVibe.App.Tests/PicoVibe.App.Tests.csproj -c Release
dotnet test app/PicoVibe.Hook.Tests/PicoVibe.Hook.Tests.csproj -c Release
dotnet test app/PicoVibe.Runtime.Tests/PicoVibe.Runtime.Tests.csproj -c Release
```

请保持改动聚焦；大功能先开 issue 对齐产品边界。

## 许可

贡献默认按仓库根目录 [LICENSE](LICENSE)（MIT）授权。第三方依赖见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
