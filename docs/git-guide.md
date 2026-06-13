# Git 使用指南（AirPlayer 仓库）

按本仓库实际情况整理：`develop` 为开发分支、`main` 为稳定分支、用 `v0.x.0` 打标签、目前没有远程。

---

## 1. 看状态和历史（先看再动手）

```bash
git status                          # 当前改了哪些文件、在哪个分支
git status -s                       # 精简版（M=修改 A=新增 ??=未跟踪）
git log --oneline -10               # 最近 10 条提交（一行一条）
git log --oneline --graph --all     # 图形化看所有分支走向
git diff                            # 看「还没 add」的改动内容
git diff --staged                   # 看「已 add 待提交」的改动
git show <提交号>                   # 看某次提交改了什么
git blame 文件名                    # 看每行是哪次提交、谁改的
```

## 2. 提交改动（add → commit 两步）

```bash
git add 文件名                      # 把某文件的改动放进暂存区
git add AirPlayer.App/              # 添加整个目录
git add -A                          # 添加所有改动（含新增/删除）
git commit -m "说明"                # 提交暂存区的改动
git commit -am "说明"               # 对「已跟踪文件」一步完成 add+commit（不含新文件）
```

> 提交信息写清楚“做了什么”，例如 `v0.3: 控制条收成单按钮菜单 + 修复全屏旋转`。

## 3. 分支（develop / main 模式）

```bash
git branch                          # 看本地分支，*号是当前所在
git switch develop                  # 切换分支（等价 git checkout develop）
git switch -c feature/录屏           # 新建并切到一个功能分支
git merge develop                   # 把 develop 合并进当前分支
git branch -d 分支名                # 删除已合并的分支
```

## 4. 撤销 / 回退（按“后悔程度”从轻到重）

```bash
git restore 文件名                  # 丢弃某文件「还没 add」的改动
git restore --staged 文件           # 把误 add 的文件移出暂存区（改动还在）
git commit --amend                  # 改最近一次提交（改信息或补文件）；会换提交号
git reset --soft HEAD~1             # 撤销最近一次提交，改动留在暂存区
git reset --mixed HEAD~1            # 撤销最近一次提交，改动留在工作区（默认）
git reset --hard HEAD~1             # ⚠️ 彻底丢弃最近一次提交和改动（危险）
git revert <提交号>                 # 安全撤销：生成一个“反向提交”，不改历史
```

## 5. 暂存现场（stash）

```bash
git stash                           # 把当前未提交改动收起来，工作区变干净
git stash pop                       # 把最近收起来的改动恢复回来
git stash list                      # 看收了几份
```

## 6. 标签（发版用）

```bash
git tag                             # 看所有标签
git tag -a v0.3.0 -m "v0.3 预发布"   # 在当前提交打带说明的标签
git tag -d v0.3.0                   # 删标签
```

## 7. 本仓库的日常流程（建议照走）

```bash
# 平时在 develop 上开发
git switch develop
# …改代码…
git add -A
git commit -m "做了啥"

# 发版时：把 develop 合到 main 并打标签
git switch main
git merge develop
git tag -a v0.3.0 -m "v0.3"
git switch develop                  # 回到 develop 继续开发
```

## 8. 以后推到 GitHub（目前没远程）

```bash
git remote -v                                  # 看有没有远程（现在为空）
git remote add origin <GitHub仓库URL>           # 绑定远程
git push -u origin main                         # 首次推送 main 并建立跟踪
git push origin develop                         # 推 develop
git push origin --tags                          # 把标签也推上去
git pull                                        # 拉取远程更新
```

---

## 本仓库注意点

- **二进制不进仓库**：`.gitignore` 已忽略 `AirPlayer.App/native/*.dll`、`bin/`、`obj/`、`*.log`、`publish/`、`*.zip`。原生依赖（如 `fdk-aac.dll`）各机器自行获取。
- **作者身份**：由 `git config user.name` / `user.email` 决定。本仓库应为
  `joyjoyfresh <fu18290401406@gmail.com>`。查看：`git config user.name`；设置：`git config user.name joyjoyfresh`。
- **index.lock / 索引损坏**：若提示 `index.lock exists` 或 `index file corrupt`，多是上次 git 进程没收尾。处理：
  ```bash
  rm -f .git/index.lock
  git status >/dev/null 2>&1 || { rm -f .git/index && git reset; }   # 重建索引，不动工作区文件
  ```
- **批量改提交作者**（如曾用错名字）：在干净工作区下
  ```bash
  git stash    # 若有未提交改动先收起
  FILTER_BRANCH_SQUELCH_WARNING=1 git filter-branch -f --tag-name-filter cat --env-filter '
  if [ "$GIT_AUTHOR_NAME" = "旧名字" ]; then export GIT_AUTHOR_NAME=joyjoyfresh; fi
  if [ "$GIT_COMMITTER_NAME" = "旧名字" ]; then export GIT_COMMITTER_NAME=joyjoyfresh; fi
  ' -- --all
  git stash pop
  ```
- **放弃所有未提交改动**（慎用）：`git restore .`
- **彻底回到干净工作区（含删未跟踪文件，慎用）**：`git clean -fd`（先 `git clean -nd` 预览）。

---

## 速查：每天最常用的 5 条

```bash
git status          # 我改了啥
git add -A          # 全加进暂存
git commit -m "…"   # 提交
git log --oneline   # 看历史
git switch <分支>   # 切分支
```
