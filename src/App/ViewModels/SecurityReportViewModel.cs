using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Api;
using App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Models;

namespace App.ViewModels;

/// <summary>Describes the aggregate severity shown by the security report summary.</summary>
public enum SecuritySeverity
{
    Unknown,
    Good,
    Caution,
    Critical
}

/// <summary>Describes the lifecycle of the latest online exposure check.</summary>
public enum ExposureCheckState
{
    NotStarted,
    Checking,
    Succeeded,
    Failed
}

/// <summary>Projects an immutable reuse group into a bindable collection.</summary>
public sealed class ReusedPasswordGroup
{
    public ReusedPasswordGroup(ReusedGroup group)
    {
        Items = new ObservableCollection<HealthItemRef>(group.Items);
    }

    public int Count => Items.Count;
    public ObservableCollection<HealthItemRef> Items { get; }
}

/// <summary>Contains display data and the exact vault navigation identity of an issue.</summary>
public sealed record SecurityIssueRow(
    string CipherId,
    string Name,
    string Username,
    string Initial,
    string Reason,
    string Description,
    string Category,
    string ActionLabel,
    string ResultLabel,
    string Severity)
{
    public string Subtitle => string.IsNullOrWhiteSpace(Username)
        ? Reason
        : $"{Username} · {Reason}";
}

/// <summary>Provides one category of issue rows to the report page.</summary>
public sealed class SecurityIssueGroup(
    string category,
    string title,
    string description,
    IEnumerable<SecurityIssueRow> items)
{
    public string Category { get; } = category;
    public string Title { get; } = title;
    public string Description { get; } = description;
    public ObservableCollection<SecurityIssueRow> Items { get; } = new(items);
    public int Count => Items.Count;
}

