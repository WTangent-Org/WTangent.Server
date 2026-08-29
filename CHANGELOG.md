# Changelog

## [0.6.1](https://github.com/WTangent-Org/WTangent.Server/compare/v0.6.0...v0.6.1) (2026-08-29)


### ✨ 新功能

* App 静态属性构造注入（PCL-CE 式）：生成器产 static App + ctor，IEntry 移除 App 成员 ([4eb898b](https://github.com/WTangent-Org/WTangent.Server/commit/4eb898bd2828967d6ed6df2cbc5938503eda7413))
* release.ps1 一键发版（触发→审批 PR CI→等合并→等 publish→等 nuget 索引） ([a0d7e20](https://github.com/WTangent-Org/WTangent.Server/commit/a0d7e209b5d512e02e66c4aa68af6ddf0ef53ed1))
* 最终特性集 [AgentEntry(id,name,isAsync)]/[EntryStart]/[EntryStop]/[AgentCommand(parent)]/[AgentTool] ([e1bbc87](https://github.com/WTangent-Org/WTangent.Server/commit/e1bbc8731468656f2feb7c2119f7df85e8b6fab5))
* 构造注入 App（无 null!）+ Current 静态桥（PCL-CE 式）；钩子实例方法，纯业务 ([4c8704e](https://github.com/WTangent-Org/WTangent.Server/commit/4c8704e6577c3a559ee40840c3f4adfa7638af28))


### 🐛 修复

* CI 布局——本仓 checkout 进同名子目录复刻本地工作区布局（ProjectReference 的 ../ 不再越出工作区），构建路径加前缀 ([9cfd4dd](https://github.com/WTangent-Org/WTangent.Server/commit/9cfd4dd6eb67c05259d077627fb7812724377842))
* glob 工具支持跨目录模式（**/*.cs 整串传 searchPattern 在 Windows 直接抛参数错误）——拆目录前缀与文件名模式，** 递归、字面前缀拼扫描根；mock 脚本过时路径更新；删无引用的 Store/Components.cs；确认问询/SCM OnStop 两处同步边界补注释 ([a28a685](https://github.com/WTangent-Org/WTangent.Server/commit/a28a6858e807f65f5f839cd96e95ff3288027558))


### 🧹 其他

* Components 升 0.0.10（Application 移除 Logger/Config，统一走门面） ([c248578](https://github.com/WTangent-Org/WTangent.Server/commit/c2485785adb4a81188354641ff74691555f2d98b))
* Components 升 0.0.11（StartAsync 无参）；R# 全量扫描修复；AGENTS.md 契约节同步 ([426e974](https://github.com/WTangent-Org/WTangent.Server/commit/426e974c89a8c8c996417b0a57877501ab75a865))
* Components 升 0.0.9（ExcludeAssets=runtime）；agent-component.json 改由生成器产出；AGENTS.md 重写为现状 ([96ebad7](https://github.com/WTangent-Org/WTangent.Server/commit/96ebad75f03ee4e758fb685f9bcd208cebeeb339))
* csproj nuget 组补 WTangentDev 条件（dev restore 直拉模式关闭包引用，引用由 wtangent.dev.props 注入） ([c436214](https://github.com/WTangent-Org/WTangent.Server/commit/c4362144fcc293c71a226b98a7b7023a9b889c33))
* csproj 补 CompilerVisibleProperty ComponentDepends（本地模式手动声明，配套生成器 depends） ([c900198](https://github.com/WTangent-Org/WTangent.Server/commit/c9001982ac09ab3e3127fc41f9e23c3e1a900452))
* OnStart 迁移到全局门面 Log.Info ([1cf6b79](https://github.com/WTangent-Org/WTangent.Server/commit/1cf6b79ac6ed959fd54d413cf3f022c0b797d494))
* release-please 统一 always-bump-patch（版本只走 patch） ([9fcc4f7](https://github.com/WTangent-Org/WTangent.Server/commit/9fcc4f7c98c8df9e040933c6b596265171baf972))
* 清残留——删 Store/PluginLoader.cs（旧架构遗留，引用不存在的老程序集名）；Server/Tui csproj 移除无引用的 McMaster.NETCore.Plugins ([e5b3602](https://github.com/WTangent-Org/WTangent.Server/commit/e5b3602e1b63458e90d6a2a7db007a60d343c46f))
* 移除本地工具脚本（build/format/release.ps1 不进仓库） ([557c16a](https://github.com/WTangent-Org/WTangent.Server/commit/557c16a613ab711449422a08572b9dcce11fe46b))

## [0.6.0](https://github.com/WTangent-Org/WTangent.Server/compare/v0.5.0...v0.6.0) (2026-08-22)


### ✨ 新功能

* AgentOptions.Tools 改为必须显式组装（ServerTools.Default 内置 + ToolComponentLoader 组件扩展） ([99c2dc0](https://github.com/WTangent-Org/WTangent.Server/commit/99c2dc0787c72d198cd8174ef98d4054d8701670))
* git 双模式——本地透传 + --server 远程执行（serve 新增 /git-exec 端点） ([8578876](https://github.com/WTangent-Org/WTangent.Server/commit/8578876559ec13f9e5cac3283f1bd39775037821))
* IEntry 元组命令（父路径挂接）+ 三形态（cmd/sub/tool）+ 类型字段废弃 ([ec6e0ac](https://github.com/WTangent-Org/WTangent.Server/commit/ec6e0ac887b435422158082d8260885e682b0948))
* IEntry 手写入口（0.0.3）——类型字段废弃，能力由 Entry 声明（Commands/Default/Tools + StartAsync 生命周期） ([31f72c6](https://github.com/WTangent-Org/WTangent.Server/commit/31f72c6659130eaeb0642c14d62667f1c5dd60d0))
* serve 服务组件（WTangent.Server 命名空间，WTangent.Components 单包） ([40686ba](https://github.com/WTangent-Org/WTangent.Server/commit/40686bac9737c6c0ef9d87b724cc5052bc27953a))
* 组件类型收敛 ui/cmd/tool + client 组件拆分（remote/run/web 归 client；tui 纯 UI；serve type=cmd；官方组件自动安装） ([c47e130](https://github.com/WTangent-Org/WTangent.Server/commit/c47e130e45813cd78a27694fbd451db81fe8f246))


### 🐛 修复

* release.yml 重复 name/on 头部（workflow startup_failure） ([0dc4823](https://github.com/WTangent-Org/WTangent.Server/commit/0dc48236c3689cc0eee520651acb6927764a5982))


### 🧹 其他

* csproj 文件名统一 WTangent.*（workflow/release-please/deps 引用同步） ([34fce1b](https://github.com/WTangent-Org/WTangent.Server/commit/34fce1be494c12d7954b5bef2cd21564ad773f22))
* WTangent.Components 0.0.1→0.0.2（Application 契约） ([3ccebe9](https://github.com/WTangent-Org/WTangent.Server/commit/3ccebe9310385ad22b2d0bc0419ec79ad8b1db10))
* WTangent.Components 引用 0.4.0→0.0.1（对齐发布版本） ([775df6b](https://github.com/WTangent-Org/WTangent.Server/commit/775df6ba3f10f717f61874099bca8391de5e11b9))

## [0.5.0](https://github.com/wtommy932/WtAgent.Server/compare/v0.4.0...v0.5.0) (2026-08-19)


### ✨ 新功能

* HttpClient 统一为 WtAgent.Core.Http（共享单例/New） ([95d6b81](https://github.com/wtommy932/WtAgent.Server/commit/95d6b81ff126ea314ee298decffc6a3dfb415284))
* 命名空间改 WtAgent（前缀统一，包引用 WtAgent.Components） ([3a3cd21](https://github.com/wtommy932/WtAgent.Server/commit/3a3cd216be062bf42612f195d1a274bbc01ce9df))
* 恢复 register/unregister + serve 缺 web 自动下载（agent install web） ([665b651](https://github.com/wtommy932/WtAgent.Server/commit/665b651578613aa274036c8bdd022ab4249046b9))
* 组件入口改 Command 列表（Entry.Commands + Default，空壳注册命令树） ([f545465](https://github.com/wtommy932/WtAgent.Server/commit/f5454659e1fc97e2b3140e17f746d5554b93598e))
* 重组单项目——顶级 serve + Entry 入口 + 组件 zip 发布（framework-dependent） ([9309a7a](https://github.com/wtommy932/WtAgent.Server/commit/9309a7a5a47ba86edee78b2275724016134e3dae))


### 🐛 修复

* csproj 改名 WtAgent.* + 显式 RootNamespace（生成器 Entry 命名空间归位） ([375648f](https://github.com/wtommy932/WtAgent.Server/commit/375648f43d3f772cbc99b8096b96e35d4b60b118))
* 恢复 .editorconfig（dotnet format 误删）+ ModelConfig 命名空间归位 ([b22e826](https://github.com/wtommy932/WtAgent.Server/commit/b22e826f21a5929644c7501b5a4bb1208ae99c4d))


### 🧹 其他

* 清理冗余（旧脚本/旧 sln 设置/TODO）+ publish 改名 workspace + AGENTS/README 更新为新架构 ([f04024b](https://github.com/wtommy932/WtAgent.Server/commit/f04024b30238bf4ea9d93d14cba03854a2597aad))
* 移除残留的 Commands/ModelConfig.cs（已移至 Store） ([1baae92](https://github.com/wtommy932/WtAgent.Server/commit/1baae92cf1bb7dc1095535222068dcbf549a7932))

## [0.4.0](https://github.com/wtommy932/Agent.Server/compare/v0.3.0...v0.4.0) (2026-08-18)


### ✨ 新功能

* serve 的 host/port 改位置参数 + 顶级 agent 启动默认 serve ([6fd69c8](https://github.com/wtommy932/Agent.Server/commit/6fd69c84bd4f7f820ba93860b3ac9212a929946d))

## [0.3.0](https://github.com/wtommy932/Agent.Server/compare/v0.2.1...v0.3.0) (2026-08-18)


### ✨ 新功能

* 构建失败禁合并（轮询只等 CLEAN） ([bca01be](https://github.com/wtommy932/Agent.Server/commit/bca01be7743e5a7ab6eee8f1e18b9cdc640ed347))


### 🐛 修复

* 自动合并轮询显式 -R 仓库并暴露错误（诊断 UNKNOWN） ([58a31d2](https://github.com/wtommy932/Agent.Server/commit/58a31d2db37201b652a70a8bc23d501101af6e32))

## [0.2.1](https://github.com/wtommy932/Agent.Server/compare/v0.2.0...v0.2.1) (2026-08-18)


### 🧹 其他

* install 脚本移至空壳仓（Agent）分发 ([3261cc0](https://github.com/wtommy932/Agent.Server/commit/3261cc0cfe63c933f7ba0a86a111e7daa4861e1c))

## [0.2.0](https://github.com/wtommy932/Agent.Server/compare/v0.1.0...v0.2.0) (2026-08-18)


### ✨ 新功能

* Basic.IsDebug 全局开关 + Tui 独立版本号（准备拆多仓） ([7fd76bb](https://github.com/wtommy932/Agent.Server/commit/7fd76bb0517d98ef687a426bbe70f67e8cba6618))
* serve-only 架构（serve/remote/git/tui/web 组件化） ([3d2aca5](https://github.com/wtommy932/Agent.Server/commit/3d2aca5bc2bfad3ec83613acd90fc563f790e0de))
* 一键安装闭环（release.ps1 三平台+组件+install 脚本）+ push 后自动优化（config auto_optimize 开关 + post-receive hook + /optimize + WUI 设置） ([b2a2c45](https://github.com/wtommy932/Agent.Server/commit/b2a2c458822ff13790629bf91fac593863b4b9cf))
* 安装发布体系——install 版本化+频道（stable/beta）+ upgrade 自体更新 + 七平台矩阵 + release-please bot 自动发布 ([8a314a4](https://github.com/wtommy932/Agent.Server/commit/8a314a4684d7d70ed85c94e7027edae940d0edc7))
* 提交全自动交给 agent（?msg= 定 type，chore 兜底；删 -CliOnly） ([9a6e764](https://github.com/wtommy932/Agent.Server/commit/9a6e7649c89dc465747b168ee1a0edcde08dce72))
* 模仿 dsh——工具（web_search/web_fetch/git_commit/bash cwd）+ LLM usage 缓存命中统计 + token 计量 + 会话持久化（SQLite 续聊）+ WUI 输出栏 ([a17d267](https://github.com/wtommy932/Agent.Server/commit/a17d267f2d4ea5ef1f856fb2b2611c2290305eb3))


### 🐛 修复

* release-type 改 simple（release-please 不支持 dotnet） ([a6e70f0](https://github.com/wtommy932/Agent.Server/commit/a6e70f0d5c0aac111c37b113a5ce5f72cb32c055))
* 自动合并 release PR 用 PAT（RELEASE_TOKEN） ([604b890](https://github.com/wtommy932/Agent.Server/commit/604b89062bb38990c8470d1c33d88d8ee6edacee))
* 自动合并条件判断空值（合并触发的再次运行 prs 为空字符串） ([3520481](https://github.com/wtommy932/Agent.Server/commit/3520481a3a3f139b8efd75aa919ecbbc9c0aeaae))
* 自动合并轮询加错误容忍 + 10 分钟等待 ([7d41a73](https://github.com/wtommy932/Agent.Server/commit/7d41a73c336abc16b2b5167752f64f07a8b45307))


### 🔧 改进

* Rider 检查清理 + 服务注册/远程命令整理 ([be5ef09](https://github.com/wtommy932/Agent.Server/commit/be5ef09a203630c158d7618bab192e8cab9f9c70))
* 产物名改 agent-server（空壳下载匹配） ([94869da](https://github.com/wtommy932/Agent.Server/commit/94869dab82f067c9224cb5ebe0e49e8e92b81c47))


### 🧹 其他

* Cli PackageId Agent.Cli ([a60d27f](https://github.com/wtommy932/Agent.Server/commit/a60d27fcbc2382ad1b4808121b209d25951531d2))
* gitignore 加 Rider .idea 目录 ([642ac46](https://github.com/wtommy932/Agent.Server/commit/642ac46886caa8442d2482801126942e1729e6a9))
* install 脚本指向真实 release 源（wtommy932/agent） ([36c1057](https://github.com/wtommy932/Agent.Server/commit/36c10570b9f27a98bfd57deeaab4cfcca16328a8))
* **main:** release 0.2.0 ([#2](https://github.com/wtommy932/Agent.Server/issues/2)) ([02b4f5c](https://github.com/wtommy932/Agent.Server/commit/02b4f5cd9034e00a1418c5a51b51dced5373a5aa))
* **main:** release 0.2.1 ([#3](https://github.com/wtommy932/Agent.Server/issues/3)) ([2eb5a03](https://github.com/wtommy932/Agent.Server/commit/2eb5a03e6594cf9542df001ed27cafcad8f33927))
* slnx 去掉 Tui（独立仓库） ([5b1aa41](https://github.com/wtommy932/Agent.Server/commit/5b1aa418f75dc720da2497852ef4aed5a365623e))
* 恢复拆分前源码（重建四仓准备） ([6d4993d](https://github.com/wtommy932/Agent.Server/commit/6d4993d462666f668895c0bfc0f65209c3e8efc3))
* 换官方 Dotnet.gitignore（github/gitignore） ([88f707a](https://github.com/wtommy932/Agent.Server/commit/88f707ad3b4f3920460a90e64a6ad80abfb8cdb6))
* 移除 Core submodule 条目（重建双仓） ([e9a41c9](https://github.com/wtommy932/Agent.Server/commit/e9a41c960d6f0893aeaf7e48aa18a967d94ae423))
* 移除 Core submodule（准备双仓重建） ([2cffa2b](https://github.com/wtommy932/Agent.Server/commit/2cffa2bd0dec8964c52f17ec5f3d6084d149b7ac))

## [0.2.1](https://github.com/wtommy932/agent/compare/v0.2.0...v0.2.1) (2026-08-17)


### 🐛 修复

* 自动合并条件判断空值（合并触发的再次运行 prs 为空字符串） ([3520481](https://github.com/wtommy932/agent/commit/3520481a3a3f139b8efd75aa919ecbbc9c0aeaae))

## [0.2.0](https://github.com/wtommy932/agent/compare/v0.1.0...v0.2.0) (2026-08-17)


### ✨ 新功能

* 一键安装闭环（release.ps1 三平台+组件+install 脚本）+ push 后自动优化（config auto_optimize 开关 + post-receive hook + /optimize + WUI 设置） ([b2a2c45](https://github.com/wtommy932/agent/commit/b2a2c458822ff13790629bf91fac593863b4b9cf))
* 安装发布体系——install 版本化+频道（stable/beta）+ upgrade 自体更新 + 七平台矩阵 + release-please bot 自动发布 ([8a314a4](https://github.com/wtommy932/agent/commit/8a314a4684d7d70ed85c94e7027edae940d0edc7))
* 模仿 dsh——工具（web_search/web_fetch/git_commit/bash cwd）+ LLM usage 缓存命中统计 + token 计量 + 会话持久化（SQLite 续聊）+ WUI 输出栏 ([a17d267](https://github.com/wtommy932/agent/commit/a17d267f2d4ea5ef1f856fb2b2611c2290305eb3))


### 🔧 改进

* Rider 检查清理 + 服务注册/远程命令整理 ([be5ef09](https://github.com/wtommy932/agent/commit/be5ef09a203630c158d7618bab192e8cab9f9c70))


### 🧹 其他

* gitignore 加 Rider .idea 目录 ([642ac46](https://github.com/wtommy932/agent/commit/642ac46886caa8442d2482801126942e1729e6a9))
* release PR 自动 squash 合并（全自动发布闭环） ([dfba4c6](https://github.com/wtommy932/agent/commit/dfba4c603bd6013e1f7e4583181d8bc49138df85))
* 自动合并 release PR 加轮询等待（PR 可合并后再 squash） ([d0a2832](https://github.com/wtommy932/agent/commit/d0a2832f415bf667b6b372010858ab7f23206368))
