''' <summary>
''' 统计各版本、各存档的游戏时长。
''' 数据来源是存档 stats 目录下的统计文件。游玩时长记录在
''' minecraft:play_time(1.17+)/ minecraft:play_one_minute(1.13~1.16)/
''' stat.playOneMinute(1.12 及更早)三个键之一,值单位是 tick(20 tick = 1 秒),
''' 键名里的「分钟」是历史误称。
''' </summary>
Public Module ModPlayTime

#Region "解析统计文件"

    Private Const TicksPerSecond As Long = 20

    ''' <summary>
    ''' 读取一个统计文件里的游玩时长,单位 tick。文件不存在、损坏或格式无法识别均返回 0。
    ''' </summary>
    Public Function PlayTimeFromStatsFile(FilePath As String) As Long
        If Not FileUtils.Exists(FilePath) Then Return 0
        Dim Json As JObject = Nothing
        Try
            Json = FileUtils.ReadAsJson(Of JObject)(FilePath)
        Catch ex As Exception
            Logger.Warn(ex, "读取存档统计文件失败,已跳过:" & FilePath)
            Return 0
        End Try
        If Json Is Nothing Then Return 0

        '格式 A / B:1.13+ 的嵌套结构 {"stats":{"minecraft:custom":{"minecraft:play_time":123}}}
        Dim Custom As JObject = Nothing
        Dim StatsNode = Json.Item("stats")
        If StatsNode IsNot Nothing AndAlso StatsNode.Type = JTokenType.Object Then
            Custom = TryCast(CType(StatsNode, JObject).Item("minecraft:custom"), JObject)
        End If
        If Custom IsNot Nothing Then
            Dim Modern = PlayTimeReadTicks(Custom.Item("minecraft:play_time"))
            If Modern >= 0 Then Return Modern
            Dim Legacy = PlayTimeReadTicks(Custom.Item("minecraft:play_one_minute"))
            If Legacy >= 0 Then Return Legacy
        End If

        '格式 C:1.12 及更早的扁平结构 {"stat.playOneMinute":123}
        Dim Flat = PlayTimeReadTicks(Json.Item("stat.playOneMinute"))
        If Flat >= 0 Then Return Flat

        Return 0
    End Function

    ''' <summary>读一个 JSON 节点里的 tick 数。不是整数节点(缺失、字符串、对象)返回 -1。</summary>
    Private Function PlayTimeReadTicks(Token As JToken) As Long
        If Token Is Nothing Then Return -1
        If Token.Type <> JTokenType.Integer Then Return -1
        Return Token.ToObject(Of Long)()
    End Function

#End Region

#Region "格式化"

    ''' <summary>
    ''' 把 tick 格式化为可读文本。1 小时以上显示「x 小时 y 分钟」,
    ''' 1 分钟以上显示「x 分钟」,不足 1 分钟显示「x 秒」,0 或负数显示破折号。
    ''' </summary>
    Public Function PlayTimeFormat(Ticks As Long) As String
        If Ticks <= 0 Then Return "—"
        Dim TotalSeconds = Ticks \ TicksPerSecond
        Dim Hours = TotalSeconds \ 3600
        Dim Minutes = (TotalSeconds Mod 3600) \ 60
        If Hours > 0 Then Return $"{Hours} 小时 {Minutes} 分钟"
        If Minutes > 0 Then Return $"{Minutes} 分钟"
        Return $"{TotalSeconds} 秒"
    End Function

#End Region

#Region "账号 UUID"

    ''' <summary>
    ''' 当前账号可能对应的全部统计文件 UUID,统一成「小写、去掉连字符」的形式。
    ''' 同时包含在线账号与按用户名推导出的离线 UUID —— 同一个存档可能两种都留下过文件。
    ''' </summary>
    Public Function PlayTimeAccountUuids() As List(Of String)
        Dim Result As New List(Of String)
        PlayTimeAddUuid(Result, Settings.Get(Of String)("CacheMsV2Uuid"))
        PlayTimeAddUuid(Result, Settings.Get(Of String)("CacheNideUuid"))
        PlayTimeAddUuid(Result, Settings.Get(Of String)("CacheAuthUuid"))
        '离线登录:LoginLegacyName 的格式是「名称¨皮肤类型」,只取名称部分
        Dim LegacyName = Settings.Get(Of String)("LoginLegacyName")
        If Not String.IsNullOrWhiteSpace(LegacyName) Then
            PlayTimeAddUuid(Result, McLoginLegacyUuid(LegacyName.BeforeFirst("¨")))
        End If
        Return Result.Distinct().ToList
    End Function

    Private Sub PlayTimeAddUuid(Target As List(Of String), Uuid As String)
        If String.IsNullOrWhiteSpace(Uuid) Then Return
        Target.Add(Uuid.Replace("-", "").ToLowerInvariant())
    End Sub

#End Region

End Module
