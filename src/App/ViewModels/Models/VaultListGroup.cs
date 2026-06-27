using System.Collections.ObjectModel;

namespace App.ViewModels.Models;

// 分组列表的一组。Count 派生自 Items;ShowHeader=false 时隐藏组头(单文件夹场景)。
public sealed class VaultListGroup
{
    public string Key { get; init; } = string.Empty;
    public bool ShowHeader { get; init; } = true;
    public ObservableCollection<CipherListItem> Items { get; } = new();
    public int Count => Items.Count;
}
