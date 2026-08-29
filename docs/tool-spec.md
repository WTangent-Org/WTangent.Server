# wtangent LLM 工具规范 v1（对齐稿）

> 目标：与业界事实标准**全面对齐**——语义/权限/模式/对话流对齐 Claude Code 系（opencode、kimi-cli、pi 均已向它收敛），命名与形态对齐 pi/opencode（小写、独立描述、每工具系统提示贡献），协议天然兼容 MCP。
> 本文档是 serve 工具层改造与后续 提问/计划模式/subagent/后台bash 实施的规范依据。

## 一、参照系（实查四家源码/文档得出）

| 维度 | 结论 | 依据 |
|---|---|---|
| 工具语义与权限分级 | Claude Code 系为事实标准 | opencode modes(plan/build)、kimi-cli plan/ExitPlanMode、pi 明确只读集，全部同构 |
| 命名风格 | **全小写连写**（bash/read/edit/grep） | pi（bash/read/edit/write/find/grep/ls/powershell）、opencode（bash/edit/write/read/grep/glob/list/patch/todowrite/task）一致；Claude/ZCode 用 PascalCase（语义相同） |
| 工具描述 | 首句功能陈述 + **Tips:** 列表（并行提示/失败行为/边界/替代工具） | kimi-cli 每工具一个 `.md`（如 read.md 14 条 Tips）；pi 有 systemPromptContribution |
| 系统提示 | 基座（身份+环境）+ 可用工具清单 + **每工具 guidelines 贡献** + 项目上下文文件 | pi src/core/system-prompt.ts（逐工具 contribution 组装） |
| 参数规范 | pydantic/typebox 式 schema，字段带 description，可直接映射 MCP inputSchema | kimi-cli（pydantic Field）、pi（typebox） |

## 二、工具矩阵（现名 → 对齐目标）

| 语义 | Claude/ZCode | pi | opencode | kimi-cli | wtangent 现状 | **对齐目标** |
|---|---|---|---|---|---|---|
| 执行命令 | Bash（run_in_background）+ BashOutput + KillShell | bash / powershell | bash | Shell（command/timeout/run_in_background/description） | bash + background 分离 | **bash**（并入 run_in_background/description/timeout）+ **bash_output** + **kill_shell** |
| 读文件 | Read | read | read | ReadFile（line_offset/n_lines，Tips 14 条） | read_file | **read**（参数对齐 offset/limit） |
| 写文件 | Write | write | write | WriteFile | ✗ 无 | **新增 write**（file_path/content，整文件覆盖） |
| 编辑 | Edit（old_string/new_string/replace_all，要求唯一） | edit | edit / patch | StrReplaceFile | edit_file | **edit**（参数对齐 old/new/replace_all） |
| 文件名查找 | Glob | find | glob | Glob | glob ✓ | **glob**（pattern/path） |
| 内容搜索 | Grep（-A/-B/-C/-i/output_mode） | grep | grep | Grep | grep（pattern/path/include/limit） | **grep**（补 context 上下文行 / case_insensitive / output_mode） |
| 列目录 | （bash ls） | ls | list | （glob/shell） | ✗ | 可选 **ls**（低优先） |
| 网页抓取 | WebFetch（url+prompt，AI 摘要） | （扩展） | webfetch | FetchURL | web_fetch ✓ | **webfetch**（补 prompt 参数语义） |
| 网页搜索 | WebSearch | （扩展） | websearch | SearchWeb | web_search ✓ | **websearch** |
| 子代理 | Task（subagent_type/resume） | ✗（刻意不做，扩展自建） | task | Agent（description/prompt/subagent_type/model/resume） | ✗ | **新增 task**（= 计划内 ③subagent） |
| 结构化提问 | AskUserQuestion（question/header/options[label+description]/multiSelect） | ✗ | ✗ | AskUser（同构） | ✗ | **新增 askuser**（= 计划内 ①提问） |
| 计划模式 | EnterPlanMode / ExitPlanMode | ✗（不做） | modes: plan/build | plan + ExitPlanMode（提交计划带备选方案 label/description） | ✗ | **新增 plan 模式**（= 计划内 ②） |
| 待办清单 | TodoWrite（content/status/priority） | ✗ | todowrite / todoread | SetTodoList（title/status: pending/in_progress/done） | ✗ | **新增 todowrite** |
| 显式思考 | （thinking 原生） | ✗ | ✗ | Think（thought） | ✗ | 可选 **think**（低优先） |
| git 提交 | ✗ | ✗ | ✗ | ✗ | git_commit | **保留**（wtangent 差异化） |
| 代码引用搜索 | ✗ | ✗ | ✗ | ✗ | ref_search | **保留**（差异化） |
| MCP | ✗ | 扩展承载 | ✗ | /mcp 管理 | 桥 ✓ | **保留** |

## 三、规范细则

1. **命名**：全小写连写；wtui/WUI 命令面板与工具同名展示。
2. **描述**：每个工具 = 首句功能 + `Tips:` 列表（何时用/何时不用/并行提示/失败行为/参数细节），来源为 C# 常量（`Tools/Descriptions/*.cs`），未来插件层同构外置。
3. **权限分级**（计划模式过滤依据）：
   - 只读：read、grep、glob、ls、webfetch、websearch、think
   - 变更：bash、edit、write、task、kill_shell
   - 中性：askuser、todowrite、bash_output
4. **系统提示组装**（pi 式）：基座（身份/环境/工作区）→ 可用工具清单 → 逐工具 guidelines → 项目上下文（AGENTS.md 优先）。禁止把工具说明散落在巨型字符串里。
5. **对话流两通道**：`confirm_req`（危险命令 y/n，已实现）与 `question_req`（结构化提问，id/question/options[label/description/multiSelect]→answer 回执）。协议事件命名与 WS 信封延续 camelCase。
6. **模式**：chat ↔ plan（会话级 flag；plan 下工具过滤为只读集；出计划→审批门复用 confirm）；`mode_changed` 事件三端徽标显示。

## 四、迁移步骤（并入既定 ①②③④ 计划）

1. 工具改名与参数对齐（read/edit/bash/grep/webfetch/websearch），新增 **write**；bash 并入 run_in_background（background 工具退役或保留别名）
2. **askuser**（①提问）：按 kimi AskUser schema（question/header/options[label/description]/multiSelect），复用 confirm 管道
3. **plan 模式**（②）：权限分级表落地 + 审批门（ExitPlanMode 式带备选方案）
4. **task**（③ subagent）：按 kimi Agent schema（description/prompt/subagent_type/resume），嵌套事件流
5. **todowrite**：kimi SetTodoList 语义（title/status）
6. 系统提示重构：pi 式逐工具 contribution 组装（"甚至提示"的落地）
7. think、ls：低优先，随用随加

## 五、验收

- 每个工具：名称/参数/描述三项与本表一致；提示含 Tips 列表
- 计划模式下 LLM 可见工具集 == 只读集（日志可查）
- 三端（wtui/WUI/TUI 旧）对 confirm/question/mode/task 事件的渲染行为一致
- mock E2E：假 LLM 脚本走通 read→edit→bash→askuser→todowrite 全链
