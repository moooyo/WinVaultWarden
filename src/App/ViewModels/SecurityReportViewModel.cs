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
    public bool IsIgnored { get; init; }
    public bool CanIgnore => !IsIgnored && Category != "breach";
    public string Subtitle => string.IsNullOrWhiteSpace(Username)
        ? Reason
        : $"{Username} · {Reason}";
}

/// <summary>Provides one category of issue rows to the report page.</summary>
public sealed class SecurityIssueGroup(
    string category,
    string title,
    string description,
    IEnumerable<SecurityIssueRow> items,
    bool showHeader = true)
{
    public string Category { get; } = category;
    public string Title { get; } = title;
    public string Description { get; } = description;
    public ObservableCollection<SecurityIssueRow> Items { get; } = new(items);
    public int Count => Items.Count;
    public bool ShowHeader { get; } = showHeader;
}

public partial class SecurityReportViewModel : ObservableObject, ISensitiveState
{
    private static readonly string OfflineAnalysisFailureMessage = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4BB53121C8DC2FB6);
    private static readonly string ExposureCheckFailureMessage = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFD1534F31D1BF516);
    private static readonly string[] IssueCategories = ["breach", "reuse", "weak", "http"];
    private static readonly StringComparer NameComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), ignoreCase: true);
    private readonly IVaultHealthUiService _service;
    private readonly Dictionary<(string Category, string CipherId), SecurityIssueRow> _baselineIssues = new();
    private readonly HashSet<(string Category, string CipherId)> _ignoredIssueKeys = new();
    private readonly HashSet<string> _pendingExposureRechecks = new(StringComparer.Ordinal);
    private HealthReport? _offlineSnapshot;
    private IReadOnlyList<ExposedFinding> _exposureSnapshot = [];
    private long _snapshotRevision;
    private List<SecurityIssueRow> _currentIssues = [];
    private List<SecurityIssueRow> _overviewIssues = [];
    private List<SecurityIssueRow> _resolvedIssues = [];
    private List<SecurityIssueRow> _ignoredRows = [];
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
    public ObservableCollection<HealthItemRef> UnassessedItems { get; } = new SensitiveCollection<HealthItemRef>();
    public ObservableCollection<SecurityIssueGroup> IssueGroups { get; } = new SensitiveCollection<SecurityIssueGroup>();
    public ObservableCollection<SecurityIssueRow> ResolvedItems { get; } = new SensitiveCollection<SecurityIssueRow>();

    [ObservableProperty]
    public partial string Category { get; set; } = "all";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPriority))]
    public partial SecurityIssueRow? PriorityIssue { get; private set; }

    public string PageTitle => Category switch
    {
        "breach" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2A425E41491FBFA8),
        "reuse" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9CAE975C32F18B17),
        "weak" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEAC3BB39F8F0C574),
        "http" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text35CB23E74DACE235),
        "done" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4BB9578CA0B97C1F),
        "ignored" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0D5BE9569D449381),
        _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text89E32ABCA54127DD),
    };

    public string CategorySummary => Category switch
    {
        "all" when HasUnassessed => _overviewIssues.Count == 0
            ? $"No confirmed issues; {UnassessedCount} password strengths were not assessed"
            : $"{_overviewIssues.Count} items need attention; {UnassessedCount} password strengths were not assessed",
        "all" => _overviewIssues.Count == 0
            ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5DEB6E6A719DDF59)
            : global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextED5467E8FFC50B7F,_overviewIssues.Count),
        "done" => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text3324FF0B70C14651,_resolvedIssues.Count),
        "ignored" => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextCBAB2EB145F82A55,_ignoredRows.Count),
        "breach" when !_hasExposureSnapshot => string.Empty,
        _ => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text31ACF8EAFC04C829,_currentIssues.Count(item => item.Category == Category),GetCategoryDescription(Category)),
    };

    public string LastCheckedText
    {
        get
        {
            if (ShowOfflineLoading || IsCheckingExposed)
                return global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6B72C3D6855CB87D);
            if (!_hasExposureSnapshot)
                return ExposureState == ExposureCheckState.Failed ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text336F2D8A36C308A0) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE0A145380D71D93C);
            if (_lastCheckedAt is not { } checkedAt)
                return global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCDBC0434F568FC9E);

            var elapsed = DateTimeOffset.Now - checkedAt;
            var relative = elapsed.TotalMinutes < 1 ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDE6785D99E028971)
                : elapsed.TotalHours < 1 ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text66F93FE5E9067CD8,Math.Max(1, (int)elapsed.TotalMinutes))
                : elapsed.TotalDays < 1 ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text6504A911F9E16273,(int)elapsed.TotalHours)
                : global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text09F12DA9A77F2347,(int)elapsed.TotalDays);
            var status = ExposureState == ExposureCheckState.Failed ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBA8B42A3467A9C2E)
                : _exposureSnapshotNeedsRefresh ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9A2EB1FFC5C1ADCB)
                : !_hasExposureSnapshot ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2DEEA59FC1B457E3)
                : string.Empty;
            return global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text2B6C49A3EF88DCF0,relative,status);
        }
    }

    public bool ShowPriority => Category == "all" && _hasExposureSnapshot && PriorityIssue is not null;
    public bool HasVisibleIssues => Category == "done"
        ? ResolvedItems.Count > 0
        : ShowPriority || IssueGroups.Count > 0;
    public bool HasResolvedItems => ResolvedItems.Count > 0;
    public bool HasUnassessed => UnassessedCount > 0;
    public int UnassessedCount => _offlineSnapshot?.UnassessedPasswords.Count ?? 0;
    public bool IsReportComplete => ExposedChecked && !HasUnassessed;
    public bool ShowUnassessedPasswords => Category == "all" && ShowOfflineContent && HasUnassessed;
    public string UnassessedSummary =>
        $"{UnassessedCount} password strengths could not be assessed";
    public bool ShowResolvedHeader => Category == "all";
    public bool ShowFirstExposureCheck => (Category is "all" or "breach")
        && ShowOfflineContent && !_hasExposureSnapshot && !IsCheckingExposed;
    public bool ShowExposureReminder => (Category is "all" or "breach")
        && ShowOfflineContent && !ExposedChecked && !ShowFirstExposureCheck && !IsCheckingExposed;
    public bool ShowEmptyMessage => !HasVisibleIssues && !ShowFirstExposureCheck
        && !IsCheckingExposed;
    public string FirstExposureCheckDescription =>
        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2633905B29908710)
        + (Category == "all" ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text97D951354CF8B968) : string.Empty);
    public string ExposureProgressText =>
        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD10D8376FD2861E5);

    public string EmptyMessage => Category switch
    {
        "done" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA29F88DFDA5753BE),
        "ignored" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text27E69852EB8E1851),
        "breach" when IsCheckingExposed => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDAFBC213B690D689),
        "breach" when ExposureState == ExposureCheckState.Failed => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text419E2094A7E958CA),
        "breach" when _exposureSnapshotNeedsRefresh => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5DF28F04EDA330A1),
        "breach" when !ExposedChecked => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text07E019F9530EC5DF),
        "all" when ShowOfflineLoading => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text23F97E2C8B4EEB66),
        "all" when ShowOfflineError => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB092A04D30E63DC6),
        "all" when HasUnassessed => "Some password strengths could not be assessed. Review the listed items before considering this report complete.",
        "all" when IsCheckingExposed => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text91074AFABA14CA0B),
        "all" when ExposureState == ExposureCheckState.Failed => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB355774A78AD0D4C),
        "all" when _exposureSnapshotNeedsRefresh => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text14AE8EBB196D901D),
        "all" when !ExposedChecked => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF8BDF9EEAA072B28),
        "all" when IgnoredCount > 0 => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text91016010670E5C1A),
        "all" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text502551074C4512DA),
        _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text515E0ED4A9136455),
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
        ExposureCheckState.Checking => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFC2DB52DAAA9955B),
        ExposureCheckState.Failed => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4F6F06254D160F17),
        _ when _exposureSnapshotNeedsRefresh => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text525ABCFA6D227457),
        ExposureCheckState.NotStarted => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text451A805775B5DA03),
        _ => string.Empty,
    };

    public string ExposureStatusMessage => ExposureState switch
    {
        ExposureCheckState.Checking => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5DF6277C6CCE4002),
        ExposureCheckState.Failed => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text419E2094A7E958CA),
        _ when _exposureSnapshotNeedsRefresh => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB4B6BECE19A3550B),
        ExposureCheckState.Succeeded => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB35B93E53DBE8C4A),
        _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text709BF610E70CCB2F),
    };

    public string ExposureMetricAccessibleName => ExposureState switch
    {
        ExposureCheckState.Checking => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9CAD22F3AF320D11),
        ExposureCheckState.Failed => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1E06F9386A1AA944),
        _ when _exposureSnapshotNeedsRefresh => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text7F2EDF72740C8381,ExposedCount),
        ExposureCheckState.Succeeded => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text3B0E0B924EDFA8F8,ExposedCount),
        _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD46D04493C0D4AE1),
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
    public int IgnoredCount => _ignoredRows.Count;

    /// <summary>Reports a hidden session reminder while always keeping breaches visible.</summary>
    public bool IsIssueIgnored(string category, string cipherId) =>
        category != "breach" && !_isClearingSensitive && _ignoredIssueKeys.Contains((category, cipherId));

    /// <summary>Returns item counts for persistent navigation, independent of the selected report projection.</summary>
    public int GetCategoryIssueCount(string category) => category switch
    {
        "all" => OverviewIssueCount,
        "done" => ResolvedCount,
        "ignored" => IgnoredCount,
        _ => _currentIssues.Count(item => item.Category == category),
    };
    public bool HasAnyIssue => TotalIssueCount > 0;

    public string SummaryText
    {
        get
        {
            if (HasUnassessed)
            {
                var confirmedSummary = TotalIssueCount == 0
                    ? "No confirmed issues"
                    : $"{TotalIssueCount} confirmed findings";
                return $"{confirmedSummary}; {UnassessedCount} password strengths were not assessed"
                    + (ExposedChecked ? "." : "; exposure verification is incomplete.");
            }
            if (ExposedChecked)
                return TotalIssueCount == 0 ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE5E8B8F3181BFAB0) : global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text377B8519E0195B95,TotalIssueCount);
            var offlineSummary = TotalIssueCount == 0 ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFBF23C6B15FD9819) : global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text377B8519E0195B95,TotalIssueCount);
            var exposureSummary = ExposureState switch
            {
                ExposureCheckState.Checking => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextAE667419EACB2DB2),
                ExposureCheckState.Failed => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text313CBF1E10185ED1),
                _ when _exposureSnapshotNeedsRefresh => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE50E7F80B63570D3),
                _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA34B47F5C3228DB3),
            };
            return $"{offlineSummary}，{exposureSummary}";
        }
    }

    public SecuritySeverity OverallSeverity =>
        (WeakCount > 0 || ExposedCount > 0) ? SecuritySeverity.Critical :
        (ReusedCount > 0 || UnsecuredCount > 0) ? SecuritySeverity.Caution :
        IsReportComplete ? SecuritySeverity.Good : SecuritySeverity.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOfflineLoading))]
    [NotifyPropertyChangedFor(nameof(ShowOfflineError))]
    [NotifyPropertyChangedFor(nameof(ShowOfflineContent))]
    [NotifyPropertyChangedFor(nameof(LastCheckedText))]
    [NotifyPropertyChangedFor(nameof(EmptyMessage))]
    [NotifyPropertyChangedFor(nameof(ShowFirstExposureCheck))]
    [NotifyPropertyChangedFor(nameof(ShowExposureReminder))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyMessage))]
    public partial DataPageState OfflineState { get; set; } = DataPageState.Loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OfflineProgressText))]
    public partial int OfflineProgress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OfflineProgressText))]
    public partial int OfflineProgressTotal { get; set; } = 100;

    public string OfflineProgressText =>
        global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text1D8047C5BFC1A9FF,Math.Min(OfflineProgress, OfflineProgressTotal),OfflineProgressTotal);

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
        UnassessedItems.CollectionChanged += (_, _) => SourcesChanged();
    }

    partial void OnCategoryChanged(string value)
    {
        if (value is not ("all" or "breach" or "reuse" or "weak" or "http" or "done" or "ignored"))
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
            nameof(HasUnassessed), nameof(UnassessedCount), nameof(IsReportComplete),
            nameof(ShowUnassessedPasswords), nameof(UnassessedSummary),
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
        OfflineProgressTotal = 100;
        OfflineError = null;
        var progress = new Progress<HealthAnalysisProgress>(value =>
        {
            if (IsCurrentOfflineRequest(requestId, token) && ShowOfflineLoading)
            {
                OfflineProgressTotal = Math.Max(1, value.Total);
                OfflineProgress = value.Completed;
            }
        });

        try
        {
            var report = await _service.AnalyzeOfflineAsync(progress, token);
            if (!IsCurrentOfflineRequest(requestId, token) || !ApplyOfflineReport(report, requestId, token))
                return UiOperationResult<HealthReport>.Cancelled();

            if (!IsCurrentOfflineRequest(requestId, token))
                return UiOperationResult<HealthReport>.Cancelled();
            OfflineProgress = OfflineProgressTotal;
            if (!IsCurrentOfflineRequest(requestId, token))
                return UiOperationResult<HealthReport>.Cancelled();
            OfflineState = report.Reused.Count + report.Weak.Count + report.Unsecured.Count
                + report.UnassessedPasswords.Count == 0
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
            report.Weak.ToArray(), report.Unsecured.ToArray())
        {
            UnassessedPasswords = report.UnassessedPasswords.DistinctBy(item => item.CipherId).ToArray(),
        };
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

    /// <summary>Hides one confirmed non-breach finding for the current unlocked session.</summary>
    [RelayCommand]
    private void IgnoreIssue(SecurityIssueRow? issue)
    {
        if (_isClearingSensitive || issue is null || !issue.CanIgnore
            || !_currentIssues.Any(row => row.Category == issue.Category && row.CipherId == issue.CipherId))
            return;
        if (_ignoredIssueKeys.Add((issue.Category, issue.CipherId)))
            SourcesChanged();
    }

    /// <summary>Restores a session-scoped reminder without changing the vault item.</summary>
    [RelayCommand]
    private void RestoreIssue(SecurityIssueRow? issue)
    {
        if (_isClearingSensitive || issue is null)
            return;
        if (_ignoredIssueKeys.Remove((issue.Category, issue.CipherId)))
            SourcesChanged();
    }

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
            _ignoredIssueKeys.RemoveWhere(key => key.CipherId == cipherId);
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
            rows.Add((CreateRow(finding.Item, "breach", global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text04E5C452E2998952,finding.BreachCount)), finding.BreachCount));
        foreach (var group in _offlineSnapshot?.Reused ?? Array.Empty<ReusedGroup>())
        {
            foreach (var item in group.Items)
            {
                var other = group.Items.Where(candidate => candidate.CipherId != item.CipherId)
                    .OrderBy(candidate => candidate.Name, NameComparer).FirstOrDefault();
                var reason = other is null ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4BAFF73252EF45C3) : global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text52A1713289D5C83B,other.Name);
                rows.Add((CreateRow(item, "reuse", reason), 0));
            }
        }
        foreach (var finding in _offlineSnapshot?.Weak ?? Array.Empty<WeakFinding>())
            rows.Add((CreateRow(finding.Item, "weak", global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text12ED3F9C95761DBB)), 0));
        foreach (var finding in _offlineSnapshot?.Unsecured ?? Array.Empty<UnsecuredFinding>())
        {
            var reason = Uri.TryCreate(finding.Uri, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
                ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text7377B96AC5BB849C,uri.Host)
                : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBFB0B1413264CC7E);
            rows.Add((CreateRow(finding.Item, "http", reason), 0));
        }

        return rows.OrderBy(item => Array.IndexOf(IssueCategories, item.Row.Category))
            .ThenByDescending(item => item.BreachCount)
            .ThenBy(item => item.Row.Name, NameComparer)
            .ThenBy(item => item.Row.CipherId, StringComparer.Ordinal)
            .DistinctBy(item => (item.Row.Category, item.Row.CipherId))
            .Select(item => item.Row).ToList();
    }

    private static SecurityIssueRow CreateRow(HealthItemRef item, string category, string reason)
    {
        var name = string.IsNullOrWhiteSpace(item.Name) ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD03438B702B564F3) : item.Name;
        var username = item.Username ?? string.Empty;
        var description = GetCategoryDescription(category);
        return new SecurityIssueRow(item.CipherId, name, username,
            StringInfo.GetNextTextElement(name.Trim()).ToUpperInvariant(), reason,
            string.IsNullOrWhiteSpace(username) ? description : $"{description} · {username}",
            category, category == "http" ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text439A8EA0C52B423D) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEC9F62A12F85A370), string.Empty,
            category == "breach" ? "critical" : "warning");
    }

    private void RebuildProjection()
    {
        var generation = Interlocked.Increment(ref _projectionGeneration);
        bool IsCurrent() => generation == Volatile.Read(ref _projectionGeneration);
        var allIssues = CreateIssueRows();
        var activeKeys = allIssues.Select(row => (row.Category, row.CipherId)).ToHashSet();
        _ignoredIssueKeys.RemoveWhere(key => !activeKeys.Contains(key));
        _ignoredRows = allIssues.Where(row => _ignoredIssueKeys.Contains((row.Category, row.CipherId)))
            .OrderBy(row => row.Name, NameComparer)
            .ThenBy(row => row.CipherId, StringComparer.Ordinal)
            .Select(row => row with
            {
                IsIgnored = true,
                Username = string.Empty,
                Reason = $"{GetCategoryTitle(row.Category)} · {row.Reason}",
                ActionLabel = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4B03BE14C1F488E8),
            }).ToList();
        _currentIssues = allIssues.Where(row => !_ignoredIssueKeys.Contains((row.Category, row.CipherId))).ToList();
        _overviewIssues = _currentIssues.DistinctBy(item => item.CipherId).ToList();
        var currentIds = allIssues.Select(item => item.CipherId)
            .Concat((_offlineSnapshot?.UnassessedPasswords ?? []).Select(item => item.CipherId))
            .ToHashSet(StringComparer.Ordinal);

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
        var visibleRows = Category == "all" ? ShowPriority ? _overviewIssues.Skip(1) : _overviewIssues
            : Category == "done" ? Enumerable.Empty<SecurityIssueRow>()
            : Category == "ignored" ? _ignoredRows
            : _currentIssues.Where(item => item.Category == Category);
        List<SecurityIssueGroup> groups = Category == "ignored"
            ? _ignoredRows.Count == 0 ? []
                : new List<SecurityIssueGroup> { new("ignored", string.Empty, string.Empty, _ignoredRows, false) }
            : visibleRows.GroupBy(item => item.Category)
            .Select(group => new SecurityIssueGroup(group.Key, GetCategoryTitle(group.Key),
                GetCategoryDescription(group.Key), group, Category == "all")).ToList();
        if (!ReplaceItems(IssueGroups, groups, IsCurrent)
            || !ReplaceItems(ResolvedItems, Category is "all" or "done" ? _resolvedIssues : [], IsCurrent))
            return;

        foreach (var property in new[]
        {
            nameof(PageTitle), nameof(CategorySummary), nameof(LastCheckedText), nameof(ShowPriority),
            nameof(HasVisibleIssues), nameof(HasResolvedItems), nameof(EmptyMessage),
            nameof(OverviewIssueCount), nameof(ResolvedCount), nameof(IgnoredCount),
            nameof(ShowResolvedHeader), nameof(ShowFirstExposureCheck), nameof(ShowExposureReminder),
            nameof(FirstExposureCheckDescription), nameof(ShowEmptyMessage),
            nameof(ShowUnassessedPasswords), nameof(IsReportComplete),
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
            || !ReplaceItems(UnsecuredItems, offlineSnapshot?.Unsecured ?? Array.Empty<UnsecuredFinding>(), IsCurrent)
            || !ReplaceItems(UnassessedItems, offlineSnapshot?.UnassessedPasswords ?? [], IsCurrent))
            return;
        ReplaceItems(ExposedItems, exposureSnapshot, IsCurrent);
    }

    private static string GetCategoryTitle(string category) => category switch
    {
        "breach" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2A425E41491FBFA8),
        "reuse" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9CAE975C32F18B17),
        "weak" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEAC3BB39F8F0C574),
        "http" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text35CB23E74DACE235),
        _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text89E32ABCA54127DD),
    };

    private static string GetCategoryDescription(string category) => category switch
    {
        "breach" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCB64045AD564B199),
        "reuse" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text20A2BE9BB8EF0E14),
        "weak" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4F075F17958838D5),
        "http" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7FA83EAEA4B9954B),
        _ => string.Empty,
    };

    // Findings do not reveal whether an item was edited, archived, or deleted.
    private static string GetResultLabel(string category) => category switch
    {
        "breach" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3433349B93919350),
        "reuse" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC092B8FE1FA458E2),
        "weak" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0D86E1C282EB9087),
        "http" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC1201C3CD2B4EBDD),
        _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text27A1E176EDADD054),
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
            _ignoredIssueKeys.Clear();
            _pendingExposureRechecks.Clear();
            _currentIssues = [];
            _overviewIssues = [];
            _resolvedIssues = [];
            _ignoredRows = [];
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
            ClearSensitiveCollection(UnassessedItems);
            ExposureState = ExposureCheckState.NotStarted;
            ExposedError = null;
            OfflineError = null;
            OfflineProgress = 0;
            OfflineProgressTotal = 100;
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
