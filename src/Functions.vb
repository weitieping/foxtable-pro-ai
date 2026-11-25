Imports System.Globalization
Imports FoxtablePro.AI

''' AI 能调哪些内部函数 (第 4 档)。用于不是单纯改表的业务动作, 如出库、审核、结算。
''' 函数开头写注释, 设计环境里保存项目时自动生成 ai\tools.json:
''' <code>
''' ' @AI工具 出库 写入 预览
''' ' 按产品出库, 扣减库存并生成出库单。
''' ' @参数 产品 文本 必填
''' ' @参数 数量 整数 必填 最小1
''' Dim p = Args(0)              ' FoxtablePro.ToolArgs: p("产品")、p.Preview
''' If p.Preview Then Return "将从总仓出库 " &amp; p("产品") &amp; " " &amp; p("数量") &amp; " 台"
''' ' ……写入; 出错直接 Throw, AI 会看到原因
''' </code>
''' 然后 ai.Functions.Allow("出库")。调用前按 @参数 检查必填、类型、范围; 写入的先请用户确认, 标了预览的先给用户看预览。
Public NotInheritable Class AIFunctions

    Private ReadOnly _rules As New List(Of AIFunctionRule)

    Friend Sub New()
    End Sub

    ''' 工具定义文件, 默认工程目录下的 ai\tools.json (设计环境保存项目时生成)。相对路径按工程目录算。
    Public Property DefinitionFile As String = "ai\tools.json"

    ''' 放行这些内部函数。函数开头要有 @AI工具 注释, 且 ai\tools.json 里已有它的定义, 否则报错说明原因。
    ''' 返回的规则可以再加说明和角色, 如 .Describe("只在月底用").Roles("仓管")。
    Public Function Allow(ParamArray functions() As String) As AIFunctionRule
        If functions Is Nothing OrElse functions.Length = 0 Then Throw New ArgumentException("至少给一个函数名。")
        Dim catalog = FunctionCatalog.Load(DefinitionPath())
        Dim rule As New AIFunctionRule()
        For Each name In functions
            Dim spec = catalog.Find(name)
            If spec Is Nothing Then Throw New ArgumentException(catalog.WhyMissing(name))
            FunctionRunner.CheckExists(spec.FunctionName)
            If Not rule.Functions.Contains(spec.FunctionName, StringComparer.OrdinalIgnoreCase) Then rule.Functions.Add(spec.FunctionName)
        Next
        _rules.Add(rule)
        Return rule
    End Function

    ''' 收回某些函数的放行。
    Public Sub Remove(ParamArray functions() As String)
        For Each name In functions
            For Each r In _rules
                r.Functions.RemoveAll(Function(f) String.Equals(f, name, StringComparison.OrdinalIgnoreCase))
            Next
        Next
        _rules.RemoveAll(Function(r) r.Functions.Count = 0)
    End Sub

    ''' 放行过的函数名 (不分角色)。
    Public ReadOnly Property Names As String()
        Get
            Return _rules.SelectMany(Function(r) r.Functions).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        End Get
    End Property

    Friend Function DefinitionPath() As String
        Dim f = If(DefinitionFile, "").Trim()
        If f = "" Then Throw New InvalidOperationException("DefinitionFile 不能为空。")
        If IO.Path.IsPathRooted(f) Then Return f
        Return IO.Path.Combine(CStr(Fx.Global("ProjectPath")), f)
    End Function

    ''' 这个用户能调的函数工具。定义每轮重读 (文件改了就生效), 已不在定义里的跳过。
    Friend Function VisibleTo(who As Who) As List(Of CustomTool)
        Dim res As New List(Of CustomTool)
        If _rules.Count = 0 Then Return res
        Dim catalog As FunctionCatalog
        Try
            catalog = FunctionCatalog.Load(DefinitionPath())
        Catch
            Return res
        End Try
        For Each r In _rules
            If Not r.AppliesTo(who) Then Continue For
            For Each f In r.Functions
                Dim spec = catalog.Find(f)
                If spec Is Nothing Then Continue For
                Dim cur = res.FirstOrDefault(Function(x) String.Equals(x.Spec.FunctionName, spec.FunctionName, StringComparison.OrdinalIgnoreCase))
                If cur Is Nothing Then
                    res.Add(New CustomTool With {.Spec = spec, .ToolName = ToolSpec.ToolNameOf("fn_", spec.FunctionName), .Note = r.Description})
                ElseIf r.Description <> "" AndAlso Not cur.Note.Contains(r.Description) Then
                    cur.Note = (cur.Note & " " & r.Description).Trim()
                End If
            Next
        Next
        Return res
    End Function
