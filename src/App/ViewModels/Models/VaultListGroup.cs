using System.Collections.ObjectModel;

namespace App.ViewModels.Models;

// 分组列表的一组:分组对象自身即条目集合。CollectionViewSource(IsSourceGrouped)直接把每个
// 分组对象当作可枚举的条目集合,无需反射式 ItemsPath —— 从而规避 AOT 下对托管属性的
// ICustomProperty 反射(否则运行时抛 NotSupportedException)。ShowHeader=false 时隐藏组头。
public sealed class VaultListGroup : ObservableCollection<CipherListItem>
{
    public string Key { get; init; } = string.Empty;
    public bool ShowHeader { get; init; } = true;
    // Count 由 ObservableCollection 提供,组头 x:Bind 直接读取。
}
