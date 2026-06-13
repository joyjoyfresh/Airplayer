# Git 版本管理规范与使用指南（AirPlayer 项目）

本仓库使用严格的 Git 工作流进行分支管理和版本发布，以确保提交历史树的清晰、可追溯以及自动化发布的流畅度。

---

## 1. 分支策略 (Branching Strategy)

- **`develop` 分支 (日常开发)**
  - **定位**：核心开发分支，代码始终保持最新的开发状态。
  - **规则**：所有的新增功能 (`feat`)、缺陷修复 (`fix`)、代码重构 (`refactor`) 等开发工作，必须**在 `develop` 分支上进行提交**。
  - **禁止**：严禁在此分支上直接发布版本标签。

- **`main` 分支 (发布/稳定)**
  - **定位**：生产/稳定版本分支，仅包含经过完整验证的 Release 版本代码。
  - **规则**：**严禁直接在 `main` 分支上进行日常提交**。必须从 `develop` 分支通过 Merge 合并代码进入 `main`。

---

## 2. 合并规范 (Merge Guidelines)

- **强制非快进合并 (`--no-ff`)**
  - 在将 `develop` 合并到 `main` 分支时，**必须使用 `--no-ff` (No Fast-Forward) 参数**：
    ```bash
    git switch main
    git merge --no-ff develop
    ```
  - **目的**：强制 Git 产生一个显式的 Merge 提交，从而在提交历史树中保留完整的开发弧线（分支图上的泡泡拓扑结构），不会将 `develop` 的零碎提交平铺压缩成单条主线。

---

## 3. 提交信息规范 (Commit Message Conventions)

### 3.1 `develop` 分支上的开发提交
所有在 `develop` 分支上的日常提交都必须严格遵循 **Conventional Commits (约定式提交)** 规范，且使用**简体中文**编写描述。
- **格式**：`<type>: <简要中文描述>`
- **常见类型 (`<type>`)**：
  - `feat`：新增功能 (Feature)
  - `fix`：修复 Bug (Bug Fix)
  - `refactor`：代码重构，不涉及功能修改
  - `chore`：杂务，如构建过程、依赖项更新、配置清理等
  - `docs`：文档更新
  - `style`：代码格式调整，不影响运行逻辑
- **示例**：
  - `feat: 更多设置新增视频播放帧率选择限制功能（支持 60fps 与 30fps 切换）`
  - `refactor: 调整更多设置中各配置项的排列顺序`
  - `fix: 修复投屏无声音问题`

### 3.2 合并到 `main` 时的 Merge 提交
当执行合并操作时，生成的合并提交消息应作为发布版本描述，采用版本号作为前缀。
- **格式**：`v{Version}: <版本更新摘要>`
- **示例**：
  - `v0.4.1: 新增全屏铺满与视频分辨率限制，优化窗口标题及菜单项显示顺序`
  - `v0.4.0: 移除 MP4 录屏功能，优化音频设备选择与窗口行为`

---

## 4. 日常发布工作流 (Release Workflow)

### 第一步：在 `develop` 上完成开发
```bash
git switch develop
# ...修改代码...
git status -s               # 查看文件改动状态
git add -A                  # 暂存所有改动
git commit -m "feat: 新增某某功能"
git push origin develop     # 推送到远程 develop 分支
```

### 第二步：将 `develop` 合并至 `main` (发版)
```bash
git switch main
git pull origin main        # 确保本地 main 为最新
git merge --no-ff develop -m "v0.4.2: 新增某某功能并优化系统表现"
```

### 第三步：为版本打标签 (Tag)
```bash
# 打上带附注的版本标签
git tag -a v0.4.2 -m "v0.4.2 预发布"
```

### 第四步：推送到远程仓库
```bash
git push origin main        # 推送合并后的 main 分支
git push origin --tags      # 推送所有新标签至 GitHub
```

### 第五步：返回开发分支
```bash
git switch develop          # 回到 develop 分支继续后续功能研发
```

---

## 5. 常用 Git 维护命令速查

### 5.1 查看状态与历史
```bash
git status                           # 查看当前分支及文件修改情况
git log --oneline -n 10              # 查看最近 10 条提交历史
git log --oneline --graph --all      # 图形化查看所有分支及合并走势
git diff                             # 查看工作区未暂存的修改
```

### 5.2 撤销与恢复
```bash
git restore <file>                   # 丢弃工作区中某文件的修改
git restore --staged <file>          # 将文件从暂存区撤出（保留修改内容）
git reset --soft HEAD~1              # 撤销最近一次提交，将修改保留在暂存区
git revert <commit-id>               # 安全回滚：通过新增提交来撤销指定提交的改动
```

### 5.3 暂存修改 (Stash)
```bash
git stash                            # 暂存当前未提交的修改，使工作区保持干净
git stash pop                        # 恢复并删除最近一次暂存的修改
```
