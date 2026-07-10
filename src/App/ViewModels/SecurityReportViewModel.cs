using System.Collections.ObjectModel;
using App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Models;

namespace App.ViewModels;

/// <summary>密码库安全体检的整体严重程度，用于 Hero 卡片配色/图标。</summary>
public enum SecuritySeverity
{
    Good,
    Caution,
    Critical
}

public partial class SecurityReportViewModel : ObservableObject, ISensitiveState
{
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

    /// <summary>泄露检查正在进行中。</summary>
    [ObservableProperty]
    public partial bool IsCheckingExposed { get; set; }

    /// <summary>泄露检查错误信息；非空时表示检查失败。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExposedError))]
    public partial string? ExposedError { get; set; }

    /// <summary>泄露检查已完成（且成功）。</summary>
    [ObservableProperty]
    public partial bool ExposedChecked { get; set; }

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
    public string SummaryText => TotalIssueCount > 0 ? $"发现 {TotalIssueCount} 项可改进" : "未发现问题";

    /// <summary>整体严重程度：弱密码/已暴露 视为 Critical；仅重复/不安全 URL 视为 Caution；均无则 Good。</summary>
    public SecuritySeverity OverallSeverity =>
        (WeakCount > 0 || ExposedCount > 0) ? SecuritySeverity.Critical :
        (ReusedCount > 0 || UnsecuredCount > 0) ? SecuritySeverity.Caution :
        SecuritySeverity.Good;

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
    }

    // ── 离线分析 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 加载离线健康报告，填充 ReusedGroups / WeakItems / UnsecuredItems。
    /// 同步方法，可在页面 OnNavigatedTo 中直接调用。
    /// </summary>
    public void LoadOffline()
    {
        var report = _service.AnalyzeOffline();

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

    // ── 泄露检查命令 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 联网检查密码是否出现在已知数据泄露中（Have I Been Pwned）。
    /// 完成后填充 ExposedItems，并将 ExposedChecked 设为 true；
    /// 失败时将错误信息写入 ExposedError。
    /// </summary>
    [RelayCommand]
    private async Task RunExposedCheck(CancellationToken ct)
    {
        IsCheckingExposed = true;
        ExposedError = null;
        ExposedChecked = false;
        ExposedItems.Clear();

        try
        {
            var findings = await _service.CheckExposedAsync(ct);

            foreach (var f in findings)
                ExposedItems.Add(f);

            ExposedChecked = true;
        }
        catch (OperationCanceledException)
        {
            // 用户取消，静默处理
        }
        catch (Exception ex)
        {
            ExposedError = ex.Message;
        }
        finally
        {
            IsCheckingExposed = false;
        }
    }

    // 锁屏/登出时清空安全报告的机密足迹:重复/弱/不安全/已泄露分组与错误。幂等。
    public void ClearSensitive()
    {
        ReusedGroups.Clear();
        WeakItems.Clear();
        UnsecuredItems.Clear();
        ExposedItems.Clear();
        ExposedError = null;
    }
}
