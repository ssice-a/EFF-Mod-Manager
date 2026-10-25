# EFF Mod Manager

Windows 桌面管理器，用于安装 EFF、管理本地 Mod，并在管理器内修复旧版 Mod。

## 当前功能

- 扫描或手动选择 Endfield 游戏目录。
- 从 `ssice-a/EFF` 最新 Release 下载 DLL 压缩包，校验 GitHub SHA-256 后安装或更新。
- 卸载时按文件哈希判断归属，恢复安装前备份；被外部修改的文件会保留。
- 将 Mod 文件夹或 ZIP 拖入窗口安装；检查 ZIP 路径、链接和展开体积。
- 扫描 `plugin/mods` 中的嵌套 Mod，查看作者、版本、描述和快捷键；使用复选框启用或停用。
- 删除所选 Mod、中文/英文界面、选择修复索引资源目录。
- 检查并安装本仓库发布的管理器更新。
- 在 Repair 页勾选 Mod 后直接修复；管理器会从 EFF Release 下载并校验与当前游戏资源匹配的索引，无需启动独立 Repair 程序。

程序启动时自动检查 EFF DLL 与管理器更新。设置页保留“立即检查”按钮。管理器更新从本仓库 Release 获取；EFF DLL 和修复索引从 `ssice-a/EFF` 获取。

## 构建

需要 Windows 与 .NET 9 SDK。发布包依赖 .NET 9 Desktop Runtime，运行前需安装该运行时：

```powershell
dotnet build EFF.ModManager/EFF.ModManager.csproj -c Release
dotnet publish EFF.ModManager/EFF.ModManager.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

运行 `publish/EFF.ModManager.exe`。首次启动会尝试扫描游戏并检查 EFF 与管理器更新。游戏运行时不能安装或卸载 DLL。

## 发布

推送 `v*` 标签后，GitHub Actions 会生成 Windows x64 小体积发布包并创建 Release。管理器启动时检查该 Release；发现新版后会询问是否下载并重启。更新包会一并替换 Repair 使用的原生资源工具。尚未创建本仓库 Release 时，更新状态会显示尚未发布。

运行 Mod 文件操作测试：

```powershell
dotnet run --project tests/EFF.ModManager.Tests/EFF.ModManager.Tests.csproj -c Release
```
