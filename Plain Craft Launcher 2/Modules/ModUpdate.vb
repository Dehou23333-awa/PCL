Imports System.Globalization
Imports System.Security.Cryptography

''' <summary>
''' 从 GitHub Releases 获取到的更新信息。
''' </summary>
Public Class UpdateInfo
    ''' <summary>
    ''' 发布时使用的版本标签。
    ''' </summary>
    Public Property Tag As String = ""
    ''' <summary>
    ''' 发布时写入的版本号（对应 ModBase 中手工递增的 VersionCode）。
    ''' 发布流程保证一定会提供，未提供时为 -1。
    ''' </summary>
    Public Property VersionCode As Integer = -1
    ''' <summary>
    ''' 发布时间。未知时为 Nothing。
    ''' </summary>
    Public Property PublishedAt As Date? = Nothing
    ''' <summary>
    ''' 更新说明（Release 正文）。
    ''' </summary>
    Public Property Notes As String = ""
    ''' <summary>
    ''' 对应 Release 的网页地址。
    ''' </summary>
    Public Property PageUrl As String = ""
    ''' <summary>
    ''' 下载包的文件名。
    ''' </summary>
    Public Property AssetName As String = ""
    ''' <summary>
    ''' 下载包的直链。没有附件时为 Nothing。
    ''' </summary>
    Public Property AssetUrl As String = Nothing
    ''' <summary>
    ''' 下载包的字节数。未知时为 -1。
    ''' </summary>
    Public Property AssetSize As Long = -1
    ''' <summary>
    ''' 下载包的校验值，形如 sha256:xxxx。未知时为 Nothing。
    ''' </summary>
    Public Property AssetDigest As String = Nothing
    ''' <summary>
    ''' 是否为预发布版本。
    ''' </summary>
    Public Property IsPrerelease As Boolean = False
    ''' <summary>
    ''' 本次检查的时间（Unix 毫秒）。0 表示未知。
    ''' </summary>
    Public Property CheckedAt As Long = 0
End Class

''' <summary>
''' 从 GitHub Releases 检查并获取 PCL 的更新。
''' <para/>为避免触发 GitHub API 的频率限制，同一个版本的检查结果会缓存 1 小时；
''' 用户手动检查更新时才会忽略缓存。缓存本身又配合 ETag 协商，304 响应不会消耗 API 额度。
''' </summary>
Public Module ModUpdate

#Region "配置"

    ''' <summary>
    ''' 用于检查更新的 GitHub 仓库（owner/name）。
    ''' </summary>
    Public Const UpdateRepo As String = "Dehou23333-awa/PCL"
    ''' <summary>
    ''' 自动检查更新的最短间隔，单位为小时。
    ''' </summary>
    Private Const CacheValidHours As Integer = 1
    ''' <summary>
    ''' 更新临时文件夹，用于存放下载中的更新包。
    ''' <para/>不能声明为字段初始化：模块静态构造顺序不确定，那时 PathTemp 可能还没赋值。
    ''' </summary>
    Private ReadOnly Property UpdateTempFolder As String
        Get
            Return PathTemp & "Cache\Update\"
        End Get
    End Property
    ''' <summary>
    ''' 更新说明在弹窗中展示的最大长度。
    ''' </summary>
    Private Const NotesMaxLength As Integer = 400

#End Region

#Region "版本号"

    ''' <summary>
    ''' 从字符串中提取发布时写入的版本号（VersionCode）。
    ''' <para/>资产名以 “-<版本号>.zip” 结尾、发布名以 “(<版本号>)” 结尾，两者任一均可。
    ''' 未找到时返回 -1。
    ''' </summary>
    Private Function ParseVersionCode(Text As String) As Integer
        If String.IsNullOrWhiteSpace(Text) Then Return -1
        '匹配资产名末尾的数字后缀
        Dim Digits As String = Text.RegexSeek("(?<=-)\d{1,6}(?=\.zip$)")
        '匹配发布名称末尾括号中的数字
        If Digits Is Nothing Then Digits = Text.RegexSeek("(?<=\()\d{1,6}(?=\)$)")
        Dim Code As Integer
        If Digits Is Nothing OrElse Not Integer.TryParse(Digits, Code) Then Return -1
        Return Code
    End Function

    ''' <summary>
    ''' 判断远端版本是否比本地版本更新。
    ''' <para/>直接比较手工递增的 VersionCode；若远端没有提供版本号，则视为没有更新。
    ''' </summary>
    Public Function IsNewerVersion(Info As UpdateInfo) As Boolean
        If Info Is Nothing OrElse Info.VersionCode <= 0 Then Return False
        Return Info.VersionCode > VersionCode
    End Function

