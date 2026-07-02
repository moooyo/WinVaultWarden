namespace App.Services;

public static class ClipboardClearPolicy
{
    // 清空剪贴板 ComboBox 索引 → 秒。0=永不(0 → 不自动清);越界回退 30。
    public static int SecondsForIndex(int index) => index switch
    {
        0 => 0, 1 => 10, 2 => 20, 3 => 30, 4 => 60, 5 => 120, 6 => 300,
        _ => 30,
    };
}
