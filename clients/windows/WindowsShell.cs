using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using System.Windows.Input;
using WinRT.Interop;
using WinUIEx;
using WinUIEx.Messaging;
using Windows.Graphics;
using Microsoft.UI.Xaml.Input;

namespace ClipWeave;

// Windows counterpart of the Mac menu-bar panel. The Go core remains alive
// while the panel is hidden, and only the explicit Quit action closes it.
sealed class WindowsShell : IDisposable {
    readonly MainWindow window;
    readonly IntPtr hwnd;
    readonly WindowMessageMonitor messages;
    TaskbarIcon? tray;
    System.Drawing.Icon? icon;
    IntPtr previous;
    bool exiting,resizing;
    int resizeStartY;double resizeStartHeight,resizeScale=1;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer saveHeight;
    public bool MenuOpen { get; set; }
    public bool HotkeyAvailable { get; }
    public static bool ControlDown=>(GetAsyncKeyState(0x11)&0x8000)!=0;
    public WindowsShell(MainWindow window) {
        this.window=window;hwnd=WindowNative.GetWindowHandle(window);previous=GetForegroundWindow();
        window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory,"Assets","cliper-tray-black.ico"));
        window.SetIsShownInSwitchers(false);window.SetIsAlwaysOnTop(true);window.SetIsResizable(false);
        if(window.AppWindow.Presenter is OverlappedPresenter presenter)presenter.SetBorderAndTitleBar(false,false);
        WindowAppearance.Apply(window);
        saveHeight=window.DispatcherQueue.CreateTimer();saveHeight.Interval=TimeSpan.FromMilliseconds(400);
        saveHeight.Tick+=(_,_)=>{saveHeight.Stop();try{window.Preferences.Save();}catch(Exception error){UiDiagnostics.Record("save-panel-height",error);}};
        var grip=new Border{Width=100,Height=10,VerticalAlignment=VerticalAlignment.Top,HorizontalAlignment=HorizontalAlignment.Center,Background=Palette.White,Child=new Border{Width=38,Height=3,CornerRadius=new CornerRadius(2),Background=Palette.Muted,VerticalAlignment=VerticalAlignment.Center}};
        ToolTipService.SetToolTip(grip,L10n.Text("拖动调整高度，面板始终贴底"));
        grip.PointerPressed+=(_,e)=>{if(!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed&&e.Pointer.PointerDeviceType==Microsoft.UI.Input.PointerDeviceType.Mouse)return;GetCursorPos(out var point);resizeStartY=point.Y;resizeStartHeight=window.Preferences.PanelHeight;resizeScale=Math.Max(1,GetDpiForWindow(hwnd)/96.0);resizing=grip.CapturePointer(e.Pointer);e.Handled=true;};
        grip.PointerMoved+=(_,e)=>{if(!resizing)return;GetCursorPos(out var point);SetPanelHeight(resizeStartHeight+(resizeStartY-point.Y)/resizeScale);e.Handled=true;};
        grip.PointerReleased+=(_,e)=>{resizing=false;grip.ReleasePointerCapture(e.Pointer);e.Handled=true;};grip.PointerCaptureLost+=(_,_)=>resizing=false;
        window.Root.Children.Add(grip);
        messages=new WindowMessageMonitor(window);
        messages.WindowMessageReceived+=(_,e)=>{
            if((uint)e.Message.MessageId==0x0014){WindowAppearance.PaintWhite(hwnd,(IntPtr)e.Message.WParam);e.Handled=true;e.Result=1;}
            if((uint)e.Message.MessageId==0x02E0&&window.AppWindow.IsVisible)window.DispatcherQueue.TryEnqueue(()=>Position(false));
            if((uint)e.Message.MessageId==0x0312){e.Handled=true;Toggle();}
            if((uint)e.Message.MessageId==0x007E&&window.AppWindow.IsVisible)Position();
        };
        HotkeyAvailable=RegisterHotKey(hwnd,1,0x4000|0x0002|0x0004,0x56);
        if(!HotkeyAvailable){window.Preferences.ShowTray=true;window.Preferences.Save();}
        if(!HotkeyAvailable)window.SetNotice(L10n.Text("快捷键已被占用，可从任务栏托盘打开 Clipper。"));
        window.AppWindow.Closing+=(_,e)=>{if(!exiting){e.Cancel=true;HidePanel(false);}};
        window.Activated+=(_,e)=>{
            if(e.WindowActivationState==WindowActivationState.Deactivated&&!window.Preferences.Pinned&&!MenuOpen&&!window.DialogOpen)HidePanel(false);
        };
        window.Root.Loaded+=(_,_)=>SetTrayVisible(window.Preferences.ShowTray);
    }
    public void Toggle() {if(window.AppWindow.IsVisible)HidePanel(true);else ShowPanel();}
    public void ShowPanel() {
        if(!window.AccountReady){window.ShowLogin();return;}
        var front=GetForegroundWindow();if(front!=IntPtr.Zero&&front!=hwnd){GetWindowThreadProcessId(front,out var pid);if(pid!=(uint)Environment.ProcessId)previous=front;}
        Position();window.AppWindow.Show();window.Activate();SetForegroundWindow(hwnd);window.FocusSearch();
        window.RefreshInBackground();
    }
    public void HidePanel(bool restore=false) {window.AppWindow.Hide();if(restore&&IsWindow(previous))SetForegroundWindow(previous);}
    public void Position(bool followPointer=true) {
        GetCursorPos(out var point);
        var area=(followPointer?DisplayArea.GetFromPoint(new PointInt32(point.X,point.Y),DisplayAreaFallback.Nearest):DisplayArea.GetFromWindowId(window.AppWindow.Id,DisplayAreaFallback.Nearest)).WorkArea;
        var monitor=MonitorFromPoint(point,2);uint dpi=96;
        if(GetDpiForMonitor(monitor,0,out var x,out _) == 0)dpi=x;
        if(!followPointer)dpi=GetDpiForWindow(hwnd);
        var box=ClipPresentation.PanelBounds(area.X,area.Y,area.Width,area.Height,dpi/96.0,window.Preferences.PanelHeight);
        window.AppWindow.MoveAndResize(new RectInt32(box.X,box.Y,box.Width,box.Height));
    }
    public void ActivateWindow(Window target)=>SetForegroundWindow(WindowNative.GetWindowHandle(target));
    public void SetPanelHeight(double height){window.Preferences.PanelHeight=double.IsFinite(height)?Math.Clamp(height,300,760):430;Position(false);saveHeight.Stop();saveHeight.Start();}
    public void SetTrayVisible(bool visible) {
        if(!visible){if(tray!=null){window.Root.Children.Remove(tray);tray.Dispose();tray=null;icon?.Dispose();icon=null;}return;}
        if(tray!=null || window.Root.XamlRoot==null)return;
        var menu=new MenuFlyout();
        void Item(string title,Action action){var item=new MenuFlyoutItem{Text=L10n.Text(title)};item.Click+=(_,_)=>action();menu.Items.Add(item);}
        Item("打开 Clipper",ShowPanel);
        Item("暂停／启用采集",()=>_=window.Execute(()=>window.ToggleCapture()));
        Item("立即同步",()=>_=window.Execute(window.SyncNow));
        Item("锁定资料库",()=>_=window.Execute(window.LockVault));
        menu.Items.Add(new MenuFlyoutSeparator());Item("设置…",window.OpenSettings);Item("退出 Clipper",Quit);
        menu.Opening+=(_,_)=>MenuOpen=true;menu.Closed+=(_,_)=>MenuOpen=false;
        icon=new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory,"Assets","cliper-tray-black.ico"));
        tray=new TaskbarIcon{Icon=icon,ToolTipText="Clipper",ContextFlyout=menu,LeftClickCommand=new ShellCommand(Toggle),Width=0,Height=0};
        window.Root.Children.Add(tray);tray.ForceCreate(false);
    }
    public async Task PasteToPreviousAsync(bool paste) {
        HidePanel(true);
        if(!paste||!IsWindow(previous))return;
        await Task.Delay(140);
        if(GetForegroundWindow()!=previous){window.SetNotice(L10n.Text("已复制，请按 Ctrl+V 粘贴。"));return;}
        var inputs=new[]{Key(0x11,false),Key(0x56,false),Key(0x56,true),Key(0x11,true)};
        if(SendInput((uint)inputs.Length,inputs,Marshal.SizeOf<INPUT>())!=inputs.Length)window.SetNotice(L10n.Text("已复制，请按 Ctrl+V 粘贴。"));
    }
    static INPUT Key(ushort code,bool up)=>new(){type=1,data=new INPUTDATA{keyboard=new KEYBOARD{virtualKey=code,flags=up?2u:0u}}};
    public void Quit(){exiting=true;window.Close();}
    public void Dispose(){saveHeight.Stop();try{window.Preferences.Save();}catch{}UnregisterHotKey(hwnd,1);messages.Dispose();tray?.Dispose();icon?.Dispose();}
    sealed class ShellCommand(Action action):ICommand {
        public bool CanExecute(object? parameter)=>true;
        public void Execute(object? parameter)=>action();
        public event EventHandler? CanExecuteChanged {add{}remove{}}
    }
    [StructLayout(LayoutKind.Sequential)] struct POINT {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] struct KEYBOARD {public ushort virtualKey,scanCode;public uint flags,time;public UIntPtr extra;}
    [StructLayout(LayoutKind.Explicit,Size=32)] struct INPUTDATA {[FieldOffset(0)] public KEYBOARD keyboard;}
    [StructLayout(LayoutKind.Sequential)] struct INPUT {public uint type;public INPUTDATA data;}
    [DllImport("user32.dll")]static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll")]static extern bool UnregisterHotKey(IntPtr hwnd,int id);
    [DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")]static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")]static extern IntPtr MonitorFromPoint(POINT point,uint flags);
    [DllImport("shcore.dll")]static extern int GetDpiForMonitor(IntPtr monitor,int type,out uint x,out uint y);
    [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
    [DllImport("user32.dll",SetLastError=true)]static extern uint SendInput(uint count,INPUT[] inputs,int size);
}
