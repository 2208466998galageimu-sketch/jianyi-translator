using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Wpf;
using Thanslator.App;

internal static class WindowFrameChecks
{
    public static async Task RunAsync(MoreSettingsWindow window, WebView2 view, string output)
    {
        if (window.WindowStyle != WindowStyle.None || window.ResizeMode != ResizeMode.CanResize ||
            WindowChrome.GetWindowChrome(window)?.CaptionHeight != 0 || !view.CoreWebView2.Settings.IsNonClientRegionSupportEnabled)
            throw new Exception("Frameless window configuration failed.");
        var layout = await view.ExecuteScriptAsync("""
            (()=>{
              const rect=document.querySelector('body>main').getBoundingClientRect();
              return {x:rect.x,y:rect.y,width:rect.width,height:rect.height,
                viewportWidth:innerWidth,viewportHeight:innerHeight,
                controls:document.querySelectorAll('[data-window]').length,
                edges:document.querySelectorAll('.window-resize-edge').length,
                bodyPadding:getComputedStyle(document.body).padding};
            })()
            """);
        using var data = JsonDocument.Parse(layout);
        var value = data.RootElement;
        if (value.GetProperty("x").GetDouble() != 0 || value.GetProperty("y").GetDouble() != 0 ||
            Math.Abs(value.GetProperty("width").GetDouble() - value.GetProperty("viewportWidth").GetDouble()) > 1 ||
            Math.Abs(value.GetProperty("height").GetDouble() - value.GetProperty("viewportHeight").GetDouble()) > 1 ||
            value.GetProperty("controls").GetInt32() != 3 || value.GetProperty("edges").GetInt32() != 8)
            throw new Exception("Rounded content does not fill the complete viewport.");
        File.WriteAllText(Path.Combine(output, "frame-layout.json"), layout);
        var region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            var result = GetWindowRgn(new WindowInteropHelper(window).Handle, region);
            if (result == 0 || PtInRegion(region, 0, 0) || !PtInRegion(region, 100, 100))
                throw new Exception("Native rounded window clipping failed.");
        }
        finally { DeleteObject(region); }
        window.Hide();
        await view.ExecuteScriptAsync("nativeCall('window',{command:'maximize'});");
        for (var i = 0; i < 100 && window.WindowState != WindowState.Maximized; i++) await Task.Delay(20);
        if (window.WindowState != WindowState.Maximized) throw new Exception("Maximize button did not change native window state.");
        await view.ExecuteScriptAsync("nativeCall('window',{command:'maximize'});");
        for (var i = 0; i < 100 && window.WindowState != WindowState.Normal; i++) await Task.Delay(20);
        if (window.WindowState != WindowState.Normal) throw new Exception("Restore button failed.");
        await view.ExecuteScriptAsync("nativeCall('window',{command:'minimize'});");
        for (var i = 0; i < 100 && window.WindowState != WindowState.Minimized; i++) await Task.Delay(20);
        if (window.WindowState != WindowState.Minimized) throw new Exception("Minimize button failed.");
        window.WindowState = WindowState.Normal;
        window.Left = -20000; window.Top = 0; window.Show();
        File.WriteAllText(Path.Combine(output, "frame-passed.txt"), "No system title bar, no outer gutters, rounded native clipping, three window controls, eight resize edges, minimize/maximize/restore passed.");
    }
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint hwnd, nint region);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
}