End Class

''' 一次 Allow 放行的函数的补充说明和适用角色。
Public NotInheritable Class AIFunctionRule

    Friend ReadOnly Functions As New List(Of String)
    Private _roles As HashSet(Of String)

    Friend Sub New()
    End Sub

    Friend Property Description As String = ""

    ''' 补充给 AI 的说明 (接在注释里的说明后面)。
    Public Function Describe(text As String) As AIFunctionRule
        Description = If(text, "").Trim()
        Return Me
    End Function

    ''' 只对有这些角色之一的用户生效。
    Public Function Roles(ParamArray names() As String) As AIFunctionRule
        Dim list = Who.SplitRoles(If(names, New String() {}))
        If list.Count = 0 Then Throw New ArgumentException("至少给一个角色名。")
        _roles = New HashSet(Of String)(list, StringComparer.OrdinalIgnoreCase)
        Return Me
    End Function

    Friend Function AppliesTo(w As Who) As Boolean
        Return _roles Is Nothing OrElse w.HasAnyRole(_roles)
    End Function
End Class

''' 代码里直接写的只读工具。lambda 的参数就是工具参数 (名字、类型照用, 写成 Integer? 这类为选填):
''' <code>
''' ai.Tools.Add("查库存", "查产品的当前库存。", Function(产品 As String) DataTables("产品").Find("名称 = '" &amp; 产品 &amp; "'")("库存"))
''' </code>
''' 返回值可以是文字、数字、行、表、行的列表或 Dictionary, 交给 AI 并在面板上显示。
Public NotInheritable Class AITools

    Private ReadOnly _items As New List(Of AIToolRule)

    Friend Sub New()
    End Sub

    ''' 加一个只读工具 (同名的替换掉)。name: 显示名, 如 "查库存"; description: 做什么、什么时候用;
    ''' handler: 收到 ToolArgs, 返回结果, 在界面线程上执行, 出错直接 Throw。参数用返回值的 .Param 声明。
    ''' 不用 p.User、p.HasRole 时, 直接用带类型参数的 lambda 更简单, 见下面几个重载。
    Public Function Add(name As String, description As String, handler As Func(Of ToolArgs, Object)) As AIToolRule
        If handler Is Nothing Then Throw New ArgumentNullException(NameOf(handler))
        Return AddCore(name, description, handler, Nothing)
    End Function

    ''' 不要参数的只读工具, 如 Function() DataTables("订单").Compute("Sum(金额)")。
    Public Function Add(name As String, description As String, handler As Func(Of Object)) As AIToolRule
        If handler Is Nothing Then Throw New ArgumentNullException(NameOf(handler))
        Return AddCore(name, description, Function(a) handler(), New List(Of ParamSpec))
    End Function

    ''' 一个参数的只读工具, 如 Function(产品 As String) ...。参数名、类型取自 lambda; 范围、可选值、默认值、说明用 .Param 补充。
    Public Function Add(Of T1)(name As String, description As String, handler As Func(Of T1, Object)) As AIToolRule
        If handler Is Nothing Then Throw New ArgumentNullException(NameOf(handler))
        Dim ps = ParamsOf(handler)
        Return AddCore(name, description, Function(a) handler(Arg(Of T1)(a, ps(0))), ps)
    End Function

    Public Function Add(Of T1, T2)(name As String, description As String, handler As Func(Of T1, T2, Object)) As AIToolRule
        If handler Is Nothing Then Throw New ArgumentNullException(NameOf(handler))
        Dim ps = ParamsOf(handler)
        Return AddCore(name, description, Function(a) handler(Arg(Of T1)(a, ps(0)), Arg(Of T2)(a, ps(1))), ps)
    End Function

    Public Function Add(Of T1, T2, T3)(name As String, description As String, handler As Func(Of T1, T2, T3, Object)) As AIToolRule
        If handler Is Nothing Then Throw New ArgumentNullException(NameOf(handler))
        Dim ps = ParamsOf(handler)
        Return AddCore(name, description, Function(a) handler(Arg(Of T1)(a, ps(0)), Arg(Of T2)(a, ps(1)), Arg(Of T3)(a, ps(2))), ps)
    End Function

    Public Function Add(Of T1, T2, T3, T4)(name As String, description As String, handler As Func(Of T1, T2, T3, T4, Object)) As AIToolRule
        If handler Is Nothing Then Throw New ArgumentNullException(NameOf(handler))
        Dim ps = ParamsOf(handler)
        Return AddCore(name, description, Function(a) handler(Arg(Of T1)(a, ps(0)), Arg(Of T2)(a, ps(1)), Arg(Of T3)(a, ps(2)), Arg(Of T4)(a, ps(3))), ps)
    End Function

    ''' typed: lambda 推断出的参数; Nothing 表示 ToolArgs 写法, 参数全靠 .Param。
    Private Function AddCore(name As String, description As String, handler As Func(Of ToolArgs, Object), typed As List(Of ParamSpec)) As AIToolRule
        If String.IsNullOrWhiteSpace(name) Then Throw New ArgumentException("工具名不能为空。")
        If String.IsNullOrWhiteSpace(description) Then Throw New ArgumentException("要写说明: 做什么、什么时候用, AI 靠它决定调不调。")
        Remove(name)
        Dim spec As New ToolSpec With {.FunctionName = name.Trim(), .Title = name.Trim(), .Description = description.Trim()}
        If typed IsNot Nothing Then spec.Params.AddRange(typed)
        Dim rule As New AIToolRule(spec, handler, typed IsNot Nothing)
        _items.Add(rule)
        Return rule
    End Function

    ''' lambda 的参数 → 参数说明: 名字照用; 类型按 VB 类型; 可空类型 (Integer? 等) 为选填, 其余必填。
    Private Shared Function ParamsOf(handler As [Delegate]) As List(Of ParamSpec)
        Dim res As New List(Of ParamSpec)
        For Each pi In handler.Method.GetParameters()
            Dim n = If(pi.Name, "")
            If n = "" OrElse n.StartsWith("$", StringComparison.Ordinal) Then
                Throw New ArgumentException("读不到参数名, 请直接写 lambda, 如 Function(产品 As String) ...; 不支持 AddressOf。")
            End If
            Dim t = Nullable.GetUnderlyingType(pi.ParameterType)
            Dim k = KindOfType(If(t, pi.ParameterType))
            If Not k.HasValue Then
                Throw New ArgumentException("参数 " & n & " 的类型 " & pi.ParameterType.Name & " 不支持, 可用 String、Integer、Long、Double、Decimal、Date、Boolean (加 ? 为选填), 或 Object (JSON)。")
            End If
            res.Add(New ParamSpec With {.Name = n, .Kind = k.Value, .Required = t Is Nothing})
        Next
        Return res
    End Function

    Private Shared Function KindOfType(t As Type) As ParamKind?
        If t Is GetType(Object) Then Return ParamKind.Json
        Select Case Type.GetTypeCode(t)
            Case TypeCode.String : Return ParamKind.Text
            Case TypeCode.Byte, TypeCode.SByte, TypeCode.Int16, TypeCode.UInt16, TypeCode.Int32, TypeCode.UInt32, TypeCode.Int64 : Return ParamKind.Integer
            Case TypeCode.Single, TypeCode.Double, TypeCode.Decimal : Return ParamKind.Number
            Case TypeCode.DateTime : Return ParamKind.Date
            Case TypeCode.Boolean : Return ParamKind.Bool
        End Select
        Return Nothing
    End Function

    ''' 已检查过的参数值转成 lambda 要的类型; 没给 (选填) 时为 Nothing。
    Private Shared Function Arg(Of T)(a As ToolArgs, p As ParamSpec) As T
        Dim v = a(p.Name)
        If v Is Nothing Then Return Nothing
        Dim target = If(Nullable.GetUnderlyingType(GetType(T)), GetType(T))
        If target IsNot GetType(Object) AndAlso Not target.IsInstanceOfType(v) Then
            Try
                v = Convert.ChangeType(v, target, CultureInfo.InvariantCulture)
            Catch ex As OverflowException
                Throw New ArgumentException(p.Name & " 超出范围: " & Convert.ToString(v, CultureInfo.InvariantCulture))
            End Try
        End If
        Return DirectCast(v, T)
    End Function

    Public Sub Remove(ParamArray names() As String)
        For Each n In names
            _items.RemoveAll(Function(r) String.Equals(r.Spec.Title, If(n, "").Trim(), StringComparison.OrdinalIgnoreCase))
        Next
    End Sub

    Public ReadOnly Property Names As String()
        Get
            Return _items.Select(Function(r) r.Spec.Title).ToArray()
        End Get
    End Property

    Friend Function VisibleTo(who As Who) As List(Of CustomTool)
        Return _items.Where(Function(r) r.AppliesTo(who)).
            Select(Function(r) New CustomTool With {.Spec = r.Spec, .ToolName = ToolSpec.ToolNameOf("my_", r.Spec.Title), .Handler = r.Handler}).ToList()
    End Function
