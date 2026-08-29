using System.Collections.ObjectModel;
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

/// <summary>Describes the user-visible lifecycle of the online exposure check.</summary>
public enum ExposureCheckState
{
    NotStarted,
    Checking,
    Succeeded,
    Failed
}

public partial class SecurityReportViewModel : ObservableObject, ISensitiveState
{
    private const string OfflineAnalysisFailureMessage = "无法完成安全报告分析，请稍后重试。";
    private const string ExposureCheckFailureMessage = "无法完成暴露密码检查，请稍后重试。";
    private readonly IVaultHealthUiService _service;

    // ── 离线分析集合 ────────────────────────────────────────────────────────────

    /// <summary>复用密码分组（每组包含使用相同密码的条目列表）。</summary>
    public ObservableCollection<ReusedGroup> ReusedGroups { get; } = new();

    /// <summary>弱密码条目列表（附强度评分）。</summary>
    public ObservableCollection<WeakFinding> WeakItems { get; } = new();

    /// <summary>未加密 URL 条目列表（http:// 而非 https://）。</summary>
    public ObservableCollection<UnsecuredFinding> UnsecuredItems { get; } = new();

    // ── 泄露检查集合 ────────────────────────────────────────────────────────────

    /// <summary>在已知数据泄露中出现的条目列表（需联网检查）。</summary>
    public ObservableCollection<ExposedFinding> ExposedItems { get; } = new();

    // ── 泄露检查状态 ────────────────────────────────────────────────────────────

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

    public bool IsCheckingExposed => ExposureState == ExposureCheckState.Checking;

    /// <summary>泄露检查错误信息；非空时表示检查失败。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExposedError))]
    public partial string? ExposedError { get; set; }

    public bool ExposedChecked => ExposureState == ExposureCheckState.Succeeded;

    public bool ShowExposedCount => ExposedChecked;

    public string ExposureStatusLabel => ExposureState switch
    {
        ExposureCheckState.NotStarted => "未检查",
        ExposureCheckState.Checking => "检查中",
        ExposureCheckState.Failed => "检查失败",
        _ => string.Empty,
    };

    public string ExposureStatusMessage => ExposureState switch
    {
        ExposureCheckState.NotStarted => "暴露密码检查尚未运行。",
        ExposureCheckState.Checking => "正在检查暴露密码。",
        ExposureCheckState.Succeeded => "暴露密码检查已完成。",
        ExposureCheckState.Failed => "暴露密码检查失败，请重试。",
        _ => string.Empty,
    };

    public string ExposureMetricAccessibleName => ExposureState switch
    {
        ExposureCheckState.NotStarted => "暴露密码，尚未检查",
        ExposureCheckState.Checking => "暴露密码，正在检查",
        ExposureCheckState.Succeeded => $"暴露密码，{ExposedCount} 项",
        ExposureCheckState.Failed => "暴露密码，检查失败",
        _ => "暴露密码",
    };

    public bool HasExposedError => !string.IsNullOrEmpty(ExposedError);

    // ── 空态可见性辅助属性 ──────────────────────────────────────────────────────

    /// <summary>存在重复密码分组时为 true（用于控制"未发现"空态 TextBlock 隐藏）。</summary>
    public bool HasReused => ReusedGroups.Count > 0;

    /// <summary>存在弱密码条目时为 true。</summary>
    public bool HasWeak => WeakItems.Count > 0;

    /// <summary>存在不安全 HTTP 条目时为 true。</summary>
    public bool HasUnsecured => UnsecuredItems.Count > 0;

    /// <summary>存在已暴露密码条目时为 true。</summary>
    public bool HasExposed => ExposedItems.Count > 0;

    // ── 聚合摘要属性（Hero 卡片） ──────────────────────────────────────────────

    /// <summary>重复密码分组数。</summary>
    public int ReusedCount => ReusedGroups.Count;

    /// <summary>弱密码条目数。</summary>
    public int WeakCount => WeakItems.Count;

    /// <summary>不安全 HTTP 条目数。</summary>
    public int UnsecuredCount => UnsecuredItems.Count;

    /// <summary>已暴露密码条目数。</summary>
    public int ExposedCount => ExposedItems.Count;

    /// <summary>四类问题的总数。</summary>
    public int TotalIssueCount => ReusedCount + WeakCount + UnsecuredCount + ExposedCount;

    /// <summary>存在任意一类问题时为 true。</summary>
    public bool HasAnyIssue => TotalIssueCount > 0;

    /// <summary>Hero 卡片摘要文案。</summary>
    public string SummaryText => ExposureState switch
    {
        ExposureCheckState.Succeeded when TotalIssueCount == 0 => "未发现问题",
        ExposureCheckState.Succeeded => $"发现 {TotalIssueCount} 项可改进",
        ExposureCheckState.NotStarted when TotalIssueCount == 0 => "离线检查未发现问题，暴露检查尚未运行",
        ExposureCheckState.NotStarted => $"发现 {TotalIssueCount} 项可改进，暴露检查尚未运行",
        ExposureCheckState.Checking when TotalIssueCount == 0 => "离线检查未发现问题，正在检查暴露密码",
        ExposureCheckState.Checking => $"发现 {TotalIssueCount} 项可改进，正在检查暴露密码",
        ExposureCheckState.Failed when TotalIssueCount == 0 => "离线检查未发现问题，暴露检查失败",
        _ => $"发现 {TotalIssueCount} 项可改进，暴露检查失败",
    };

