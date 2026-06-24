---
name: release-flow
description: AirPlayer 项目的发布流程。当用户说「发布 vX.X.X」「准备发布」「打 tag 发布」「构建安装包/绿色包并发布」时使用。完成版本号同步、文档更新、绿色包+安装包构建、打 tag 推送一整套流程。
---

# AirPlayer 发布流程

执行 AirPlayer 项目从「改完代码」到「可发布」的完整发版流程。适用于用户说「发布 vX.X.X」「准备发布」「打 tag 发布」「构建安装包和绿色包」等场景。

## 关键约束（必须遵守）

- 版本号遵循 SemVer：`主.次.修订`（如 `1.1.1`）。
- 提交信息用 Conventional Commits + **简体中文**，如 `chore: 版本升至 v1.1.1，更新 CHANGELOG/README/发布说明`。
- **禁止**用版本号做提交前缀（如 `v1.1.1: ...`）。
- 打 tag 用附注标签：`git tag -a vX.X.X -m "AirPlayer vX.X.X"`。
- 推送用 `git push origin main --tags`。
- **本流程全段无需询问用户确认**：从版本号同步到打 tag 推送一气呵成，直接执行；仅当构建失败或遇歧义时才暂停。

## 流程步骤

### 0. 确认版本号与变更内容
- 从用户消息或上下文确定目标版本号（如 `1.1.1`）。
- 运行 `git log v<上一版本>..HEAD --oneline` 列出待发布提交，归纳为本版变更（新增/优化/修复/变更分类）。
- 若用户没给版本号，根据变更类型建议：仅修复 → 修订号+1；有新功能 → 次版本+1。

### 1. 同步版本号到构建文件
- `AirPlayer.App/AirPlayer.App.csproj`：`<Version>`、`<AssemblyVersion>`、`<FileVersion>` 三处。
  - `<Version>1.1.1</Version>`
  - `<AssemblyVersion>1.1.1.0</AssemblyVersion>`
  - `<FileVersion>1.1.1.0</FileVersion>`
- `tools/installer.iss`：`#define MyAppVersion "1.1.1"`。

### 2. 更新 README.md
- 版本徽章：`https://img.shields.io/badge/Version-1.1.1-brightgreen?style=for-the-badge`
- 构建脚本示例版本号：`tools\build-release.ps1 -Version 1.1.1` 和 `publish\AirPlayer-1.1.1-win-x64\`（共 2 处）。

### 3. 更新 CHANGELOG.md
- 在顶部（紧跟标题说明区）新增 `## [X.X.X] - <YYYY-MM-DD>` 条目。
- 按分类列变更：`### 新增` / `### 变更` / `### 优化` / `### 修复`（按实际有内容写，无则省略该分类）。
- 文件底部补链接行：`[X.X.X]: https://github.com/joyjoyfresh/Airplayer/releases/tag/vX.X.X`（插在已有链接行最上方）。
- 日期取当天（用 currentDate，不要用 Date.now()）。

### 4. 新建发布说明
- 新建 `docs/RELEASE_NOTES vX.X.X.md`，参照同目录已有 `RELEASE_NOTES_v*.md` 的格式。
- 必含：本版更新（对应 CHANGELOG 条目）、系统要求、下载与安装（文件名带新版本号）、使用、说明、致谢。

### 5. 提交版本号与文档
```
git add AirPlayer.App/AirPlayer.App.csproj tools/installer.iss README.md CHANGELOG.md docs/RELEASE_NOTES_vX.X.X.md
git commit -m "chore: 版本升至 vX.X.X，更新 CHANGELOG/README/发布说明"
```

### 6. 构建绿色包
```
powershell.exe -ExecutionPolicy Bypass -File "tools/build-release.ps1" -Version X.X.X
```
- 注意：本仓库 CLAUDE.md 提到的 `rtk` 在此环境不可用，直接用原命令。
- 成功产出 `publish/AirPlayer-X.X.X-win-x64/`（目录）和 `publish/AirPlayer-X.X.X-win-x64.zip`。
- 构建输出可能很大，用 `tail` 截取末尾确认 Done 即可。

### 7. 构建安装包
```
"C:/Program Files (x86)/Inno Setup 6/iscc.exe" "tools/installer.iss"
```
- 成功产出 `publish/AirPlayer-X.X.X-setup.exe`。
- 用 `tail -3` 确认 `Successful compile`。

### 8. 汇报产物与版本变更
列出两个产物文件名 + 大小，以及本版变更摘要。

### 9. 打 tag 并推送（直接执行，无需确认）
版本号与文档提交、两个包构建成功后，直接打 tag 推送，不要停下来询问用户：
```
git tag -a vX.X.X -m "AirPlayer vX.X.X"
git push origin main --tags
```
然后告知用户去 GitHub `joyjoyfresh/Airplayer` → Releases → Draft a new release：
- 选 tag `vX.X.X`
- 标题 `AirPlayer vX.X.X`
- 上传 `AirPlayer-X.X.X-win-x64.zip` 和 `AirPlayer-X.X.X-setup.exe`
- 描述从 `docs/RELEASE_NOTES_vX.X.X.md` 复制

## 注意事项

- 若构建失败：先看错误，常见是 `.NET 8 SDK` 或 `Windows App SDK workload` 缺失；版本号未传 `-p:Version` 导致 csproj 与 tag 不一致。
- Inno Setup 路径固定 `C:/Program Files (x86)/Inno Setup 6/iscc.exe`，若不在该路径需先 `where iscc` 探测。
- Step 1–8（版本号同步、文档、构建、提交）+ Step 9（打 tag 推送）一气呵成，全程不停下询问；仅当构建失败或遇到歧义时才暂停。
