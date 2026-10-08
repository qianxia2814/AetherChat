# 发布到 GitHub 第三方插件库

第三方库由三个部分组成：公开的源码仓库、GitHub Release 中的安装包，以及仓库根目录的 `repo.json` 索引。仅上传源码不会自动成为可安装的插件库。

## 准备

1. 使用自己的 GitHub 账号或有写入权限的组织，新建公开仓库。建议名称 `AetherChat`，默认分支 `main`。
2. 决定源码许可证后添加 LICENSE；在决定之前不要宣称已按某种开源许可发布。
3. 将本目录的源码、说明、构建脚本与 `.gitignore` 上传。不要上传 `.build-home`、`bin`、`obj`、用户配置或 Dalamud 自身程序集。构建安装包通过 Releases 分发。

## 生成发布材料

使用 PowerShell 7，在源码目录运行：

```powershell
.\build.ps1
.\prepare-release.ps1 -Repository '你的GitHub账号/AetherChat'
```

发布脚本会验证源代码版本、插件 DLL、核心 DLL 和清单一致，生成到 `dist/publish/0.1.1`：

- `AetherChat-0.1.1.zip`：用户安装包，文件位于 ZIP 根目录。
- `repo.json`：真正的插件库索引，包含固定版本的安装与更新链接。
- `SHA256SUMS.txt`：安装包校验值。
- `RELEASE-NOTES.md`：可直接用于 GitHub Release 的中文说明。

## 发布顺序

1. 将源码提交并推送到目标仓库。
2. 创建 `v0.1.1` 的 GitHub Release，上传插件 ZIP 和 SHA256SUMS.txt；发布说明使用生成的 RELEASE-NOTES.md。
3. 先确认安装包可匿名下载，再把生成的 `repo.json` 提交到 `main` 分支根目录。这样用户不会提前看到指向不存在安装包的索引。
4. 分享下面形式的地址（替换成实际账号和仓库）：

```text
https://raw.githubusercontent.com/你的GitHub账号/AetherChat/main/repo.json
```

5. 用不带 GitHub 登录信息的 HTTP 请求检查索引和下载链接，核对下载 ZIP、清单和 DLL 的版本及校验值。最后在国服 Dalamud 中按第三方库方式安装一次，验证列表展示和安装。

## 后续更新

增加 `Directory.Build.props` 的版本，并同步 `AetherChat.Plugin/AetherChat.json` 的四段程序集版本；重新构建与测试，使用新 tag 发布新的安装包，再更新 `repo.json`。不要用不同内容覆盖已经发布的版本号。

用户会通过相同插件库地址获得新版本；是否自动安装更新取决于他们的 Dalamud 设置。无需每次重新添加地址。

## 当前状态

本机 0.1.1 已通过 29 项测试，用户已验证说话频道与副本外跨服小队。正式仓库为 [qianxia2814/AetherChat](https://github.com/qianxia2814/AetherChat)，安装方式见 README。通过第三方库在游戏内完成安装仍需另行验收；源码许可证尚未选定。

格式依据：[Dalamud 官方第三方库说明](https://dalamud.dev/plugin-publishing/custom-repositories/)。库地址和安装包必须支持匿名读取，因此采用公开仓库与公开 Release。
