# ProAI：狐表系统里的 AI 助手

ProAI 是一个可以嵌进狐表（Foxtable）系统的 AI 聊天面板，面向系统的最终用户。开发者写几行代码把面板放进自己的窗口，用户就能直接问"本月销售额是多少"，或者说"新增一张订单"。

| 能力 | 支持内容 |
| --- | --- |
| 查询统计 | 只在放行的表和列里查明细、做汇总和排名，结果可以在狐表里筛出来 |
| 录入修改 | 新增、修改行（含明细），写入前弹确认卡片；也可以填进现有录入窗口，由用户自己保存 |
| 拍照与导入 | 发图片识别单据，发 Excel / CSV 导入或问答 |
| 语音输入 | 说话转文字，填进输入框 |
| 懂业务 | 读工程目录下的技能库（口径、术语），并带上当前窗口和选中行 |
| 业务动作 | 出库、审核这类业务交给开发者的内部函数，AI 按函数开头的注释调用，执行前请用户确认 |
| 按人放行 | 不同角色看到不同的表、列和窗口；每轮问答都记日志 |

ProAI 只用狐表的公开接口，设计环境和运行环境都能用。它是 Foxtable Pro 的一部分，单独使用也不依赖 Foxtable Pro 的工具箱。

## 运行要求

- 狐表 2026-11-16 或更新的版本（用到官方的 `LLMCaller`）；
- 最终用户的电脑上装有 WebView2 运行时（Win10/11 一般自带，没有时面板会给出下载链接）；
- 一个 OpenAI 兼容的对话接口，如 DeepSeek。

## 快速开始

### 1. 最小用法

