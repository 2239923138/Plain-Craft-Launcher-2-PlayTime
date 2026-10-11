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

#Region "缓存"

    '缓存文件里存每个存档「上次统计时 stats 目录的最新修改时间」,
    '下次统计只要这个时间没变就跳过重读,避免每次都把所有存档的 JSON 解析一遍。
    Private ReadOnly Property PlayTimeCachePath As String
        Get
            Return PathTemp & "Cache\PlayTime.json"
        End Get
    End Property

    Private _PlayTimeCache As JObject = Nothing

    Private Function PlayTimeCacheGet() As JObject
        If _PlayTimeCache Is Nothing Then
            Try
                _PlayTimeCache = FileUtils.ReadAsJson(Of JObject)(PlayTimeCachePath)
            Catch ex As Exception
                Logger.Warn(ex, "读取游戏时长缓存失败,已重置")
            End Try
            If _PlayTimeCache Is Nothing Then _PlayTimeCache = New JObject
            If _PlayTimeCache.Item("Saves") Is Nothing Then _PlayTimeCache("Saves") = New JObject
        End If
        Return _PlayTimeCache
    End Function

    Public Sub PlayTimeCacheSave()
        If _PlayTimeCache Is Nothing Then Return
        Try
            DirectoryUtils.Create(IO.Path.GetDirectoryName(PlayTimeCachePath))
            FileUtils.Write(PlayTimeCachePath, _PlayTimeCache.ToString(Formatting.None))
        Catch ex As Exception
            Logger.Warn(ex, "写入游戏时长缓存失败")
        End Try
    End Sub

    ''' <summary>清空内存与磁盘缓存。手动刷新时调用。</summary>
    Public Sub PlayTimeClearCache()
        _PlayTimeCache = New JObject
        _PlayTimeCache("Saves") = New JObject
        PlayTimeCacheSave()
    End Sub

    ''' <summary>取一个存档 stats 目录下所有 JSON 的最新修改时间(UTC tick)。没有文件返回 0。</summary>
    Private Function PlayTimeStatsMtime(StatsFolder As String) As Long
        Dim Latest As Long = 0
        For Each File In DirectoryUtils.EnumerateFiles(StatsFolder, False, "*.json")
            Dim T = FileUtils.GetInfo(File).LastWriteTimeUtc.Ticks
            If T > Latest Then Latest = T
        Next
        Return Latest
    End Function

#End Region