    /// <summary>Returns a neutral severity until every required check has completed successfully.</summary>
    public SecuritySeverity OverallSeverity =>
        (WeakCount > 0 || ExposedCount > 0) ? SecuritySeverity.Critical :
        (ReusedCount > 0 || UnsecuredCount > 0) ? SecuritySeverity.Caution :
        ExposureState == ExposureCheckState.Succeeded ? SecuritySeverity.Good :
        SecuritySeverity.Unknown;

    // ── 离线分析状态机 ──────────────────────────────────────────────────────────

    private CancellationTokenSource? _offlineCts;
    private long _offlineRequestId;

    /// <summary>离线分析页面状态:Loading/Error/Empty/Content。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOfflineLoading))]
    [NotifyPropertyChangedFor(nameof(ShowOfflineError))]
    [NotifyPropertyChangedFor(nameof(ShowOfflineContent))]
    public partial DataPageState OfflineState { get; set; } = DataPageState.Loading;

    /// <summary>离线分析进度百分比(0-100)。</summary>
    [ObservableProperty]
    public partial int OfflineProgress { get; set; }

    /// <summary>离线分析错误信息;非空时表示上次分析失败。</summary>
    [ObservableProperty]
    public partial string? OfflineError { get; set; }

    public bool ShowOfflineLoading => OfflineState == DataPageState.Loading;
    public bool ShowOfflineError => OfflineState == DataPageState.Error;
    public bool ShowOfflineContent => OfflineState is DataPageState.Empty or DataPageState.Content;

    // ── 构造 ────────────────────────────────────────────────────────────────────

