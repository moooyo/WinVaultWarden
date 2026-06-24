using App.ViewModels.Models;

namespace App.Views;

public static class SendEditorDialogLifecycle
{
    public static void SetType(
        SendEditorDraft draft,
        SendType type,
        bool canUpdateBindings,
        Action? updateBindings)
    {
        draft.Type = type;
        if (canUpdateBindings)
            updateBindings?.Invoke();
    }
}