从 [Releases](https://github.com/weitieping/foxtable-pro-ai/releases) 下载 `Foxtable.Pro.AI.dll` 和 `Foxtable.Pro.AI.xml`，两个文件放在同一目录，在狐表工程中引用 DLL（XML 是代码提示用的）。也可以[从源码构建](#从源码构建)。Foxtable Pro 的发布包里已经带了这份 DLL，不用另外下载。

一个 `ProAI` 对象就是一段常驻的对话：把它放在全局变量里，再挂到各个窗口上。窗口关了再开，对话还在。

```vbnet
' 全局代码
Public ai As FoxtablePro.ProAI

' AfterOpenProject 事件
ai = New FoxtablePro.ProAI("https://api.deepseek.com/chat/completions", "deepseek-flash", key)
ai.Tables.Allow("订单", "客户", "产品")    ' 只读: 查询、统计

' 窗口 AfterLoad
ai.AttachTo(e.Form.Controls("Panel1"))     ' 放进窗口里的容器
' 或 ai.ShowPopup() 独立小窗; ai.ShowSidebar() 停靠在主窗口右侧
```

对话接口可以填任何 OpenAI 兼容的 `/chat/completions` 地址。模型参数通过 `ai.Caller`（底层是 `LLMCaller`）设置，如 `ai.Caller.temperature = 0.3`。

### 2. 放行数据

没有放行的表，AI 看不到。放行规则可以链式收窄：

```vbnet
ai.Tables.Allow("订单").Hide("成本价").Describe("每张订单一行, 金额含税")   ' .Columns(...) 只放行几列
ai.Tables.Allow("订单").Roles("经理")                                        ' 经理还能看成本价
ai.Tables.AllowWrite("订单", 明细:="订单明细").Required("客户编号", "日期").Protect("订单号")
ai.Forms.Allow("订单录入").Describe("录入销售单: 明细一行一个产品")
ai.SetUser("张三", "销售")     ' 不用狐表登录用户时指定; 默认取 User.Roles
```

- `AllowWrite`：能新增、修改，不能删除。每次写入前，用户先在确认卡片上核对；表事件里的校验照常生效。
- `Forms.Allow`：AI 打开现有的录入窗口并填好内容，用户核对后点原有的保存按钮。这是最稳妥的方式。
- `Required` 声明必填列（狐表的列没有"必填"属性）；`Protect` 声明能看不能改的列。

### 3. 面板与输入

```vbnet
ai.Title = "销售助手"
ai.Welcome = "你好，我可以帮你查询和统计销售数据。"
ai.ThemeColor = "#2563eb"
ai.QuickQuestions.Add("本月销售额是多少？")
ai.AllowImages = True     ' 能发图片 (模型要能看图)
ai.AllowFiles = True      ' 能发 Excel / CSV
ai.SetSpeech("https://api.deepseek.com/v1/tools/asr", "", key)   ' 语音输入, DeepSeek 不用填模型
```

语音输入要配一个语音识别接口，不配就不显示麦克风按钮：

- DeepSeek 的 key 与对话接口的相同，不用填模型。
- 也可以用 OpenAI 兼容的 `/audio/transcriptions` 接口，第二个参数填模型名，如硅基流动的 `FunAudioLLM/SenseVoiceSmall`。

识别出的文字先填进输入框，用户确认后才发送。用户电脑要在"设置 → 隐私和安全性 → 麦克风"里允许桌面应用访问麦克风。

### 4. 让 AI 懂业务

- `ai.Instructions`：追加给 AI 的口径，如"销售额不含退货"。
- 技能库：放在工程目录下的 `ai\`（可用 `ai.SkillsFolder` 改位置），改完不用重开工程。
  - `ai\SKILL.md` 写系统概况、术语和口径，每次都会给 AI。
  - 按模块另写 `ai\<模块>\SKILL.md`，AI 按需读取；用 `roles:` 限定只给部分角色看。
  - 起草技能库时，可以让 AI 客户端加载 [skills/foxtable-proai-skills](skills/foxtable-proai-skills/SKILL.md) 这个技能（它要用到 Foxtable Pro 的 MCP 工具）。
- `ai.IncludeScene`：默认会带上谁在问、当前窗口、当前行或选中行，用户可以直接说"这张单""选中的这些"。

### 5. 业务工具（内部函数）

出库、审核、结算这类业务，不是单纯改表就能完成的，交给开发者已有的内部函数处理。步骤如下：

1. 在函数开头写注释：

   ```vbnet
   ' @AI工具 出库 写入 预览
   ' 按产品出库: 扣减库存并生成出库单。用户说出库、发货时用。
   ' @参数 产品 文本 必填
   ' @参数 数量 整数 必填 最小1
   ' @参数 仓库 文本 默认总仓 总仓|分仓
   Dim p = Args(0)                  ' FoxtablePro.ToolArgs
   If p.Preview Then Return "从 " & p("仓库") & " 出库 " & p("产品") & " " & p("数量") & " 台"
   ' ……扣库存、写出库单; 出错直接 Throw, AI 会把原因告诉用户
   Return "已出库, 单号 CK0001"
   ```

2. 生成工程目录下的 `ai\tools.json`，它要随工程一起发给最终用户。装了 Foxtable Pro 工具箱的，在设计环境里保存项目就会自动生成；没装的，按[下文的格式](#aitoolsjson-格式)手写。
3. 放行：`ai.Functions.Allow("出库")`，还可以接 `.Roles("仓管")` 限定角色。

注释的写法：

- `@AI工具` 后面依次写：显示名（不写就用函数名）、`只读` 或 `写入`、`预览`。
  - 写入的工具执行前弹确认卡片。
  - 标了预览的，先以 `p.Preview = True` 调一次，把返回的文字或表格放在卡片上，用户确认后再正式调用。
- `@参数 名 类型 [必填] [最小N] [最大N] [最长N] [默认X] [a|b|c] [说明]`：类型有文本、整数、数字、日期、是否、JSON。调用前统一检查，函数里不用重复写。

函数里的用法：

- 取值用 `p("产品")`（已经转好类型）；还可以用 `p.Preview`、`p.User`、`p.HasRole("仓管")`。
- 可以返回文字、数字、行、表、行的列表或 Dictionary。
- 已有函数在别处被按位置传参调用的，不要改它，另建一个带注释的函数转调。
- 注释写错时，`Allow` 会直接报错并说明原因。

简单的只读工具不用建函数，可以直接写在代码里，lambda 的参数就是工具参数：

```vbnet
ai.Tools.Add("可用库存", "查一个产品可以发货的数量: 库存减去已锁定。", Function(产品 As String, 仓库 As String) 可用库存(产品, 仓库)).Param("仓库 默认总仓 总仓|分仓")
```

- 参数类型按 `As` 定：`String`、`Integer`、`Long`、`Double`、`Decimal`、`Date`、`Boolean`，`Object` 收 JSON。参数默认必填，写成 `Integer?` 这类就是选填。
- 范围、可选值、默认值和说明用 `.Param` 补充，写法同 `@参数`，不用重复写类型。
- 必须写 lambda，不能用 `AddressOf`；最多 4 个参数。
- 要用 `p.User`、`p.HasRole` 时写成 `Function(p)`，参数全部用 `.Param` 声明，取值用 `p("产品")`。
- 单表的查询统计，放行表就够了，不用写工具。

#### ai\tools.json 格式

文件用 UTF-8 编码。`errors` 可以省略；Foxtable Pro 会把注释写错的函数和原因记在这里，`Allow` 报错时会带上原因。

```json
{
  "tools": [
    {
      "function": "出库函数",
      "title": "出库",
      "description": "按产品出库: 扣减库存并生成出库单。",
      "writes": true,
      "preview": true,
      "params": [
        { "name": "产品", "type": "文本", "required": true },
        { "name": "数量", "type": "整数", "required": true, "min": 1 },
        { "name": "仓库", "type": "文本", "default": "总仓", "options": ["总仓", "分仓"] }
      ]
    }
  ],
  "errors": [
    { "function": "某函数", "error": "原因" }
  ]
}
```

- `function` 是内部函数名；`title` 是显示名，和函数名相同时可以省略。
- 参数还支持 `max`、`maxLength` 和 `description`。

### 6. 代码控制与日志

```vbnet
ai.Ask("本月销售额是多少？")    ' 代用户提问; 正在回答时返回 False
ai.AttachFile("D:\单据.jpg")    ' 代用户附上文件, 下次提问一起发
ai.Cancel() : ai.NewChat() : ai.Detach()
ai.LogFolder = "ailogs"         ' 默认; 每天一个 .jsonl, 设为 "" 不写文件
AddHandler ai.Logged, AddressOf 记AI日志   ' e.User、e.Question、e.Tools、e.Wrote、e.Json
```

其余配置：

- `MaxRows`：明细最多返回多少行，默认 100。
- `MaxContextChars`：上下文大约多少字开始裁剪，默认 60000。

## 从源码构建

需要：

- .NET SDK 或 Visual Studio 2022；
- Node.js：构建时会自动在 `src/web/` 下运行 `npm install` 和 `npm run build`，把面板网页打成单个 HTML 嵌进 DLL；
- 本机装有狐表 2026-11-16 或更新的版本。编译时直接引用狐表目录里的 `foxtable.exe`、`Newtonsoft.Json.dll` 和 WebView2 DLL，这些文件不会复制到输出目录。

先指定狐表的安装目录，用环境变量或 MSBuild 参数都可以：

```powershell
$env:FOXTABLE_DIR = "D:\foxtable"
dotnet build -c Release
# 或
dotnet build -c Release -p:FoxtableDir=D:\foxtable
```

产物在仓库根目录的 `dist\`：`Foxtable.Pro.AI.dll` 和 `Foxtable.Pro.AI.xml`，编译目标是 x86、.NET Framework 4.7.2。只改了 VB 代码的话，可以加 `-p:SkipPanelWeb=true` 跳过网页打包。

用 Visual Studio 打开 `FoxTablePro.AI.sln` 也一样，同样需要先设好 `FOXTABLE_DIR`。

调试面板网页时，可以在 `src/web/` 下运行 `npm run dev`。没有狐表宿主时，网页会改用 `src/web/src/mockHost.ts` 里的模拟数据。

## 目录

| 路径 | 内容 |
| --- | --- |
| `src/` | 源码，项目文件 `FoxTablePro.AI.vbproj` |
| `src/ProAI.vb` | 对外的入口类 `FoxtablePro.ProAI` |
| `src/Tables.vb`、`Forms.vb`、`Functions.vb` | 表、窗口、内部函数的放行规则 |
| `src/Chat/` | 对话、附件、语音、上下文裁剪 |
| `src/Data/` | 查询引擎、筛选条件、表结构 |
| `src/Tools/` | 给模型的工具：读、写、窗口、文件、自定义工具 |
| `src/Knowledge/` | 技能库读取 |
| `src/Panel/` | WebView2 面板宿主 |
| `src/Runtime/` | 按名字绑定狐表对象、调用大模型 |
| `src/web/` | 面板网页（React + Vite） |
| `examples/` | 做好的示例工程，如 `基本示例.Table` |
| `skills/` | 给 AI 客户端用的技能，用来起草技能库 |

## 安全说明

- AI 只能看到放行的表、列和窗口；写入都要经过用户确认。不过技能库和 `Instructions` 会原样发给模型，不要在里面写密钥或个人信息。
- 对话内容和放行的数据会发到你配置的模型接口。请按你们的数据合规要求选择服务商。

## 许可证

[MIT](LICENSE)