    public SecurityReportViewModel(IVaultHealthUiService service)
    {
        _service = service;

        ReusedGroups.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasReused)); NotifyAggregates(); };
        WeakItems.CollectionChanged    += (_, _) => { OnPropertyChanged(nameof(HasWeak)); NotifyAggregates(); };
        UnsecuredItems.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasUnsecured)); NotifyAggregates(); };
        ExposedItems.CollectionChanged  += (_, _) => { OnPropertyChanged(nameof(HasExposed)); NotifyAggregates(); };
    }

    private void NotifyAggregates()
    {
        OnPropertyChanged(nameof(ReusedCount)); OnPropertyChanged(nameof(WeakCount));
        OnPropertyChanged(nameof(UnsecuredCount)); OnPropertyChanged(nameof(ExposedCount));
        OnPropertyChanged(nameof(TotalIssueCount)); OnPropertyChanged(nameof(HasAnyIssue));
        OnPropertyChanged(nameof(SummaryText)); OnPropertyChanged(nameof(OverallSeverity));
        OnPropertyChanged(nameof(ExposureMetricAccessibleName));
    }

    // ── 离线分析 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 异步加载离线健康报告,填充 ReusedGroups / WeakItems / UnsecuredItems。
    /// 在 <see cref="IVaultHealthUiService.AnalyzeOfflineAsync"/> 内部,CPU 密集分析
    /// 已被送入后台线程,本方法不阻塞 UI。每次调用都会取消上一次未完成的请求(latest-wins),
    /// 并用递增的 request-id 校验:迟到完成的旧请求绝不会覆盖更新的结果或已取消的状态。
    /// </summary>
    public async Task<UiOperationResult<HealthReport>> LoadOfflineAsync(
        CancellationToken ct = default)
    {
        CancelOfflineAnalysis();
        var requestId = Interlocked.Increment(ref _offlineRequestId);
        _offlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _offlineCts.Token;
        OfflineState = DataPageState.Loading;
        OfflineProgress = 0;
        OfflineError = null;

        var progress = new Progress<HealthAnalysisProgress>(
            value =>
            {
                if (requestId == Volatile.Read(ref _offlineRequestId))
                    OfflineProgress = value.Completed;
            });

        try
        {
            var report = await _service.AnalyzeOfflineAsync(progress, token);
            if (requestId != Volatile.Read(ref _offlineRequestId))
                return UiOperationResult<HealthReport>.Cancelled();

            ApplyOfflineReport(report);
            OfflineProgress = 100;
            OfflineState = report.Reused.Count + report.Weak.Count + report.Unsecured.Count == 0
                ? DataPageState.Empty
                : DataPageState.Content;
            return UiOperationResult<HealthReport>.Success(report);
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, token))
        {
            if (requestId != Volatile.Read(ref _offlineRequestId))
                return UiOperationResult<HealthReport>.Cancelled();
            return UiOperationResult<HealthReport>.Cancelled();
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            if (requestId == Volatile.Read(ref _offlineRequestId))
            {
                OfflineError = UiExceptionPolicy.RequestTimeoutMessage;
                OfflineState = DataPageState.Error;
            }
            return UiOperationResult<HealthReport>.Failure(
                UiExceptionPolicy.RequestTimeoutMessage);
        }
        catch (VaultHealthUiException)
        {
            if (requestId == Volatile.Read(ref _offlineRequestId))
            {
                OfflineError = OfflineAnalysisFailureMessage;
                OfflineState = DataPageState.Error;
            }
            return UiOperationResult<HealthReport>.Failure(OfflineAnalysisFailureMessage);
        }
        catch (Exception)
        {
            if (requestId == Volatile.Read(ref _offlineRequestId))
            {
                OfflineError = OfflineAnalysisFailureMessage;
                OfflineState = DataPageState.Error;
            }
            return UiOperationResult<HealthReport>.Failure(OfflineAnalysisFailureMessage);
        }
    }

    private void ApplyOfflineReport(HealthReport report)
    {
        ReusedGroups.Clear();
        foreach (var g in report.Reused)
            ReusedGroups.Add(g);

        WeakItems.Clear();
        foreach (var w in report.Weak)
            WeakItems.Add(w);

        UnsecuredItems.Clear();
        foreach (var u in report.Unsecured)
            UnsecuredItems.Add(u);
    }

    /// <summary>用户点击"重试"时重新发起离线分析。</summary>
    [RelayCommand]
    private Task RetryOfflineAsync() => LoadOfflineAsync();

    /// <summary>
    /// 取消进行中的离线分析(离开页面/再次触发加载/Lock 时调用)。
    /// 先递增 request-id 再取消 token,确保任何仍在飞行中的回调都能立即识别为过期。
    /// </summary>
    public void CancelOfflineAnalysis()
    {
        Interlocked.Increment(ref _offlineRequestId);
        _offlineCts?.Cancel();
        _offlineCts?.Dispose();
        _offlineCts = null;
    }

    // ── 泄露检查命令 ────────────────────────────────────────────────────────────

    // 泄露检查(HIBP)的取消/代次状态机,与离线分析对称:
    // 每次 RunExposedCheck 先取消上一次请求并领取递增的 request-id;结果提交前用 request-id 校验,
    // 迟到完成的旧请求(即使 fake 忽略取消令牌)绝不会写回 ExposedItems/ExposedChecked/ExposedError。
    private CancellationTokenSource? _exposedCts;
    private long _exposedRequestId;

    /// <summary>
    /// 联网检查密码是否出现在已知数据泄露中（Have I Been Pwned）。
    /// 完成后填充 ExposedItems，并将 ExposedChecked 设为 true；
    /// 失败时将错误信息写入 ExposedError。
    /// 入口先取消上一次请求并领取 request-id;每个结果提交前校验 request-id,
    /// Lock/离页/再次触发导致的旧请求即便迟到返回也不会回写机密。
    /// </summary>
    [RelayCommand]
    private async Task RunExposedCheck(CancellationToken ct)
    {
        CancelExposedCheck();
        var requestId = Interlocked.Increment(ref _exposedRequestId);
        _exposedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _exposedCts.Token;

        ExposedError = null;
        ExposedItems.Clear();
        ExposureState = ExposureCheckState.Checking;

        try
        {
            var findings = await _service.CheckExposedAsync(token);
            if (requestId != Volatile.Read(ref _exposedRequestId))
                return;

            foreach (var f in findings)
                ExposedItems.Add(f);

            ExposureState = ExposureCheckState.Succeeded;
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, token))
        {
            if (requestId == Volatile.Read(ref _exposedRequestId))
                ExposureState = ExposureCheckState.NotStarted;
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            if (requestId == Volatile.Read(ref _exposedRequestId))
            {
                ExposedError = UiExceptionPolicy.RequestTimeoutMessage;
                ExposureState = ExposureCheckState.Failed;
            }
        }
        catch (Exception)
        {
            if (requestId == Volatile.Read(ref _exposedRequestId))
            {
                ExposedError = ExposureCheckFailureMessage;
                ExposureState = ExposureCheckState.Failed;
            }
        }
    }

    /// <summary>
    /// 取消进行中的泄露检查(离开页面/再次触发加载/Lock 时调用)。
    /// 先递增 request-id 再取消 token,确保任何仍在飞行中的回调都能立即识别为过期而丢弃结果。
    /// </summary>
    public void CancelExposedCheck()
    {
        Interlocked.Increment(ref _exposedRequestId);
        _exposedCts?.Cancel();
        _exposedCts?.Dispose();
        _exposedCts = null;
        ExposureState = ExposureCheckState.NotStarted;
    }

    // 锁屏/登出时清空安全报告的机密足迹:重复/弱/不安全/已泄露分组与错误。幂等。
    public void ClearSensitive()
    {
        CancelOfflineAnalysis();
        CancelExposedCheck();
        ReusedGroups.Clear();
        WeakItems.Clear();
        UnsecuredItems.Clear();
        ExposedItems.Clear();
        ExposedError = null;
        OfflineError = null;
    }
}