public partial class SecurityReportViewModel : ObservableObject, ISensitiveState
{
    private const string OfflineAnalysisFailureMessage = "无法完成安全报告分析，请稍后重试。";
    private const string ExposureCheckFailureMessage = "无法完成暴露密码检查，请稍后重试。";
    private static readonly string[] IssueCategories = ["breach", "reuse", "weak", "http"];
    private static readonly StringComparer NameComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), ignoreCase: true);
    private readonly IVaultHealthUiService _service;
    private readonly Dictionary<(string Category, string CipherId), SecurityIssueRow> _baselineIssues = new();
    private readonly HashSet<string> _pendingExposureRechecks = new(StringComparer.Ordinal);
    private HealthReport? _offlineSnapshot;
    private IReadOnlyList<ExposedFinding> _exposureSnapshot = [];
    private long _snapshotRevision;
    private List<SecurityIssueRow> _currentIssues = [];
    private List<SecurityIssueRow> _overviewIssues = [];
    private List<SecurityIssueRow> _resolvedIssues = [];
    private bool _offlineBaselineCaptured;
    private bool _exposureBaselineCaptured;
    private bool _hasRequestedExposureCheck;
    private bool _hasExposureSnapshot;
    private bool _exposureSnapshotNeedsRefresh;
    private DateTimeOffset? _lastCheckedAt;
    private int _sourceUpdateDepth;
    private bool _isClearingSensitive;
    private long _projectionGeneration;
    private CancellationTokenSource? _offlineCts;
    private long _offlineRequestId;
    private CancellationTokenSource? _exposedCts;
    private long _exposedRequestId;

    public ObservableCollection<ReusedPasswordGroup> ReusedGroups { get; } = new SensitiveCollection<ReusedPasswordGroup>();
    public ObservableCollection<WeakFinding> WeakItems { get; } = new SensitiveCollection<WeakFinding>();
    public ObservableCollection<UnsecuredFinding> UnsecuredItems { get; } = new SensitiveCollection<UnsecuredFinding>();
    public ObservableCollection<ExposedFinding> ExposedItems { get; } = new SensitiveCollection<ExposedFinding>();
    public ObservableCollection<SecurityIssueGroup> IssueGroups { get; } = new SensitiveCollection<SecurityIssueGroup>();
    public ObservableCollection<SecurityIssueRow> ResolvedItems { get; } = new SensitiveCollection<SecurityIssueRow>();

    [ObservableProperty]
    public partial string Category { get; set; } = "all";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPriority))]
    public partial SecurityIssueRow? PriorityIssue { get; private set; }

    public string PageTitle => Category switch
    {
        "breach" => "已泄露的密码",
        "reuse" => "重复使用的密码",
        "weak" => "弱密码",
        "http" => "不安全的网址",
        "done" => "本次已处理",
        _ => "安全报告",
    };

    public string CategorySummary => Category switch
    {
        "all" => _overviewIssues.Count == 0
            ? "没有需要处理的问题"
            : $"还有 {_overviewIssues.Count} 项需要处理",
        "done" => $"已处理 {_resolvedIssues.Count} 项",
        _ => $"{_currentIssues.Count(item => item.Category == Category)} 项 · {GetCategoryDescription(Category)}",
    };

    public string LastCheckedText
    {
        get
        {
            if (ShowOfflineLoading || IsCheckingExposed)
                return "正在检查…";
            if (_lastCheckedAt is not { } checkedAt)
                return "尚未检查";

            var elapsed = DateTimeOffset.Now - checkedAt;
            var relative = elapsed.TotalMinutes < 1 ? "刚刚"
                : elapsed.TotalHours < 1 ? $"{Math.Max(1, (int)elapsed.TotalMinutes)} 分钟前"
                : elapsed.TotalDays < 1 ? $"{(int)elapsed.TotalHours} 小时前"
                : $"{(int)elapsed.TotalDays} 天前";
            var status = ExposureState == ExposureCheckState.Failed ? " · 暴露密码检查失败"
                : _exposureSnapshotNeedsRefresh ? " · 暴露密码待复查"
                : !_hasExposureSnapshot ? " · 暴露密码尚未检查"
                : string.Empty;
            return $"上次检查 · {relative}{status}";
        }
    }

    public bool ShowPriority => Category == "all" && PriorityIssue is not null;
    public bool HasVisibleIssues => Category == "done"
        ? ResolvedItems.Count > 0
        : ShowPriority || IssueGroups.Count > 0;
    public bool HasResolvedItems => ResolvedItems.Count > 0;

    public string EmptyMessage => Category switch
    {
        "done" => "这次还没有处理过条目，可以从“概览”里的第一项开始。",
        "breach" when IsCheckingExposed => "正在检查暴露密码…",
        "breach" when ExposureState == ExposureCheckState.Failed => "暴露密码检查失败，请重试。",
        "breach" when _exposureSnapshotNeedsRefresh => "请重新检查暴露密码，确认最近的修改。",
        "breach" when !ExposedChecked => "还没有检查暴露密码。运行检查后才能确认结果。",
        "all" when ShowOfflineLoading => "正在检查密码库…",
        "all" when ShowOfflineError => "安全报告暂时无法完成，请重试。",
        "all" when IsCheckingExposed => "离线检查未发现问题，正在检查暴露密码。",
        "all" when ExposureState == ExposureCheckState.Failed => "离线检查未发现问题，暴露密码检查失败，请重试。",
        "all" when _exposureSnapshotNeedsRefresh => "离线检查未发现问题，请重新检查暴露密码。",
        "all" when !ExposedChecked => "离线检查未发现问题，暴露密码尚未检查。",
        "all" => "所有条目都通过了检查。",
        _ => "这一类没有需要处理的条目。",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCheckingExposed))]
    [NotifyPropertyChangedFor(nameof(ExposedChecked))]
    [NotifyPropertyChangedFor(nameof(ShowExposedCount))]
    [NotifyPropertyChangedFor(nameof(ExposureStatusLabel))]
    [NotifyPropertyChangedFor(nameof(ExposureStatusMessage))]
    [NotifyPropertyChangedFor(nameof(ExposureMetricAccessibleName))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    [NotifyPropertyChangedFor(nameof(OverallSeverity))]
    public partial ExposureCheckState ExposureState { get; set; } = ExposureCheckState.NotStarted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExposedError))]
    public partial string? ExposedError { get; set; }

    public bool IsCheckingExposed => ExposureState == ExposureCheckState.Checking;
    public bool ExposedChecked => ExposureState == ExposureCheckState.Succeeded && !_exposureSnapshotNeedsRefresh;
    public bool ShowExposedCount => _hasExposureSnapshot;
    public bool HasExposedError => !string.IsNullOrEmpty(ExposedError);

    public string ExposureStatusLabel => ExposureState switch
    {
        ExposureCheckState.Checking => "检查中",
        ExposureCheckState.Failed => "检查失败",
        _ when _exposureSnapshotNeedsRefresh => "待复查",
        ExposureCheckState.NotStarted => "未检查",
        _ => string.Empty,
    };

    public string ExposureStatusMessage => ExposureState switch
    {
        ExposureCheckState.Checking => "正在检查暴露密码。",
        ExposureCheckState.Failed => "暴露密码检查失败，请重试。",
        _ when _exposureSnapshotNeedsRefresh => "显示上次暴露密码检查结果，请重新检查以确认最近的修改。",
        ExposureCheckState.Succeeded => "暴露密码检查已完成。",
        _ => "暴露密码检查尚未运行。",
    };

    public string ExposureMetricAccessibleName => ExposureState switch
    {
        ExposureCheckState.Checking => "暴露密码，正在检查",
        ExposureCheckState.Failed => "暴露密码，检查失败",
        _ when _exposureSnapshotNeedsRefresh => $"暴露密码，上次检查 {ExposedCount} 项，待复查",
        ExposureCheckState.Succeeded => $"暴露密码，{ExposedCount} 项",
        _ => "暴露密码，尚未检查",
    };

    public bool HasReused => ReusedCount > 0;
    public bool HasWeak => WeakCount > 0;
    public bool HasUnsecured => UnsecuredCount > 0;
    public bool HasExposed => ExposedCount > 0;
    public int ReusedCount => _offlineSnapshot?.Reused.Count ?? 0;
    public int WeakCount => _offlineSnapshot?.Weak.Count ?? 0;
    public int UnsecuredCount => _offlineSnapshot?.Unsecured.Count ?? 0;
    public int ExposedCount => _exposureSnapshot.Count;
    public int TotalIssueCount => ReusedCount + WeakCount + UnsecuredCount + ExposedCount;
    public int OverviewIssueCount => _overviewIssues.Count;
    public int ResolvedCount => _resolvedIssues.Count;

    /// <summary>Returns item counts for persistent navigation, independent of the selected report projection.</summary>
    public int GetCategoryIssueCount(string category) => category switch
    {
        "all" => OverviewIssueCount,
        "done" => ResolvedCount,
        _ => _currentIssues.Count(item => item.Category == category),
    };
    public bool HasAnyIssue => TotalIssueCount > 0;

    public string SummaryText
    {
        get
        {
            if (ExposedChecked)
                return TotalIssueCount == 0 ? "未发现问题" : $"发现 {TotalIssueCount} 项可改进";
            var offlineSummary = TotalIssueCount == 0 ? "离线检查未发现问题" : $"发现 {TotalIssueCount} 项可改进";
            var exposureSummary = ExposureState switch
            {
                ExposureCheckState.Checking => "正在检查暴露密码",
                ExposureCheckState.Failed => "暴露检查失败",
                _ when _exposureSnapshotNeedsRefresh => "暴露检查待复查",
                _ => "暴露检查尚未运行",
            };
            return $"{offlineSummary}，{exposureSummary}";
        }
    }

    public SecuritySeverity OverallSeverity =>
        (WeakCount > 0 || ExposedCount > 0) ? SecuritySeverity.Critical :
        (ReusedCount > 0 || UnsecuredCount > 0) ? SecuritySeverity.Caution :
        ExposedChecked ? SecuritySeverity.Good : SecuritySeverity.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOfflineLoading))]
    [NotifyPropertyChangedFor(nameof(ShowOfflineError))]
    [NotifyPropertyChangedFor(nameof(ShowOfflineContent))]
    [NotifyPropertyChangedFor(nameof(LastCheckedText))]
    [NotifyPropertyChangedFor(nameof(EmptyMessage))]
    public partial DataPageState OfflineState { get; set; } = DataPageState.Loading;

    [ObservableProperty]
    public partial int OfflineProgress { get; set; }

    [ObservableProperty]
    public partial string? OfflineError { get; set; }

    public bool ShowOfflineLoading => OfflineState == DataPageState.Loading;
    public bool ShowOfflineError => OfflineState == DataPageState.Error;
    public bool ShowOfflineContent => OfflineState is DataPageState.Empty or DataPageState.Content;

    public SecurityReportViewModel(IVaultHealthUiService service)
    {
        _service = service;
        ReusedGroups.CollectionChanged += (_, _) => SourcesChanged();
        WeakItems.CollectionChanged += (_, _) => SourcesChanged();
        UnsecuredItems.CollectionChanged += (_, _) => SourcesChanged();
        ExposedItems.CollectionChanged += (_, _) => SourcesChanged();
    }

    partial void OnCategoryChanged(string value)
    {
        if (value is not ("all" or "breach" or "reuse" or "weak" or "http" or "done"))
        {
            Category = "all";
            return;
        }
        if (_sourceUpdateDepth == 0)
            RebuildProjection();
    }

    partial void OnExposureStateChanged(ExposureCheckState value) => SourcesChanged();

    private void SourcesChanged()
    {
        if (_sourceUpdateDepth > 0)
            return;
        RebuildProjection();
        NotifyAggregates();
    }

    private void NotifyAggregates()
    {
        foreach (var property in new[]
        {
            nameof(HasReused), nameof(HasWeak), nameof(HasUnsecured), nameof(HasExposed),
            nameof(ReusedCount), nameof(WeakCount), nameof(UnsecuredCount), nameof(ExposedCount),
            nameof(TotalIssueCount), nameof(HasAnyIssue), nameof(SummaryText), nameof(OverallSeverity),
            nameof(ExposedChecked), nameof(ShowExposedCount), nameof(ExposureStatusLabel),
            nameof(ExposureStatusMessage), nameof(ExposureMetricAccessibleName),
        })
            OnPropertyChanged(property);
    }

    /// <summary>Loads a latest-wins offline snapshot without starting an online check.</summary>
    public async Task<UiOperationResult<HealthReport>> LoadOfflineAsync(CancellationToken ct = default)
    {
        if (_isClearingSensitive)
            return UiOperationResult<HealthReport>.Cancelled();
        CancelOfflineAnalysis();
        CancelExposedCheck();
        var requestId = Interlocked.Increment(ref _offlineRequestId);
        var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _offlineCts = requestCts;
        var token = requestCts.Token;
        OfflineState = DataPageState.Loading;
        OfflineProgress = 0;
        OfflineError = null;
        var progress = new Progress<HealthAnalysisProgress>(value =>
        {
            if (IsCurrentOfflineRequest(requestId, token) && ShowOfflineLoading)
                OfflineProgress = value.Completed;
        });

        try
        {
            var report = await _service.AnalyzeOfflineAsync(progress, token);
            if (!IsCurrentOfflineRequest(requestId, token) || !ApplyOfflineReport(report, requestId, token))
                return UiOperationResult<HealthReport>.Cancelled();

            if (!IsCurrentOfflineRequest(requestId, token))
                return UiOperationResult<HealthReport>.Cancelled();
            OfflineProgress = 100;
            if (!IsCurrentOfflineRequest(requestId, token))
                return UiOperationResult<HealthReport>.Cancelled();
            OfflineState = report.Reused.Count + report.Weak.Count + report.Unsecured.Count == 0
                ? DataPageState.Empty
                : DataPageState.Content;
            return IsCurrentOfflineRequest(requestId, token)
                ? UiOperationResult<HealthReport>.Success(report)
                : UiOperationResult<HealthReport>.Cancelled();
        }
        catch (OperationCanceledException ex) when (UiExceptionPolicy.IsGenuineCancellation(ex, token))
        {
            return UiOperationResult<HealthReport>.Cancelled();
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            return SetOfflineFailure(requestId, token, UiExceptionPolicy.RequestTimeoutMessage);
        }
        catch (Exception)
        {
            return SetOfflineFailure(requestId, token, OfflineAnalysisFailureMessage);
        }
        finally
        {
            if (ReferenceEquals(_offlineCts, requestCts))
                _offlineCts = null;
            requestCts.Dispose();
        }
    }

    /// <summary>Refreshes exposure only after the user has requested it during this session.</summary>
    public async Task<UiOperationResult<HealthReport>> RefreshReportsAsync(CancellationToken ct = default)
    {
        var refreshExposure = _hasRequestedExposureCheck;
        var offlineTask = LoadOfflineAsync(ct);
        var requestId = Volatile.Read(ref _offlineRequestId);
        var result = await offlineTask;
        if (result.IsSuccess && refreshExposure && IsCurrentOfflineRequest(requestId, ct))
            await RunExposedCheck(ct);
        return result;
    }

    private UiOperationResult<HealthReport> SetOfflineFailure(long requestId, CancellationToken token, string message)
    {
        if (!IsCurrentOfflineRequest(requestId, token))
            return UiOperationResult<HealthReport>.Cancelled();
        OfflineError = message;
        if (IsCurrentOfflineRequest(requestId, token))
            OfflineState = DataPageState.Error;
        return UiOperationResult<HealthReport>.Failure(message);
    }

    private bool IsCurrentOfflineRequest(long requestId, CancellationToken token) =>
        requestId == Volatile.Read(ref _offlineRequestId) && !token.IsCancellationRequested;

    private bool IsCurrentExposureRequest(long requestId, CancellationToken token) =>
        requestId == Volatile.Read(ref _exposedRequestId) && !token.IsCancellationRequested;

    private bool ApplyOfflineReport(HealthReport report, long requestId, CancellationToken token)
    {
        var snapshot = new HealthReport(
            report.Reused.Select(group => new ReusedGroup(group.Count, group.Items.ToArray())).ToArray(),
            report.Weak.ToArray(), report.Unsecured.ToArray());
        if (!IsCurrentOfflineRequest(requestId, token))
            return false;

        _sourceUpdateDepth++;
        try
        {
            // Commit the complete snapshot before publishing collection notifications.
            _offlineSnapshot = snapshot;
            var revision = Interlocked.Increment(ref _snapshotRevision);
            _exposureSnapshotNeedsRefresh = _hasExposureSnapshot;
            _lastCheckedAt = DateTimeOffset.Now;
            if (!_offlineBaselineCaptured)
            {
                CaptureBaseline(CreateIssueRows().Where(item => item.Category != "breach"));
                _offlineBaselineCaptured = true;
            }
            SynchronizeSourceCollections(revision);
            return true;
        }
        finally
        {
            _sourceUpdateDepth--;
            SourcesChanged();
        }
    }

    [RelayCommand]
    private Task RetryOfflineAsync() => RefreshReportsAsync();

    /// <summary>Invalidates callbacks before cancelling the active offline request.</summary>
    public void CancelOfflineAnalysis()
    {
        Interlocked.Increment(ref _offlineRequestId);
        var requestCts = _offlineCts;
        _offlineCts = null;
        requestCts?.Cancel();
        requestCts?.Dispose();
    }

    /// <summary>Checks exposure while retaining the previous committed result until success.</summary>
    [RelayCommand]
    private async Task RunExposedCheck(CancellationToken ct)
    {
        if (_isClearingSensitive)
            return;
        CancelExposedCheck();
        var requestId = Interlocked.Increment(ref _exposedRequestId);
        var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _exposedCts = requestCts;
        var token = requestCts.Token;
        _hasRequestedExposureCheck = true;
        _exposureSnapshotNeedsRefresh = _hasExposureSnapshot;
        ExposedError = null;
        ExposureState = ExposureCheckState.Checking;

        try
        {
            var findings = await _service.CheckExposedAsync(token);
            if (!IsCurrentExposureRequest(requestId, token))
            {
                RestoreExposureStateAfterCancellation(requestId);
                return;
            }
            var snapshot = findings.ToArray();
            if (!IsCurrentExposureRequest(requestId, token))
            {
                RestoreExposureStateAfterCancellation(requestId);
                return;
            }

            _sourceUpdateDepth++;
            try
            {
                _exposureSnapshot = snapshot;
                var revision = Interlocked.Increment(ref _snapshotRevision);
                _hasExposureSnapshot = true;
                _exposureSnapshotNeedsRefresh = false;
                _pendingExposureRechecks.Clear();
                _lastCheckedAt = DateTimeOffset.Now;
                if (!_exposureBaselineCaptured)
                {
                    CaptureBaseline(CreateIssueRows().Where(item => item.Category == "breach"));
                    _exposureBaselineCaptured = true;
                }
                ExposureState = ExposureCheckState.Succeeded;
                SynchronizeSourceCollections(revision);
            }
            finally
            {
                _sourceUpdateDepth--;
                SourcesChanged();
            }
        }
        catch (OperationCanceledException ex) when (UiExceptionPolicy.IsGenuineCancellation(ex, token))
        {
            RestoreExposureStateAfterCancellation(requestId);
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            SetExposureFailure(requestId, token, UiExceptionPolicy.RequestTimeoutMessage);
        }
        catch (Exception)
        {
            SetExposureFailure(requestId, token, ExposureCheckFailureMessage);
        }
        finally
        {
            if (ReferenceEquals(_exposedCts, requestCts))
                _exposedCts = null;
            requestCts.Dispose();
        }
    }

    private void SetExposureFailure(long requestId, CancellationToken token, string message)
    {
        if (!IsCurrentExposureRequest(requestId, token))
        {
            RestoreExposureStateAfterCancellation(requestId);
            return;
        }
        ExposedError = message;
        if (IsCurrentExposureRequest(requestId, token))
            ExposureState = ExposureCheckState.Failed;
    }

    private void RestoreExposureStateAfterCancellation(long requestId)
    {
        if (requestId == Volatile.Read(ref _exposedRequestId))
            ExposureState = _hasExposureSnapshot ? ExposureCheckState.Succeeded : ExposureCheckState.NotStarted;
    }

    /// <summary>Cancels pending work without discarding completed exposure findings.</summary>
    public void CancelExposedCheck()
    {
        var requestId = Interlocked.Increment(ref _exposedRequestId);
        var requestCts = _exposedCts;
        _exposedCts = null;
        requestCts?.Cancel();
        requestCts?.Dispose();
        if (requestId != Volatile.Read(ref _exposedRequestId))
            return;
        ExposedError = null;
        RestoreExposureStateAfterCancellation(requestId);
    }

    /// <summary>Invalidates findings for a password that has been successfully replaced in the vault.</summary>
    public void MarkPasswordChanged(string cipherId)
    {
        if (_isClearingSensitive || string.IsNullOrWhiteSpace(cipherId))
            return;

        _sourceUpdateDepth++;
        try
        {
            var needsExposureRecheck = _hasRequestedExposureCheck || _hasExposureSnapshot;
            if (needsExposureRecheck)
                _pendingExposureRechecks.Add(cipherId);
            _exposureSnapshot = _exposureSnapshot
                .Where(finding => !string.Equals(finding.Item.CipherId, cipherId, StringComparison.Ordinal))
                .ToArray();
            var revision = Interlocked.Increment(ref _snapshotRevision);
            _exposureSnapshotNeedsRefresh = needsExposureRecheck;
            CancelExposedCheck();
            ReplaceItems(ExposedItems, _exposureSnapshot,
                () => revision == Volatile.Read(ref _snapshotRevision));
        }
        finally
        {
            _sourceUpdateDepth--;
            SourcesChanged();
        }
    }

    private void CaptureBaseline(IEnumerable<SecurityIssueRow> items)
    {
        foreach (var item in items)
            _baselineIssues.TryAdd((item.Category, item.CipherId), item);
    }

    private List<SecurityIssueRow> CreateIssueRows()
    {
        var rows = new List<(SecurityIssueRow Row, int BreachCount)>();
        foreach (var finding in _exposureSnapshot)
            rows.Add((CreateRow(finding.Item, "breach", $"密码出现在 {finding.BreachCount} 次已知泄露中"), finding.BreachCount));
        foreach (var group in _offlineSnapshot?.Reused ?? Array.Empty<ReusedGroup>())
        {
            foreach (var item in group.Items)
            {
                var other = group.Items.Where(candidate => candidate.CipherId != item.CipherId)
                    .OrderBy(candidate => candidate.Name, NameComparer).FirstOrDefault();
                var reason = other is null ? "与其他条目使用相同的密码" : $"与“{other.Name}”用的是同一个密码";
                rows.Add((CreateRow(item, "reuse", reason), 0));
            }
        }
        foreach (var finding in _offlineSnapshot?.Weak ?? Array.Empty<WeakFinding>())
            rows.Add((CreateRow(finding.Item, "weak", "密码过于简单或容易被猜到"), 0));
        foreach (var finding in _offlineSnapshot?.Unsecured ?? Array.Empty<UnsecuredFinding>())
            rows.Add((CreateRow(finding.Item, "http", "登录网址使用未加密的 HTTP 连接"), 0));

        return rows.OrderBy(item => Array.IndexOf(IssueCategories, item.Row.Category))
            .ThenByDescending(item => item.BreachCount)
            .ThenBy(item => item.Row.Name, NameComparer)
            .ThenBy(item => item.Row.CipherId, StringComparer.Ordinal)
            .DistinctBy(item => (item.Row.Category, item.Row.CipherId))
            .Select(item => item.Row).ToList();
    }

    private static SecurityIssueRow CreateRow(HealthItemRef item, string category, string reason)
    {
        var name = string.IsNullOrWhiteSpace(item.Name) ? "未命名条目" : item.Name;
        var username = item.Username ?? string.Empty;
        var description = GetCategoryDescription(category);
        return new SecurityIssueRow(item.CipherId, name, username,
            StringInfo.GetNextTextElement(name.Trim()).ToUpperInvariant(), reason,
            string.IsNullOrWhiteSpace(username) ? description : $"{description} · {username}",
            category, category == "http" ? "修改网址" : "更换密码", string.Empty,
            category == "breach" ? "critical" : "warning");
    }

    private void RebuildProjection()
    {
        var generation = Interlocked.Increment(ref _projectionGeneration);
        bool IsCurrent() => generation == Volatile.Read(ref _projectionGeneration);
        _currentIssues = CreateIssueRows();
        _overviewIssues = _currentIssues.DistinctBy(item => item.CipherId).ToList();
        var currentIds = _overviewIssues.Select(item => item.CipherId).ToHashSet(StringComparer.Ordinal);

        // Only a successful check can clear its own baseline category. Unknown exposure is never a clean result.
        _resolvedIssues = _offlineBaselineCaptured
            ? _baselineIssues.Values.GroupBy(item => item.CipherId)
                .Where(group => !currentIds.Contains(group.Key)
                    && !_pendingExposureRechecks.Contains(group.Key)
                    && group.All(item => item.Category != "breach" || ExposedChecked))
                .Select(group => group.OrderBy(item => Array.IndexOf(IssueCategories, item.Category)).First())
                .OrderBy(item => item.Name, NameComparer)
                .ThenBy(item => item.CipherId, StringComparer.Ordinal)
                .Select(item => item with { ResultLabel = GetResultLabel(item.Category), Severity = "neutral" })
                .ToList()
            : [];

        PriorityIssue = Category == "all" ? _overviewIssues.FirstOrDefault() : null;
        if (!IsCurrent())
            return;
        var visibleRows = Category == "all" ? _overviewIssues.Skip(1)
            : Category == "done" ? Enumerable.Empty<SecurityIssueRow>()
            : _currentIssues.Where(item => item.Category == Category);
        var groups = visibleRows.GroupBy(item => item.Category)
            .Select(group => new SecurityIssueGroup(group.Key, GetCategoryTitle(group.Key),
                GetCategoryDescription(group.Key), group)).ToList();
        if (!ReplaceItems(IssueGroups, groups, IsCurrent)
            || !ReplaceItems(ResolvedItems, Category is "all" or "done" ? _resolvedIssues : [], IsCurrent))
            return;

        foreach (var property in new[]
        {
            nameof(PageTitle), nameof(CategorySummary), nameof(LastCheckedText), nameof(ShowPriority),
            nameof(HasVisibleIssues), nameof(HasResolvedItems), nameof(EmptyMessage),
            nameof(OverviewIssueCount), nameof(ResolvedCount),
        })
        {
            if (!IsCurrent())
                return;
            OnPropertyChanged(property);
        }
    }

    private static bool ReplaceItems<T>(ObservableCollection<T> target, IEnumerable<T> items, Func<bool> isCurrent)
    {
        var snapshot = items.ToArray();
        if (!isCurrent())
            return false;
        if (target.Count > 0)
            target.Clear();
        foreach (var item in snapshot)
        {
            if (!isCurrent())
                return false;
            target.Add(item);
        }
        return isCurrent();
    }

    private void SynchronizeSourceCollections(long revision)
    {
        bool IsCurrent() => revision == Volatile.Read(ref _snapshotRevision);
        var offlineSnapshot = _offlineSnapshot;
        var exposureSnapshot = _exposureSnapshot;
        if (!ReplaceItems(ReusedGroups,
                (offlineSnapshot?.Reused ?? Array.Empty<ReusedGroup>()).Select(group => new ReusedPasswordGroup(group)), IsCurrent)
            || !ReplaceItems(WeakItems, offlineSnapshot?.Weak ?? Array.Empty<WeakFinding>(), IsCurrent)
            || !ReplaceItems(UnsecuredItems, offlineSnapshot?.Unsecured ?? Array.Empty<UnsecuredFinding>(), IsCurrent))
            return;
        ReplaceItems(ExposedItems, exposureSnapshot, IsCurrent);
    }

    private static string GetCategoryTitle(string category) => category switch
    {
        "breach" => "已泄露的密码",
        "reuse" => "重复使用的密码",
        "weak" => "弱密码",
        "http" => "不安全的网址",
        _ => "安全报告",
    };

    private static string GetCategoryDescription(string category) => category switch
    {
        "breach" => "出现在公开的数据泄露中，应尽快修改",
        "reuse" => "一处泄露就会波及其他账户",
        "weak" => "太短或容易被猜到",
        "http" => "未加密的连接可能泄露登录信息",
        _ => string.Empty,
    };

    // Findings do not reveal whether an item was edited, archived, or deleted.
    private static string GetResultLabel(string category) => category switch
    {
        "breach" => "不再出现在暴露密码结果中",
        "reuse" => "不再与其他条目共用密码",
        "weak" => "不再出现在弱密码结果中",
        "http" => "不再使用未加密的登录网址",
        _ => "不再有已知问题",
    };

    private sealed class SensitiveCollection<T> : ObservableCollection<T>
    {
        public void ClearSensitiveItems()
        {
            if (Count == 0)
                return;

            // Lock may run inside a collection notification; clear before publishing the reset.
            Items.Clear();
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    private static void ClearSensitiveCollection<T>(ObservableCollection<T> collection) =>
        ((SensitiveCollection<T>)collection).ClearSensitiveItems();

    /// <summary>Clears findings, session baselines, and late callbacks on lock or sign-out.</summary>
    public void ClearSensitive()
    {
        if (_isClearingSensitive)
            return;
        _isClearingSensitive = true;
        Interlocked.Increment(ref _projectionGeneration);
        Interlocked.Increment(ref _snapshotRevision);
        _sourceUpdateDepth++;
        try
        {
            _offlineSnapshot = null;
            _exposureSnapshot = [];
            _baselineIssues.Clear();
            _pendingExposureRechecks.Clear();
            _currentIssues = [];
            _overviewIssues = [];
            _resolvedIssues = [];
            _offlineBaselineCaptured = false;
            _exposureBaselineCaptured = false;
            _hasRequestedExposureCheck = false;
            _hasExposureSnapshot = false;
            _exposureSnapshotNeedsRefresh = false;
            _lastCheckedAt = null;
            CancelOfflineAnalysis();
            CancelExposedCheck();
            PriorityIssue = null;
            ClearSensitiveCollection(IssueGroups);
            ClearSensitiveCollection(ResolvedItems);
            ClearSensitiveCollection(ReusedGroups);
            ClearSensitiveCollection(WeakItems);
            ClearSensitiveCollection(UnsecuredItems);
            ClearSensitiveCollection(ExposedItems);
            ExposureState = ExposureCheckState.NotStarted;
            ExposedError = null;
            OfflineError = null;
            OfflineProgress = 0;
            OfflineState = DataPageState.Loading;
            Category = "all";
        }
        finally
        {
            _sourceUpdateDepth--;
            try
            {
                SourcesChanged();
            }
            finally
            {
                _isClearingSensitive = false;
            }
        }
    }
}
