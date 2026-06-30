using System.Collections.ObjectModel;
using App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Models;

namespace App.ViewModels;

public partial class SecurityReportViewModel : ObservableObject
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

    // ── 构造 ────────────────────────────────────────────────────────────────────

    public SecurityReportViewModel(IVaultHealthUiService service)
    {
        _service = service;

        ReusedGroups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasReused));
        WeakItems.CollectionChanged    += (_, _) => OnPropertyChanged(nameof(HasWeak));
        UnsecuredItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasUnsecured));
        ExposedItems.CollectionChanged  += (_, _) => OnPropertyChanged(nameof(HasExposed));
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
}