#End Region

#Region "结果缓存"

    Private CachedInfo As UpdateInfo = Nothing
    Private IsCacheLoaded As Boolean = False
    Private ReadOnly CacheLock As New Object
    ''' <summary>
    ''' 检查结果的缓存文件。不能声明为字段初始化，理由同 UpdateTempFolder。
    ''' </summary>
    Private ReadOnly Property UpdateCachePath As String
        Get
            Return PathTemp & "Cache\Update.json"
        End Get
    End Property

    ''' <summary>
    ''' 获取上一次检查得到的更新信息。从未检查过或缓存损坏时返回 Nothing。
    ''' <para/>本函数只读本地文件，不会发起网络请求。
    ''' </summary>
    Public Function GetCachedUpdateInfo() As UpdateInfo
        SyncLock CacheLock
            If IsCacheLoaded Then Return CachedInfo
            IsCacheLoaded = True
            Try
                Dim Text As String = FileUtils.TryReadAsString(UpdateCachePath)
                If String.IsNullOrWhiteSpace(Text) Then Return Nothing
                Dim Json = Text.DeserializeJson()
                If Json Is Nothing Then Return Nothing
                Dim PublishedAt As Date?
                Dim PublishedText As String = Json("published")?.ToString()
                Dim PublishedOffset As DateTimeOffset
                If Not String.IsNullOrWhiteSpace(PublishedText) AndAlso
                   DateTimeOffset.TryParse(PublishedText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, PublishedOffset) Then
                    PublishedAt = PublishedOffset.LocalDateTime
                End If
                CachedInfo = New UpdateInfo With {
                    .Tag = If(Json("tag")?.ToString(), ""),
                    .VersionCode = If(Json("code") Is Nothing, -1, Json("code").ToObject(Of Integer)()),
                    .PublishedAt = PublishedAt,
                    .Notes = If(Json("notes")?.ToString(), ""),
                    .PageUrl = If(Json("page")?.ToString(), ""),
                    .AssetName = If(Json("assetName")?.ToString(), ""),
                    .AssetUrl = If(Json("assetUrl")?.ToString(), Nothing),
                    .AssetSize = If(Json("assetSize") Is Nothing, -1L, Json("assetSize").ToObject(Of Long)()),
                    .AssetDigest = If(Json("assetDigest")?.ToString(), Nothing),
                    .IsPrerelease = If(Json("prerelease") Is Nothing, False, Json("prerelease").ToObject(Of Boolean)()),
                    .CheckedAt = If(Json("checked") Is Nothing, 0L, Json("checked").ToObject(Of Long)())
                }
                Return CachedInfo
            Catch ex As Exception
                Logger.Warn(ex, "读取更新信息缓存失败")
                FileUtils.Delete(UpdateCachePath)
                Return Nothing
            End Try
        End SyncLock
    End Function

    Private Sub SaveUpdateCache(Info As UpdateInfo)
        SyncLock CacheLock
            CachedInfo = Info
            IsCacheLoaded = True
            Try
                Dim Json As New JObject From {
                    {"tag", Info.Tag},
                    {"code", Info.VersionCode},
                    {"published", If(Info.PublishedAt Is Nothing, "", Info.PublishedAt.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture))},
                    {"notes", Info.Notes},
                    {"page", Info.PageUrl},
                    {"assetName", Info.AssetName},
                    {"assetUrl", If(Info.AssetUrl, "")},
                    {"assetSize", Info.AssetSize},
                    {"assetDigest", If(Info.AssetDigest, "")},
                    {"prerelease", Info.IsPrerelease},
                    {"checked", Info.CheckedAt}
                }
                FileUtils.Write(UpdateCachePath, Json.ToString(Formatting.None))
            Catch ex As Exception
                Logger.Warn(ex, "保存更新信息缓存失败")
            End Try
        End SyncLock
    End Sub

    ''' <summary>
    ''' 检查结果是否仍在有效期内（默认 1 小时），以避免频繁触发 GitHub API 的频率限制。
    ''' </summary>
    Private Function IsCacheValid(Info As UpdateInfo) As Boolean
        If Info Is Nothing OrElse Info.CheckedAt <= 0 Then Return False
        Dim Elapsed As Long = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Info.CheckedAt
        Return Elapsed >= 0 AndAlso Elapsed < CacheValidHours * 3600L * 1000L
    End Function

