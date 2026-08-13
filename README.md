# Clipboard History for Yingqi Tools

一个面向 Windows 10/11 的 WPF 剪贴板历史组件。它读取 Windows 的 Win+V 历史，保存纯文本和图片，并提供不会在选择后自动消失的完整页面或紧凑窗口。

## 设计边界

- 只读取 Windows 剪贴板历史，不安装后台服务，也不使用 `AddClipboardFormatListener`。
- 组件停止后不再同步；再次启动时只能补回仍存在于 Win+V 的项目。
- 默认保留 200 条、30 天、500 MiB。
- 内容使用 AES-GCM 加密，主密钥由当前 Windows 用户的 DPAPI 保护。
- 写回剪贴板时禁止再次进入 Win+V，也禁止跨设备同步。
- 不保存 HTML、RTF、文件列表或高置信密钥/令牌。

## 构建

```powershell
$env:YINGQI_DOTNET = 'D:\Tools\YingqiTools\dotnet-sdk\dotnet.exe'
$env:NUGET_PACKAGES = 'D:\Tools\YingqiTools\nuget-packages'
.\build.ps1
```

## 许可证

MIT
