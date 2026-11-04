using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SpirePatch;

// Best-effort Windows 11 system backdrop. Older versions keep the opaque-safe WPF gradient.
// This does not alter or require any game directory files.
internal static class SpireGlassBackdrop
{
 [DllImport("dwmapi.dll",PreserveSig=true)]
 static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
 [StructLayout(LayoutKind.Sequential)]
 struct Margins {public int Left,Right,Top,Bottom;}
 [DllImport("dwmapi.dll",PreserveSig=true)]
 static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd,ref Margins margins);
 internal static void Enable(Window window)
 {
  if(!OperatingSystem.IsWindowsVersionAtLeast(10,0,22621))return;
  try
  {
   var hwnd=new WindowInteropHelper(window).Handle;
   if(hwnd==IntPtr.Zero)return;
   int dark=1,backdrop=2; // DWMWA_USE_IMMERSIVE_DARK_MODE / DWMSBT_MAINWINDOW (Mica)
   DwmSetWindowAttribute(hwnd,20,ref dark,sizeof(int));
   DwmSetWindowAttribute(hwnd,38,ref backdrop,sizeof(int));
   var frame=new Margins{Left=-1,Top=-1,Right=-1,Bottom=-1};
   DwmExtendFrameIntoClientArea(hwnd,ref frame);
  }
  catch(DllNotFoundException){}
  catch(EntryPointNotFoundException){}
 }
}
