namespace App.Services;

public enum DataPageState { Loading, Error, Empty, Content }
public enum UiOperationStatus { Succeeded, Busy, Cancelled, Failed }

public readonly record struct UiOperationResult(
    UiOperationStatus Status,
    string? Error = null)
{
    public bool IsSuccess => Status == UiOperationStatus.Succeeded;
    public static UiOperationResult Success() => new(UiOperationStatus.Succeeded);
    public static UiOperationResult Busy() => new(UiOperationStatus.Busy);
    public static UiOperationResult Cancelled() => new(UiOperationStatus.Cancelled);
    public static UiOperationResult Failure(string error) => new(UiOperationStatus.Failed, error);
}

public readonly record struct UiOperationResult<T>(
    UiOperationStatus Status,
    T? Value = default,
    string? Error = null)
{
    public bool IsSuccess => Status == UiOperationStatus.Succeeded;
    public static UiOperationResult<T> Success(T value) => new(UiOperationStatus.Succeeded, value);
    public static UiOperationResult<T> Busy() => new(UiOperationStatus.Busy);
    public static UiOperationResult<T> Cancelled() => new(UiOperationStatus.Cancelled);
    public static UiOperationResult<T> Failure(string error) => new(UiOperationStatus.Failed, default, error);
}
