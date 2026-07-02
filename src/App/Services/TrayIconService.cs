using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using App.Interop;

namespace App.Services;

/// <summary>
/// Win32 系统托盘图标：注册 NOTIFYICONDATA、子类化窗口过程接收托盘回调、弹出右键菜单、派发点击。
/// 单窗口单实例（一个 MainWindow），故用静态 _instance 供 [UnmanagedCallersOnly] 回调派发。
/// </summary>
internal sealed unsafe class TrayIconService : IDisposable
{
    private const uint TrayId = 1;

    private static TrayIconService? _instance;

    private readonly IntPtr _hwnd;
    private readonly Action _onOpen;
    private readonly Action _onLock;
    private readonly Action _onExit;

    private IntPtr _origWndProc;
    private IntPtr _hIcon;
    private bool _ownsIcon;
    private bool _subclassed;
    private bool _visible;

    public TrayIconService(IntPtr hwnd, Action onOpen, Action onLock, Action onExit)
    {
        _hwnd = hwnd;
        _onOpen = onOpen;
        _onLock = onLock;
        _onExit = onExit;
        _instance = this;
    }

    /// <summary>加托盘图标（幂等）。首次调用时子类化窗口过程以接收托盘回调消息。</summary>
    public void Show()
    {
        if (_visible)
            return;

        EnsureSubclassed();
        if (_hIcon == IntPtr.Zero)
            (_hIcon, _ownsIcon) = LoadTrayIcon();

        var data = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NativeMethods.NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = TrayId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = NativeMethods.WM_TRAYICON,
            hIcon = _hIcon,
        };
        SetTip(ref data, "WinVaultWarden");
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
        _visible = true;
    }

    /// <summary>删托盘图标（幂等）。窗口过程子类化保留至 Dispose，避免频繁装卸的竞态。</summary>
    public void Hide()
    {
        if (!_visible)
            return;

        var data = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NativeMethods.NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = TrayId,
        };
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
        _visible = false;
    }

    public void Dispose()
    {
        Hide();

        if (_subclassed && _origWndProc != IntPtr.Zero)
        {
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_WNDPROC, _origWndProc);
            _subclassed = false;
            _origWndProc = IntPtr.Zero;
        }

        if (_ownsIcon && _hIcon != IntPtr.Zero)
            NativeMethods.DestroyIcon(_hIcon);   // stock 图标不销毁（_ownsIcon=false）
        _hIcon = IntPtr.Zero;

        if (ReferenceEquals(_instance, this))
            _instance = null;
    }

    private void EnsureSubclassed()
    {
        if (_subclassed)
            return;

        delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr> fp = &WndProcStatic;
        _origWndProc = NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_WNDPROC, (IntPtr)fp);
        _subclassed = true;
    }

    private void ShowMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, (UIntPtr)1, "打开");
        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, (UIntPtr)2, "锁定");
        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, (UIntPtr)3, "退出");

        // 经典要求：弹菜单前把窗口设为前台，否则菜单不会在点击别处时消失。
        NativeMethods.SetForegroundWindow(_hwnd);
        NativeMethods.GetCursorPos(out var pt);
        int cmd = NativeMethods.TrackPopupMenu(
            menu,
            NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON,
            pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        NativeMethods.DestroyMenu(menu);

        switch (cmd)
        {
            case 1: _onOpen(); break;
            case 2: _onLock(); break;
            case 3: _onExit(); break;
        }
    }

    private static (IntPtr icon, bool owns) LoadTrayIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var small = new IntPtr[1];
                var count = NativeMethods.ExtractIconEx(exe, 0, null, small, 1);
                if (count > 0 && small[0] != IntPtr.Zero)
                    return (small[0], true);   // 提取的图标由我们负责 DestroyIcon
            }
        }
        catch
        {
            // 退回 stock 图标
        }
        return (NativeMethods.LoadIcon(IntPtr.Zero, (IntPtr)NativeMethods.IDI_APPLICATION), false);
    }

    private static void SetTip(ref NativeMethods.NOTIFYICONDATAW data, string tip)
    {
        int n = Math.Min(tip.Length, 127);
        for (int i = 0; i < n; i++)
            data.szTip[i] = tip[i];
        data.szTip[n] = '\0';
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr WndProcStatic(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var self = _instance;
        if (self is not null && msg == NativeMethods.WM_TRAYICON)
        {
            uint evt = (uint)(lParam.ToInt64() & 0xFFFF);
            if (evt == NativeMethods.WM_LBUTTONUP)
                self._onOpen();
            else if (evt == NativeMethods.WM_RBUTTONUP || evt == NativeMethods.WM_CONTEXTMENU)
                self.ShowMenu();
            return IntPtr.Zero;
        }
        return NativeMethods.CallWindowProc(self?._origWndProc ?? IntPtr.Zero, hWnd, msg, wParam, lParam);
    }
}
