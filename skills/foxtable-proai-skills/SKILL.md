---
name: foxtable-proai-skills
description: 给狐表运行期 AI 面板 (ProAI) 起草或更新项目技能库 (工程目录下的 ai\ 目录), 或把内部函数做成 AI 工具 (@AI工具 注释) 时使用。任务提到 ProAI、AI 面板的业务口径、术语、技能库、ai\SKILL.md、@AI工具、ai.Functions 时读本技能。
---

# 起草 ProAI 项目技能库

ProAI 每次提问都会重读工程目录下的 `ai\`：`ai\SKILL.md` 的正文整段给 AI；其余带 `description` 的 `.md` 列成目录，AI 按需读取。改完不用重开工程。

## 步骤

1. **定位**：`execute_code("Return ProjectPath")` 取工程目录。代码里改过 `SkillsFolder` 的以代码为准（`search_code` 搜 `SkillsFolder`）。`ai\` 已存在就在原文上增改。
2. **读项目**（只读，不用问）：
   - `search_code` 搜 `ProAI`、`Tables.Allow`、`SetUser`，弄清放行了哪些表和列、有哪些角色。
   - `read_object("Tables/表名")` 看列；表事件和内部函数里找口径：状态列怎么取值，哪些单算作废、退货，金额怎么算。
   - `execute_code` 抽几行数据，看状态列实际有哪些值。
3. **起草**：
   - `ai\SKILL.md`：系统概况一两段、术语（用户的说法对应到哪个列、哪个值）、口径（如"销售额不含 已退货 = True 的订单"）、红线。不超过 8000 字。
   - `ai\<模块>\SKILL.md`：一个业务模块一份。frontmatter 写 `name`，`description` 写清什么问题该读。只给部分角色看的加 `roles: 经理, 财务`，同目录的文件随之限定。
   - 长资料另放同目录的 `.md`，在正文里用相对路径引用。
4. **交开发者审核**：列出写了哪些文件，每条口径注明出处（哪个事件、函数，或开发者原话）。拿不准的标"待确认"，开发者确认后再定稿。

## 写法

- 只写表结构里读不出的东西：口径、术语、流程、例外。列清单不用抄。
- 口径写成能照着查的条件，表名、列名、取值用真实的。
- 不写密钥和个人信息。技能库随工程发给最终用户。
- 只认 `.md`、`.txt`。

## 把内部函数做成 AI 工具

开发者要让 AI 调某个函数（出库、审核这类业务动作）时：

1. `read_object("Functions/函数名")` 看清它要哪些参数、改哪些数据、哪里会出错。
2. 工具函数从 `Dim p = Args(0)` 取参数（`p("名")`）。原函数被别处按位置传参调用（`search_code` 搜函数名）时不要改它，另建一个函数转调。在开头加注释，`write_code` 写入：
   - `' @AI工具 显示名 只读|写入 [预览]`，下一行写做什么、什么时候用。
   - 每个参数一行 `' @参数 名 类型 [必填] [最小N] [最大N] [默认X] [a|b|c] [说明]`，类型用文本、整数、数字、日期、是否、JSON。
   - 标了预览的，`If p.Preview Then Return 预览文字` 放在写数据之前。
3. 保存项目（`execute_code("Syscmd.Project.Save()")`），确认工程目录下 `ai\tools.json` 里有它、没有进 `errors`。
4. 告诉开发者在启动代码里加 `ai.Functions.Allow("函数名")`。