#Region "扫描存档与版本"

    ''' <summary>
    ''' 当前账号的指纹。统计结果与账号相关(只算自己那份统计文件),
    ''' 而切换账号不会改动统计文件的修改时间,所以缓存必须带上它才能自动失效。
    ''' </summary>
    Private Function PlayTimeAccountFingerprint() As String
        Dim Account = PlayTimeCurrentAccount()
        Return Account.OfflinePrefix & "|" & String.Join(",", Account.Online.OrderBy(Function(S) S))
    End Function

    ''' <summary>
    ''' 判断一条缓存是否仍然有效。三个条件都要满足:Ticks 存在、Mtime 没变、账号指纹一致。
    ''' Ticks 缺失时不能当成 -1(那是「出错」的意思),否则一条残缺的缓存会被永久当成出错。
    ''' </summary>
    Private Function PlayTimeCacheHit(Cached As JObject, Mtime As Long, Fingerprint As String) As Boolean
        If Cached.Item("Ticks") Is Nothing Then Return False
        If PlayTimeReadTicks(Cached.Item("Mtime")) <> Mtime Then Return False
        Dim Account = Cached.Item("Account")
        Return Account IsNot Nothing AndAlso Account.ToString = Fingerprint
    End Function

    ''' <summary>
    ''' 单个存档中属于当前账号的游玩时长(tick)。SavePath 必须以 \ 结尾。
    ''' 优先按账号匹配统计文件;一个都匹配不上时,若该存档只有一个统计文件
    ''' (单机存档的常见情况)则直接采用,多个文件时无法判定归属,返回 -1(出错)。
    ''' 被采用的任意一个文件读取失败,也返回 -1。
    ''' </summary>
    Public Function PlayTimeGetSave(SavePath As String, Optional UseCache As Boolean = True) As Long
        Dim StatsFolder As String = SavePath & "stats\"
        If Not DirectoryUtils.Exists(StatsFolder) Then Return 0
        Dim Files = DirectoryUtils.EnumerateFiles(StatsFolder, False, "*.json").ToList
        If Files.Count = 0 Then Return 0

        Dim Mtime = PlayTimeStatsMtime(StatsFolder)
        Dim Fingerprint = PlayTimeAccountFingerprint()
        If UseCache Then
            Dim Saves = CType(PlayTimeCacheGet().Item("Saves"), JObject)
            Dim Cached = TryCast(Saves.Item(SavePath), JObject)
            If Cached IsNot Nothing AndAlso PlayTimeCacheHit(Cached, Mtime, Fingerprint) Then
                Return PlayTimeReadTicks(Cached.Item("Ticks"))
            End If
        End If

        Dim Fresh = PlayTimeSumFiles(Files)
        '读取失败(-1)不写缓存。否则一次偶发失败会因为 mtime 没变而被固化,
        '之后每次命中都返回「出错」,直到统计文件被改动为止。
        If UseCache AndAlso Fresh >= 0 Then
            Dim Entry As New JObject
            Entry("Mtime") = Mtime
            Entry("Ticks") = Fresh
            Entry("Account") = Fingerprint
            CType(PlayTimeCacheGet().Item("Saves"), JObject)(SavePath) = Entry
            '这里刻意不落盘 —— 一次全量扫描会调用本函数几十次,每个存档写一次文件太重。
            '落盘交给调用方:PlayTimeLoad / PlayTimeUpdateCurrent 在收尾时统一 PlayTimeCacheSave()。
        End If
        Return Fresh
    End Function

    ''' <summary>
    ''' 从一组统计文件里挑出属于当前账号的并求和。
    ''' 匹不上归属时:只有一个文件就直接采用,多个文件无法判定,返回 -1。
    ''' 被采用的文件里任意一个读取失败,整个存档也返回 -1。
    ''' </summary>
    Private Function PlayTimeSumFiles(Files As List(Of String)) As Long
        Dim Account = PlayTimeCurrentAccount()
        Dim Matched = Files.Where(Function(F) Account.Matches(IO.Path.GetFileNameWithoutExtension(F))).ToList
        If Matched.Count = 0 Then
            If Files.Count <> 1 Then Return -1
            Matched = Files
        End If
        Dim Total As Long = 0
        For Each File In Matched
            Dim Ticks = PlayTimeFromStatsFile(File)
            If Ticks < 0 Then Return -1
            Total += Ticks
        Next
        Return Total
    End Function

    ''' <summary>
    ''' 某个版本下全部存档的游玩时长合计(tick)。
    ''' 任意一个存档出错就整体返回 -1,让界面显示「出错」,而不是给出一个偏低但看起来正常的数。
    ''' </summary>
    Public Function PlayTimeGetVersion(Instance As McInstance, Optional UseCache As Boolean = True) As Long
        Dim SavesFolder As String = Instance.PathIndie & "saves\"
        If Not DirectoryUtils.Exists(SavesFolder) Then Return 0
        Dim Total As Long = 0
        For Each Save As IO.DirectoryInfo In DirectoryUtils.GetInfo(SavesFolder).EnumerateDirectories
            Dim Ticks = PlayTimeGetSave(Save.FullName & "\", UseCache)
            If Ticks < 0 Then Return -1
            Total += Ticks
        Next
        Return Total
    End Function

#End Region

#Region "异步加载与触发"

    ''' <summary>版本文件夹路径 → 该版本的游玩总时长(tick)。由 PlayTimeLoader 在后台填充,UI 只读。</summary>
    Public ReadOnly PlayTimeVersionCache As New ConcurrentDictionary(Of String, Long)

    Public PlayTimeLoader As New LoaderTask(Of Integer, Integer)("游戏时长统计", AddressOf PlayTimeLoad)

    Private Sub PlayTimeLoad(Loader As LoaderTask(Of Integer, Integer))
        Dim Instances = McInstanceList.Values.SelectMany(Function(List) List).ToList
        Dim Done As Integer = 0
        For Each Instance In Instances
            If Loader.IsCanceled Then Return
            PlayTimeVersionCache(Instance.PathVersion) = PlayTimeGetVersion(Instance)
            Done += 1
            Loader.Progress = Done / Math.Max(1.0, Instances.Count)
        Next
        PlayTimeCacheSave()
        Logger.Info($"游戏时长统计完成,共 {Instances.Count} 个版本")
    End Sub

    ''' <summary>只更新一个版本。PlayTimeGetVersion 内部靠 mtime 跳过没变过的存档。</summary>
    Public Sub PlayTimeUpdateCurrent(Instance As McInstance)
        If Instance Is Nothing Then Return
        Try
            PlayTimeVersionCache(Instance.PathVersion) = PlayTimeGetVersion(Instance)
            PlayTimeCacheSave()
        Catch ex As Exception
            Logger.Warn(ex, $"更新版本游戏时长失败:{Instance.Name}")
        End Try
    End Sub

    ''' <summary>游戏启动前记录一次快照,退出时据此知道要更新哪个版本。</summary>
    Public Sub PlayTimeMarkLaunch()
        Try
            Dim Cache = PlayTimeCacheGet()
            Cache("LastLaunchVersion") = If(McInstanceSelected Is Nothing, "", McInstanceSelected.PathVersion)
            Cache("LastLaunchPending") = True
            PlayTimeCacheSave()
        Catch ex As Exception
            Logger.Warn(ex, "记录游戏时长启动快照失败")
        End Try
    End Sub

    ''' <summary>游戏进程退出后调用,清掉待更新标记。</summary>
    Public Sub PlayTimeMarkLaunchFinished()
        Try
            PlayTimeCacheGet()("LastLaunchPending") = False
            PlayTimeCacheSave()
        Catch ex As Exception
            Logger.Warn(ex, "清除游戏时长启动快照失败")
        End Try
    End Sub

    ''' <summary>
    ''' PCL 启动时调用。若上一次游戏启动后 PCL 没等到进程退出就关了,
    ''' 这里补做一次那个版本的更新。
    ''' </summary>
    Public Sub PlayTimeCatchUp()
        Try
            Dim Cache = PlayTimeCacheGet()
            Dim Pending = Cache.Item("LastLaunchPending")
            If Pending Is Nothing OrElse Pending.Type <> JTokenType.Boolean OrElse Not Pending.ToObject(Of Boolean)() Then Return
            Dim Path As String = If(Cache.Item("LastLaunchVersion") Is Nothing, "", Cache.Item("LastLaunchVersion").ToString)
            Cache("LastLaunchPending") = False
            PlayTimeCacheSave()
            If Path = "" Then Return
            Dim Instance = McInstanceList.Values.SelectMany(Function(List) List).FirstOrDefault(Function(I) I.PathVersion = Path)
            If Instance IsNot Nothing Then
                Logger.Info($"检测到上次游戏启动未收尾,补更新游戏时长:{Instance.Name}")
                PlayTimeUpdateCurrent(Instance)
            End If
        Catch ex As Exception
            Logger.Warn(ex, "补更新游戏时长失败")
        End Try
    End Sub

    ''' <summary>手动刷新:清缓存后重扫全部版本。</summary>
    Public Sub PlayTimeRefreshAll()
        PlayTimeClearCache()
        PlayTimeLoader.Start(0, IsForceRestart:=True)
    End Sub

#End Region

End Module
