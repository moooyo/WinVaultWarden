namespace App.Services;

public readonly record struct WindowSizeDip(double Width, double Height);

public static class LoginWindowSizing
{
    public static WindowSizeDip ClampToWorkArea(
        WindowSizeDip desired,
        WindowSizeDip workArea,
        WindowSizeDip preferredMinimum,
        WindowSizeDip configuredMaximum,
        double workAreaMarginPerEdge)
    {
        ValidateFinite(desired, nameof(desired));
        ValidateFinite(workArea, nameof(workArea));
        ValidateFinite(preferredMinimum, nameof(preferredMinimum));
        ValidateFinite(configuredMaximum, nameof(configuredMaximum));
        if (!double.IsFinite(workAreaMarginPerEdge))
            throw new ArgumentOutOfRangeException(nameof(workAreaMarginPerEdge));

        var margin = Math.Max(0, workAreaMarginPerEdge);
        return new WindowSizeDip(
            ClampDimension(desired.Width, workArea.Width, preferredMinimum.Width, configuredMaximum.Width, margin),
            ClampDimension(desired.Height, workArea.Height, preferredMinimum.Height, configuredMaximum.Height, margin));
    }

    private static double ClampDimension(
        double desired,
        double workArea,
        double preferredMinimum,
        double configuredMaximum,
        double margin)
    {
        var availableMaximum = Math.Max(1, workArea - (2 * margin));
        var configuredLimit = Math.Max(1, configuredMaximum);
        var upperBound = Math.Min(availableMaximum, configuredLimit);
        var preferredFloor = Math.Clamp(preferredMinimum, 1, configuredLimit);
        var lowerBound = Math.Min(preferredFloor, upperBound);

        return Math.Clamp(desired, lowerBound, upperBound);
    }

    private static void ValidateFinite(WindowSizeDip size, string parameterName)
    {
        if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height))
            throw new ArgumentOutOfRangeException(parameterName);
    }
}