#End Region

#Region "检查更新"

    ''' <summary>
    ''' 更新检查加载器。输入为是否由用户手动触发：手动触发会忽略缓存，并在无更新时给出提示。
    ''' <para/>同一输入在 ReloadTimeout 内不会重复执行，避免短时间内反复请求 GitHub API。
    ''' </summary>
    Public UpdateLoader As New LoaderTask(Of Boolean, UpdateInfo)("PCL 更新检查", AddressOf UpdateSub, Priority:=ThreadPriority.BelowNormal) With
        {.ReloadTimeout = CacheValidHours * 1000 * 60 * 60} '超时 1 小时
    ''' <summary>
    ''' 更新检查的主体：先读本地缓存，再根据需要联网，最后由加载器自己处理结果。
    ''' </summary>
    Private Sub UpdateSub(Loader As LoaderTask(Of Boolean, UpdateInfo))
        Dim IsManual As Boolean = Loader.Input
        '读取上次的检查结果，确保离线或未联网时也能判断版本
        Loader.Output = GetCachedUpdateInfo()
        Loader.Progress = 0.2
        '清理上次更新遗留的临时文件
        CleanUpdateTemp()
        '上次下载好的更新包仍在，继续等待退出时应用
        If FileUtils.Exists(UpdateNewExePath) Then
            Dim PendingCode As Integer = Settings.Get(Of Integer)("SystemUpdatePendingCode")
            If PendingCode > 0 AndAlso PendingCode <= VersionCode Then
                '已经手动更新过，旧更新包不能再覆盖程序
                Logger.Info($"待应用的更新包版本号（{PendingCode}）不高于当前版本（{VersionCode}），已丢弃")
                FileUtils.Delete(UpdateNewExePath)
                Settings.Set("SystemUpdatePendingCode", 0)
            Else
                IsUpdateWaitingRestart = True
                Logger.Info($"发现待应用的更新包：{UpdateNewExePath}（版本号 {PendingCode}）")
            End If
        End If
        '联网获取最新版本
        If IsManual OrElse Not IsCacheValid(Loader.Output) Then
            Try
                '强制重启时不对同一接口做重试，避免在 403 时把额度彻底耗光
                Loader.Output = RequestUpdateInfo()
                SaveUpdateCache(Loader.Output)
            Catch ex As Exception
                Logger.Warn(ex, "检查更新失败")
                If IsManual Then Hint("检查更新失败：" & ex.Message, HintType.Red)
                If Loader.Output Is Nothing Then Return '没有任何数据可用于判断
            End Try
        Else
            Logger.Info("更新检查结果仍在有效期内，跳过联网检查")
        End If
        Loader.Progress = 0.8
        '判断结果
        If Not IsNewerVersion(Loader.Output) Then
            If IsManual Then Hint($"当前已是最新版本（{VersionBaseName}，版本号 {VersionCode}）", HintType.Green)
            Return
        End If
        If Not IsManual Then
            '启动时的检查：遵循设置，并且同一个版本只提示一次
            Dim Behavior As Integer = Settings.Get(Of Integer)("SystemSystemUpdate")
            If Behavior >= 3 Then Return
            If Settings.Get(Of String)("SystemUpdateNotifiedTag") = Loader.Output.Tag Then Return
            Settings.Set("SystemUpdateNotifiedTag", Loader.Output.Tag)
            '设置为“在有新版本时自动下载”时，直接在后台准备更新包
            If Behavior = 0 Then
                UpdateStart(Silent:=True, Info:=Loader.Output)
                Return
            End If
        End If
        ShowUpdateMsgBox(Loader.Output)
    End Sub

    ''' <summary>
    ''' 清理上次更新遗留的临时文件。
    ''' </summary>
    Private Sub CleanUpdateTemp()
        Try
            '下载中的压缩包可以安全删除，但 update.exe 代表待应用的更新，必须保留
            FileUtils.Delete(UpdateTempFolder & "PCL.zip")
            FileUtils.Delete(UpdateTempFolder & "Latest.zip")
            FileUtils.Delete(UpdateUpdaterPath)
            DirectoryUtils.Create(UpdateTempFolder)
        Catch ex As Exception
            Logger.Warn(ex, "清理更新临时文件失败")
        End Try
    End Sub

    ''' <summary>
    ''' 请求 GitHub API 获取最新正式版信息。失败时抛出异常。
    ''' <para/>只使用 releases/latest 这一个端点（正式版通道，自动排除草稿与预发布版本），
    ''' 且底层使用 ETag 缓存协商，304 响应不会消耗 API 额度。
    ''' </summary>
    Private Function RequestUpdateInfo() As UpdateInfo
        Dim Url As String = $"https://api.github.com/repos/{UpdateRepo}/releases/latest"
        Dim Result As String
        Try
            '此处不使用 Retry 版本，避免在 403 时反复重试把额度彻底耗光
            Result = NetRequestByClient(Url, Accept:="application/vnd.github+json", Timeout:=15000, RequireJson:=True)
        Catch ex As HttpRequestCodeException
            Select Case CInt(ex.StatusCode)
                Case 403, 429
                    Throw New Exception($"GitHub API 的访问次数已达上限（{CInt(ex.StatusCode)}），请稍后再试", ex)
                Case 404
                    Throw New Exception($"未找到更新仓库或发布（{UpdateRepo}），请检查仓库地址是否正确，或该仓库尚未发布任何正式版", ex)
                Case Else
                    Throw
            End Select
        End Try
        '解析结果
        Dim Json = Result.DeserializeJson()
        If Json Is Nothing OrElse String.IsNullOrWhiteSpace(Json("tag_name")?.ToString()) Then
            Throw New Exception("GitHub API 返回的内容中不包含版本标签，可能是 API 地址被劫持或返回了错误内容。")
        End If
        Dim Info As New UpdateInfo With {
            .Tag = Json("tag_name").ToString(),
            .Notes = If(Json("body")?.ToString(), ""),
            .PageUrl = If(Json("html_url")?.ToString(), ""),
            .IsPrerelease = If(Json("prerelease") Is Nothing, False, Json("prerelease").ToObject(Of Boolean)()),
            .CheckedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }
        Dim PublishedOffset As DateTimeOffset
        If DateTimeOffset.TryParse(If(Json("published_at")?.ToString(), ""), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, PublishedOffset) Then
            Info.PublishedAt = PublishedOffset.LocalDateTime
        End If
        '优先选择 zip 附件
        Dim Assets = TryCast(Json("assets"), JArray)
        If Assets IsNot Nothing AndAlso Assets.Count > 0 Then
            Dim Asset = Assets.FirstOrDefault(Function(a) If(a("name")?.ToString(), "").EndsWithF(".zip", True))
            If Asset Is Nothing Then Asset = Assets.First()
            Info.AssetName = If(Asset("name")?.ToString(), "")
            Info.AssetUrl = If(Asset("browser_download_url")?.ToString(), Nothing)
            Info.AssetSize = If(Asset("size") Is Nothing, -1L, Asset("size").ToObject(Of Long)())
            Info.AssetDigest = If(Asset("digest")?.ToString(), Nothing)
        End If
        '从资产名中提取发布时写入的版本号，其次尝试发布名称
        Info.VersionCode = ParseVersionCode(Info.AssetName)
        If Info.VersionCode < 0 Then Info.VersionCode = ParseVersionCode(Json("name")?.ToString())
        If Info.VersionCode < 0 Then Logger.Warn($"未能从发布 {Info.Tag} 的名称（{Info.AssetName}）中获取版本号，本次将忽略该版本")
        Logger.Info($"最新版本：{Info.Tag}（版本号 {If(Info.VersionCode < 0, "未知", Info.VersionCode.ToString())}，发布于 {If(Info.PublishedAt Is Nothing, "未知", Info.PublishedAt.Value.ToString("yyyy-MM-dd HH:mm"))}），附件：{Info.AssetName}")
        Return Info
    End Function

    ''' <summary>
    ''' 当前运行中的 PCL 是否已经是最新版本。
    ''' 若返回 Nothing，则代表尚未获取到更新信息（例如从未成功检查过）。
    ''' </summary>
    Public Function IsLauncherNewest() As Boolean?
        Dim Info As UpdateInfo = GetCachedUpdateInfo()
        If Info Is Nothing Then Return Nothing
        If Info.VersionCode <= 0 Then Return Nothing
        Return Not IsNewerVersion(Info)
    End Function

    ''' <summary>
    ''' 由用户主动触发的更新检查。不受缓存与设置项限制。
    ''' </summary>
    Public Sub UpdateCheckByButton()
        RunInNewThread(
        Sub()
            Hint("正在检查最新版本……")
            Try
                '输入为 True 表示手动触发：忽略缓存，并在结果中给出提示
                UpdateLoader.WaitForExit(True, IsForceRestart:=True)
            Catch ex As Exception
                Logger.Warn(ex, "检查更新失败")
            End Try
        End Sub, "UpdateCheckByButton")
    End Sub

    ''' <summary>
    ''' 启动时的静默更新检查。按设置决定是否执行，实际请求频率由缓存与 ReloadTimeout 限制。
    ''' </summary>
    Public Sub UpdateCheckOnStartup()
        Try
            If BuildType = BuildTypes.Debug Then Return '开发版不检查更新
            If Settings.Get(Of Integer)("SystemSystemUpdate") >= 3 Then Return '用户关闭了更新提示
            UpdateLoader.Start(False)
        Catch ex As Exception
            Logger.Warn(ex, "启动时检查更新失败")
        End Try
    End Sub

    ''' <summary>
    ''' 展示发现新版本的提示。
    ''' <para/>目前尚未实现自动下载与替换，因此点击按钮后直接前往发布页。
    ''' </summary>
    Private Sub ShowUpdateMsgBox(Info As UpdateInfo)
        Dim Text As String = $"发现 PCL 新版本：{Info.Tag}（当前版本：{VersionBaseName}，版本号 {VersionCode}）"
        If Info.VersionCode > 0 Then Text &= vbCrLf & "新版版本号：" & Info.VersionCode
        If Info.PublishedAt IsNot Nothing Then Text &= vbCrLf & "发布于 " & Info.PublishedAt.Value.ToString("yyyy-MM-dd HH:mm")
        Dim Notes As String = If(Info.Notes, "").ReplaceLineEndings(vbLf).Trim()
        If Notes.Length > 0 Then
            If Notes.Length > NotesMaxLength Then Notes = Notes.Substring(0, NotesMaxLength) & "……"
            Text &= vbCrLf & vbCrLf & Notes
        End If
        Select Case MyMsgBox(Text, "发现新版本", "立即更新", "稍后再说", "前往发布页")
            Case 1
                UpdateStart(Info:=Info)
            Case 3
                OpenWebsite(If(String.IsNullOrWhiteSpace(Info.PageUrl), $"https://github.com/{UpdateRepo}/releases", Info.PageUrl))
        End Select
    End Sub

#End Region

#Region "下载与替换"

    ''' <summary>
    ''' 待替换的新版本程序路径。存在该文件即代表更新包已下载就绪。
    ''' </summary>
    Public ReadOnly Property UpdateNewExePath As String
        Get
            Return UpdateTempFolder & "update.exe"
        End Get
    End Property

    ''' <summary>
    ''' 更新器的路径。PCL 会把自己复制到这里，再由它执行替换。
    ''' </summary>
    Public ReadOnly Property UpdateUpdaterPath As String
        Get
            Return Paths.Base & "PCL\update.exe"
        End Get
    End Property

    ''' <summary>
    ''' 是否已准备好更新包，等待程序退出后替换。
    ''' </summary>
    Public IsUpdateWaitingRestart As Boolean = False
    ''' <summary>
    ''' 用户在更新提示中是否选择了“立即重启”。
    ''' </summary>
    Private IsUpdateRestartPending As Boolean = False

    ''' <summary>
    ''' 下载并准备更新包。就绪后会在程序退出时自动替换。
    ''' <para/>下载会作为任务加入任务管理，可查看进度与取消。
    ''' </summary>
    ''' <param name="Silent">是否静默下载，即不询问是否立即重启。</param>
    ''' <param name="Info">已获取的更新信息。为空时会自动等待检查结果。</param>
    Public Sub UpdateStart(Optional Silent As Boolean = False, Optional Info As UpdateInfo = Nothing)
        RunInNewThread(
        Sub()
            Try
                '获取更新信息
                If Info Is Nothing Then
                    If UpdateLoader.Output Is Nothing Then UpdateLoader.WaitForExit(False, IsForceRestart:=True)
                    Info = UpdateLoader.Output
                End If
                If Info Is Nothing Then Throw New Exception("尚未获取到新版本的信息，请先检查更新")
                If String.IsNullOrWhiteSpace(Info.AssetUrl) Then
                    Hint("该版本没有提供可直接下载的更新包，将前往发布页手动下载", HintType.Red)
                    OpenWebsite(If(String.IsNullOrWhiteSpace(Info.PageUrl), $"https://github.com/{UpdateRepo}/releases", Info.PageUrl))
                    Return
                End If
                '下载更新包（包成组合加载器，以便出现在任务管理中）
                DirectoryUtils.Create(UpdateTempFolder)
                Dim ZipPath As String = UpdateTempFolder & "PCL.zip"
                FileUtils.Delete(ZipPath)
                Dim DownloadTask As New LoaderDownload("下载更新包", New List(Of NetFile) From {New NetFile({Info.AssetUrl}, ZipPath)}) With {.ProgressWeight = 100}
                Dim Downloader As New LoaderCombo(Of List(Of NetFile))($"下载 PCL 更新包（{Info.Tag}）", {DownloadTask})
                Downloader.Start()
                LoaderTaskbarAdd(Downloader)
                FrmMain?.BtnExtraDownload.ShowRefresh()
                Try
                    Downloader.WaitForExit()
                Catch ex As OperationCanceledException
                    FileUtils.Delete(ZipPath)
                    Hint("已取消下载更新包", HintType.Blue)
                    Return
                Finally
                    Downloader.Cancel()
                End Try
                '校验与解压
                VerifyUpdatePackage(ZipPath, Info)
                ExtractUpdateExe(ZipPath, UpdateNewExePath)
                FileUtils.Delete(ZipPath)
                IsUpdateWaitingRestart = True
                Settings.Set("SystemUpdatePendingCode", Info.VersionCode)
                Logger.Info($"更新包已准备就绪：{Info.Tag} → {UpdateNewExePath}")
                '提示重启
                If Silent Then
                    Hint("新版本已下载完成，将在关闭 PCL 后自动更新", HintType.Blue)
                    Return
                End If
                If MyMsgBox("更新包已下载完成，但是需要重启 PCL 才能完成更新。" & vbCrLf &
                            "若选择稍后重启，更新将在关闭 PCL 时自动完成。", "更新就绪", "立即重启", "稍后重启") = 1 Then
                    IsUpdateRestartPending = True
                    RunInUi(Sub() FrmMain?.EndProgram(False))
                Else
                    Hint("更新将在关闭 PCL 时自动应用", HintType.Blue)
                End If
            Catch ex As Exception
                Logger.Warn(ex, "下载更新包失败")
                Hint("下载更新包失败：" & ex.Message, HintType.Red)
                FileUtils.Delete(UpdateTempFolder & "PCL.zip")
            End Try
        End Sub, "UpdateStart")
    End Sub

    ''' <summary>
    ''' 校验下载得到的更新包（大小与 SHA-256）。校验不通过时抛出异常。
    ''' </summary>
    Private Sub VerifyUpdatePackage(ZipPath As String, Info As UpdateInfo)
        If Not FileUtils.Exists(ZipPath) Then Throw New Exception("更新包下载失败，文件不存在")
        Dim Length As Long = New FileInfo(ZipPath).Length
        If Info.AssetSize > 0 AndAlso Length <> Info.AssetSize Then
            Throw New Exception($"更新包大小不正确（应为 {Info.AssetSize} 字节，实际为 {Length} 字节），请重试或前往发布页手动下载")
        End If
        If String.IsNullOrWhiteSpace(Info.AssetDigest) Then Return
        'GitHub 提供的校验值形如 sha256:xxxx
        Dim Parts As String() = Info.AssetDigest.Split(":"c)
        If Parts.Length <> 2 OrElse Not String.Equals(Parts(0).Trim(), "sha256", StringComparison.OrdinalIgnoreCase) Then
            Logger.Warn($"未知的更新包校验方式，已跳过校验：{Info.AssetDigest}")
            Return
        End If
        Dim Actual As String = GetFileSha256(ZipPath)
        If Not String.Equals(Actual, Parts(1).Trim(), StringComparison.OrdinalIgnoreCase) Then
            Throw New Exception("更新包校验失败（SHA-256 不一致），已取消更新，请重试或前往发布页手动下载")
        End If
        Logger.Info("更新包 SHA-256 校验通过")
    End Sub

    Private Function GetFileSha256(FilePath As String) As String
        Using Stream As New FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)
            Using Sha As SHA256 = SHA256.Create()
                Return BitConverter.ToString(Sha.ComputeHash(Stream)).Replace("-", "").ToLowerInvariant()
            End Using
        End Using
    End Function

    ''' <summary>
    ''' 从更新包中提取程序本体。
    ''' </summary>
    Private Sub ExtractUpdateExe(ZipPath As String, TargetPath As String)
        Using Archive As ZipArchive = FileUtils.OpenZip(ZipPath)
            '优先取程序本体，其次取任意 exe
            Dim Entry = Archive.Entries.FirstOrDefault(Function(e) String.Equals(e.Name, "Plain Craft Launcher 2.exe", StringComparison.OrdinalIgnoreCase))
            If Entry Is Nothing Then Entry = Archive.Entries.FirstOrDefault(Function(e) Not String.IsNullOrEmpty(e.Name) AndAlso e.Name.EndsWithF(".exe", True))
            If Entry Is Nothing Then Throw New Exception("更新包中不包含可执行文件，无法完成更新")
            FileUtils.Delete(TargetPath)
            DirectoryUtils.Create(Path.GetDirectoryName(TargetPath))
            Entry.ExtractToFile(TargetPath, True)
            Logger.Info($"已从更新包中提取程序文件：{Entry.FullName}（{Entry.Length} 字节）")
        End Using
    End Sub

    ''' <summary>
    ''' 启动更新进程完成替换。必须在主进程退出前调用（目前由 EndProgramForce 触发）。
    ''' <para/>程序本体正在被占用，无法自我替换，因此先把自己复制为 PCL\update.exe，
    ''' 再由它带上 --update 参数启动，由 Application 的对应分支执行实际替换。
    ''' </summary>
    ''' <param name="TriggerRestartAndByEnd">替换后是否重新启动 PCL。</param>
    Public Sub UpdateRestart(TriggerRestartAndByEnd As Boolean)
        If Not IsUpdateWaitingRestart Then Return
        If Not FileUtils.Exists(UpdateNewExePath) Then
            Logger.Warn($"待应用的更新文件不存在，已跳过更新：{UpdateNewExePath}")
            Return
        End If
        '复制自身作为更新器，避免更新器占用待替换的程序本体
        FileUtils.Copy(PathExe, UpdateUpdaterPath)
        Logger.Info($"已复制更新器：{UpdateUpdaterPath}")
        Dim ShouldRestart As Boolean = TriggerRestartAndByEnd OrElse IsUpdateRestartPending
        Logger.Info($"启动更新进程：{UpdateUpdaterPath} --update {Process.GetCurrentProcess().Id}（重启：{ShouldRestart}）")
        StartProcess(UpdateUpdaterPath, $"--update {Process.GetCurrentProcess().Id} ""{PathExe}"" ""{UpdateNewExePath}"" {ShouldRestart}")
    End Sub

    ''' <summary>
    ''' 由 --update 参数启动的更新进程所执行的实际替换。
    ''' <para/>等待旧进程退出后覆盖程序本体，并按需重新启动。
    ''' </summary>
    Public Sub UpdateReplace(ProcessId As Integer, OldFileName As String, NewFileName As String, TriggerRestart As Boolean)
        Try
            Logger.Info($"更新进程已启动，等待进程 {ProcessId} 退出……")
            '等待旧进程退出
            Try
                Process.GetProcessById(ProcessId).WaitForExit(30000)
            Catch ex As ArgumentException
                '旧进程已经不存在
            End Try
            '覆盖程序本体（旧进程刚退出时文件可能仍被短暂占用，因此重试）
            Dim Retry As Integer = 0
            Do
                Try
                    FileUtils.Copy(NewFileName, OldFileName)
                    FileUtils.Delete(NewFileName)
                    Exit Do
                Catch ex As Exception
                    Retry += 1
                    If Retry > 20 Then Throw
                    Thread.Sleep(300)
                End Try
            Loop
            Logger.Info("程序文件替换完成")
            Settings.Set("SystemUpdatePendingCode", 0)
            If TriggerRestart Then
                Logger.Info($"正在重新启动 PCL：{OldFileName}")
                StartProcess(OldFileName)
            End If
        Catch ex As Exception
            Logger.Warn(ex, "替换程序文件失败")
            MsgBox("PCL 更新失败：" & ex.Message & vbCrLf & vbCrLf &
                   "请手动将以下文件复制到目标位置：" & vbCrLf & NewFileName & vbCrLf & "→" & vbCrLf & OldFileName,
                   MsgBoxStyle.Critical, "更新失败")
        Finally
            Environment.Exit(ProcessReturnValues.TaskDone)
        End Try
    End Sub

    ''' <summary>
    ''' 确保 PathTemp\Latest.exe 是最新正式版的 PCL，它会被用于整合包打包。
    ''' </summary>
    Public Sub DownloadLatestPCL(Optional LoaderToSyncProgress As LoaderBase = Nothing)
        '正式版自身就是最新正式版，直接复制
        If BuildType = BuildTypes.Release Then
            FileUtils.Copy(PathExe, PathTemp & "Latest.exe")
            Logger.Info("已将当前正式版复制为 Latest.exe")
            Return
        End If
        '其他构建需要从 GitHub 获取最新正式版
        UpdateLoader.WaitForExit(False, LoaderToSyncProgress:=LoaderToSyncProgress)
        Dim Info As UpdateInfo = UpdateLoader.Output
        If Info Is Nothing OrElse String.IsNullOrWhiteSpace(Info.AssetUrl) Then Throw New Exception("无法获取最新正式版 PCL 的下载地址")
        DirectoryUtils.Create(UpdateTempFolder)
        Dim ZipPath As String = UpdateTempFolder & "Latest.zip"
        FileUtils.Delete(ZipPath)
        Dim Downloader As New LoaderDownload("下载最新正式版 PCL", New List(Of NetFile) From {New NetFile({Info.AssetUrl}, ZipPath)})
        Try
            Downloader.WaitForExit(LoaderToSyncProgress:=LoaderToSyncProgress)
        Finally
            Downloader.Cancel()
        End Try
        VerifyUpdatePackage(ZipPath, Info)
        ExtractUpdateExe(ZipPath, PathTemp & "Latest.exe")
        FileUtils.Delete(ZipPath)
        Logger.Info($"已保存最新正式版 PCL：{Info.Tag}")
    End Sub

#End Region

End Module
