using System.Runtime.InteropServices;

namespace Maus.Bench.Platform;

/// <summary>
/// Fenêtre plein écran sans bordure sur l'écran principal, avec sa propre boucle de messages (Win32 : RegisterClassExW,
/// CreateWindowExW, PeekMessageW ; Microsoft Learn). Échap demande l'arrêt ; le curseur est masqué pendant la mesure.
/// Plein écran « fenêtré » : Windows garde la main (Alt+Tab, notifications) et la présentation reste en mode flip.
/// </summary>
public sealed unsafe partial class BenchWindow : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsExAppWindow = 0x00040000;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const uint WmDestroy = 0x0002;
    private const uint WmClose = 0x0010;
    private const uint WmKeyDown = 0x0100;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmQuit = 0x0012;
    private const uint PmRemove = 0x0001;
    private const int VkEscape = 0x1B;
    private const int VkReturn = 0x0D;
    private const int VkSpace = 0x20;
    private const int SwShow = 5;

    private static BenchWindow? s_current;
    private readonly nint _instance;
    private readonly string _className = "MAUS.Benchmark." + Guid.NewGuid().ToString("N");
    private bool _cursorHidden;

    public BenchWindow(string title)
    {
        _instance = GetModuleHandle(null);
        var className = Marshal.StringToHGlobalUni(_className);
        try
        {
            var wc = new WndClassEx
            {
                Size = (uint)sizeof(WndClassEx),
                Style = 0x0003, // CS_HREDRAW | CS_VREDRAW
                WndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
                Instance = _instance,
                Cursor = LoadCursor(0, 32512), // IDC_ARROW
                Background = 0,
                ClassName = className,
            };
            if (RegisterClassEx(&wc) == 0)
            {
                throw new InvalidOperationException("RegisterClassEx : " + Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            // Windows garde une copie du nom de classe : la mémoire peut être rendue.
            Marshal.FreeHGlobal(className);
        }

        Width = GetSystemMetrics(SmCxScreen);
        Height = GetSystemMetrics(SmCyScreen);
        s_current = this;
        Handle = CreateWindowEx(WsExAppWindow, _className, title, WsPopup | WsVisible, 0, 0, Width, Height, 0, 0, _instance, 0);
        if (Handle == 0)
        {
            throw new InvalidOperationException("CreateWindowEx : " + Marshal.GetLastPInvokeError());
        }

        ShowWindow(Handle, SwShow);
        SetForegroundWindow(Handle);
    }

    public nint Handle { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Échap ou fermeture de la fenêtre : l'utilisateur veut arrêter.</summary>
    public bool CloseRequested { get; private set; }

    /// <summary>Entrée ou Espace (écran des résultats : fermer).</summary>
    public bool ConfirmPressed { get; private set; }

    public void HideCursor()
    {
        if (!_cursorHidden)
        {
            ShowCursor(false);
            _cursorHidden = true;
        }
    }

    /// <summary>Traite les messages en attente sans bloquer. Faux si la fenêtre a été fermée.</summary>
    public bool Pump()
    {
        ConfirmPressed = false;
        Msg msg;
        while (PeekMessage(&msg, 0, 0, 0, PmRemove))
        {
            if (msg.Message == WmQuit)
            {
                CloseRequested = true;
                return false;
            }

            TranslateMessage(&msg);
            DispatchMessage(&msg);
        }

        return !CloseRequested;
    }

    public void Dispose()
    {
        if (_cursorHidden)
        {
            ShowCursor(true);
            _cursorHidden = false;
        }

        if (Handle != 0)
        {
            DestroyWindow(Handle);
        }

        UnregisterClass(_className, _instance);
        if (ReferenceEquals(s_current, this))
        {
            s_current = null;
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        var window = s_current;
        switch (message)
        {
            case WmKeyDown or WmSysKeyDown when window is not null:
                if (wParam == VkEscape)
                {
                    window.CloseRequested = true;
                }
                else if (wParam is VkReturn or VkSpace)
                {
                    window.ConfirmPressed = true;
                }

                break;
            case WmClose when window is not null:
                window.CloseRequested = true;
                return 0;
            case WmDestroy:
                return 0;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public nint WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public nint MenuName;
        public nint ClassName;
        public nint IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? name);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static partial ushort RegisterClassEx(WndClassEx* windowClass);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterClass(string className, nint instance);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static partial nint LoadCursor(nint instance, nint name);

    [LibraryImport("user32.dll")]
    private static partial int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(Msg* msg, nint hwnd, uint filterMin, uint filterMax, uint remove);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(Msg* msg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(Msg* msg);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
}
