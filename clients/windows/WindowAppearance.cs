using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using WinRT.Interop;
namespace ClipWeave;
static class WindowAppearance {
    public static void Apply(Window window){
        window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory,"Assets","cliper-tray-black.ico"));
        if(window.Content is FrameworkElement root)root.RequestedTheme=ElementTheme.Light;
        var disabled=0;var hwnd=WindowNative.GetWindowHandle(window);
        // Keep native chrome light even when Windows itself uses dark mode.
        DwmSetWindowAttribute(hwnd,20,ref disabled,sizeof(int));
    }
    public static void SizeCentered(Window window,double width,double height,Window? relative=null){
        var reference=relative??window;var scale=Math.Max(1,GetDpiForWindow(WindowNative.GetWindowHandle(reference))/96.0);
        var area=Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(reference.AppWindow.Id,Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
        var w=Math.Min((int)Math.Round(width*scale),Math.Max(1,area.Width-24));var h=Math.Min((int)Math.Round(height*scale),Math.Max(1,area.Height-24));
        window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X+(area.Width-w)/2,area.Y+(area.Height-h)/2,w,h));
    }
    [DllImport("user32.dll")]static extern uint GetDpiForWindow(IntPtr hwnd);
    public static void PaintWhite(IntPtr hwnd,IntPtr hdc){if(hdc!=IntPtr.Zero&&GetClientRect(hwnd,out var rect))FillRect(hdc,ref rect,GetStockObject(0));}
    [StructLayout(LayoutKind.Sequential)]struct RECT{public int Left,Top,Right,Bottom;}
    [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
    [DllImport("user32.dll")]static extern bool GetClientRect(IntPtr hwnd,out RECT rect);
    [DllImport("user32.dll")]static extern int FillRect(IntPtr hdc,ref RECT rect,IntPtr brush);
    [DllImport("gdi32.dll")]static extern IntPtr GetStockObject(int index);
}
