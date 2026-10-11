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
    ''' 读取一个统计文件里的游玩时长,单位 tick。
    ''' 文件不存在、或格式正常但没有可识别的时长字段,返回 0;
    ''' 文件存在但读取或解析失败,返回 -1(调用方据此显示「出错」)。
    ''' </summary>
    Public Function PlayTimeFromStatsFile(FilePath As String) As Long
        If Not FileUtils.Exists(FilePath) Then Return 0
        Dim Json As JObject = Nothing
        Try
            Json = FileUtils.ReadAsJson(Of JObject)(FilePath)
        Catch ex As Exception
            Logger.Warn(ex, "读取存档统计文件失败:" & FilePath)
            Return -1
        End Try
        If Json Is Nothing Then Return -1

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
    ''' 把 tick 格式化为可读文本。-1 显示「出错」,0 显示破折号,
    ''' 1 小时以上显示「x 小时 y 分钟」,1 分钟以上显示「x 分钟」,不足 1 分钟显示「x 秒」。
    ''' </summary>
    Public Function PlayTimeFormat(Ticks As Long) As String
        If Ticks < 0 Then Return "出错"
        If Ticks = 0 Then Return "—"
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
    ''' 当前账号在统计目录里的匹配信息。
    ''' </summary>
    Public Class PlayTimeAccount

        ''' <summary>在线账号的完整 UUID,统一成 32 位小写十六进制(无连字符)。</summary>
        Public ReadOnly Online As New List(Of String)

        ''' <summary>离线账号 UUID 的前 27 位前缀。空串表示没有可用的离线账号。</summary>
        Public OfflinePrefix As String = ""

        ''' <summary>判断一个统计文件名(不含扩展名)是否属于当前账号。</summary>
        Public Function Matches(UuidFromFile As String) As Boolean
            Dim U As String = If(UuidFromFile, "").Replace("-", "").ToLowerInvariant()
            If U.Length <> 32 Then Return False
            If Online.Contains(U) Then Return True
            Return OfflinePrefix <> "" AndAlso U.StartsWith(OfflinePrefix)
        End Function

    End Class

    ''' <summary>
    ''' 当前账号的匹配信息。
    ''' 离线账号只比前 27 位,是因为 PCL 会按离线皮肤设置递增 UUID 的最后 5 位
    ''' (McLoginLegacyUuidWithCustomSkin),前 27 位才是该用户名稳定的部分。
    ''' </summary>
    Public Function PlayTimeCurrentAccount() As PlayTimeAccount
        Dim Result As New PlayTimeAccount
        PlayTimeAddOnline(Result, Settings.Get(Of String)("CacheMsV2Uuid"))
        PlayTimeAddOnline(Result, Settings.Get(Of String)("CacheNideUuid"))
        PlayTimeAddOnline(Result, Settings.Get(Of String)("CacheAuthUuid"))
        '离线登录:LoginLegacyName 是以 ¨ 分隔的玩家名列表,第一个是当前使用的那个
        Dim LegacyName = Settings.Get(Of String)("LoginLegacyName")
        If Not String.IsNullOrWhiteSpace(LegacyName) Then
            Dim Uuid As String = McLoginLegacyUuid(LegacyName.BeforeFirst("¨")).Replace("-", "").ToLowerInvariant()
            If Uuid.Length = 32 Then Result.OfflinePrefix = Uuid.Substring(0, 27)
        End If
        Return Result
    End Function

    Private Sub PlayTimeAddOnline(Target As PlayTimeAccount, Uuid As String)
        If String.IsNullOrWhiteSpace(Uuid) Then Return
        Target.Online.Add(Uuid.Replace("-", "").ToLowerInvariant())
    End Sub

#End Region

End Module
