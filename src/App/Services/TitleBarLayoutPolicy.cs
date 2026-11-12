namespace App.Services;

/// <summary>Determines whether measured title-bar siblings leave space for search and tab labels.</summary>
public static class TitleBarLayoutPolicy
{
    public static bool CanCenterSearch(
        double width, double tabsRight, double actionsLeft, double searchWidth = 360, double gap = 12)
    {
        if (!double.IsFinite(width) || !double.IsFinite(tabsRight) || !double.IsFinite(actionsLeft)
            || width < 1280 || tabsRight <= 0 || actionsLeft <= tabsRight
            || !double.IsFinite(searchWidth) || searchWidth <= 0 || !double.IsFinite(gap) || gap < 0)
            return false;
        var left = (width - searchWidth) / 2;
        return left >= tabsRight + gap && left + searchWidth <= actionsLeft - gap;
    }

    public static bool NeedCompactTabs(
        double width, double expandedTabs, double brand, double actions, double caption,
        double insets, double minimumSearch)
    {
        if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(expandedTabs) || expandedTabs <= 0)
            return false;
        if (!double.IsFinite(brand) || !double.IsFinite(actions) || !double.IsFinite(caption)
            || !double.IsFinite(insets) || !double.IsFinite(minimumSearch))
            return false;
        return expandedTabs > Math.Max(0, width - brand - actions - caption - insets - minimumSearch);
    }
}