End Class

''' 代码里加的工具: 参数和适用角色。
Public NotInheritable Class AIToolRule

    Friend ReadOnly Spec As ToolSpec
    Friend ReadOnly Handler As Func(Of ToolArgs, Object)
    Private ReadOnly _typed As Boolean
    Private _roles As HashSet(Of String)

    ''' typed: 参数取自带类型的 lambda, .Param 只能补充, 不能新增。
    Friend Sub New(spec As ToolSpec, handler As Func(Of ToolArgs, Object), typed As Boolean)
        Me.Spec = spec
        Me.Handler = handler
        _typed = typed
    End Sub

    ''' 声明一个参数, 写法同函数注释里的 @参数: "名 [类型] [必填] [最小N] [最大N] [最长N] [默认X] [a|b|c] [说明]"。
    ''' 如 .Param("产品 文本 必填 产品名称").Param("天数 整数 默认30 最小1")
    ''' lambda 带类型参数时只写要补充的, 如 .Param("仓库 默认总仓 总仓|分仓 出库的仓库")。
    Public Function Param(definition As String) As AIToolRule
        Dim p As ParamSpec
        Try
            p = ParamSpec.Parse(definition)
        Catch ex As FormatException
            Throw New ArgumentException(ex.Message)
        End Try
        If _typed Then
            Dim old = Spec.Params.FirstOrDefault(Function(x) String.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase))
            If old Is Nothing Then Throw New ArgumentException("lambda 里没有参数 " & p.Name & "。参数: " & If(Spec.Params.Count = 0, "无", String.Join("、", Spec.Params.Select(Function(x) x.Name))))
            Merge(old, p)
            Return Me
        End If
        Spec.Params.RemoveAll(Function(x) String.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase))
        Spec.Params.Add(p)
        Return Me
    End Function

    ''' 把补充的说明并进 lambda 推断出的参数; 类型以 lambda 为准。
    Private Shared Sub Merge(old As ParamSpec, p As ParamSpec)
        If p.KindGiven AndAlso p.Kind <> old.Kind Then
            Throw New ArgumentException("参数 " & old.Name & " 的类型按 lambda 里写的定, 是" & ParamSpec.KindName(old.Kind) & ", 不用再写类型。")
        End If
        If p.RequiredGiven Then old.Required = p.Required
        If p.Min.HasValue Then old.Min = p.Min
        If p.Max.HasValue Then old.Max = p.Max
        If p.MaxLength.HasValue Then old.MaxLength = p.MaxLength
        If p.Options.Count > 0 Then
            old.Options.Clear()
            old.Options.AddRange(p.Options)
        End If
        If p.Description <> "" Then old.Description = p.Description
        If p.DefaultText IsNot Nothing Then
            old.DefaultText = p.DefaultText
            old.Required = False
        End If
    End Sub

    ''' 只对有这些角色之一的用户生效。
    Public Function Roles(ParamArray names() As String) As AIToolRule
        Dim list = Who.SplitRoles(If(names, New String() {}))
        If list.Count = 0 Then Throw New ArgumentException("至少给一个角色名。")
        _roles = New HashSet(Of String)(list, StringComparer.OrdinalIgnoreCase)
        Return Me
    End Function

    Friend Function AppliesTo(w As Who) As Boolean
        Return _roles Is Nothing OrElse w.HasAnyRole(_roles)
    End Function
End Class

''' AI 调工具时给的参数, 已按参数说明检查并转换好类型 (文本、整数、数字、日期、是否; JSON 为 Dictionary / List)。
''' 内部函数里是 Args(0), 代码里加的工具是 handler 的参数。
Public NotInheritable Class ToolArgs

    Private ReadOnly _values As Dictionary(Of String, Object)
    Private ReadOnly _given As HashSet(Of String)
    Private ReadOnly _roles As List(Of String)

    Friend Sub New(tool As String, values As Dictionary(Of String, Object), given As IEnumerable(Of String), preview As Boolean, user As String, roles As List(Of String))
        Me.Tool = tool
        _values = values
        _given = New HashSet(Of String)(given, StringComparer.OrdinalIgnoreCase)
        Me.Preview = preview
        Me.User = user
        _roles = roles
    End Sub

    ''' 参数值。没给且没有默认值时为 Nothing; 名字不是声明过的参数时报错。
    Default Public ReadOnly Property Item(name As String) As Object
        Get
            Dim v As Object = Nothing
            If _values.TryGetValue(If(name, ""), v) Then Return v
            Throw New ArgumentException("工具 " & Tool & " 没有参数 """ & name & """。参数: " & String.Join("、", _values.Keys))
        End Get
    End Property

    ''' True: 只生成预览给用户看, 不要写数据 (工具标了"预览"时, 确认前先这样调一次)。
    Public ReadOnly Property Preview As Boolean

    ''' 工具的显示名。
    Public ReadOnly Property Tool As String

    ''' 谁在问 (用户名)。
    Public ReadOnly Property User As String

    ''' 当前用户有没有这个角色。
    Public Function HasRole(role As String) As Boolean
        Return _roles IsNot Nothing AndAlso _roles.Contains(If(role, ""), StringComparer.OrdinalIgnoreCase)
    End Function

    ''' AI 给了这个参数 (没给、用的默认值时为 False)。
    Public Function Contains(name As String) As Boolean
        Return _given.Contains(If(name, ""))
    End Function

    ''' 参数名, 按声明顺序。
    Public ReadOnly Property Names As String()
        Get
            Return _values.Keys.ToArray()
        End Get
    End Property

    Public Overrides Function ToString() As String
        Dim o As New JObject()
        For Each kv In _values
            o(kv.Key) = If(kv.Value Is Nothing, JValue.CreateNull(), JToken.FromObject(If(TypeOf kv.Value Is Date, CObj(Values.FormatDate(DirectCast(kv.Value, Date))), kv.Value)))
        Next
        Return o.ToString(Newtonsoft.Json.Formatting.None)
    End Function
End Class
