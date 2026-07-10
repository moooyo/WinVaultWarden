using App.ViewModels.Models;

namespace App.Views;

public static class SendEditorDialogLifecycle
{
    // 早期(bindings 未就绪)的 RadioButton.Checked 事件必须完全忽略:既不改草稿类型、也不刷新绑定。
    // 否则编辑既有文件 Send 时,初始化阶段默认 Text 按钮的 Checked 会把 File 草稿误改成 Text。
    public static void SetType(
        SendEditorDraft draft,
        SendType type,
        bool canUpdateBindings,
        Action? updateBindings)
    {
        if (!canUpdateBindings)
            return;
        draft.Type = type;
        updateBindings?.Invoke();
    }
}
