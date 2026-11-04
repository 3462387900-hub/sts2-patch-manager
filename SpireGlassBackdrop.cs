using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace SpirePatch;

/// <summary>
/// Uses actual Desktop Acrylic via the Windows 11 DWM compositor where supported.
/// It must not be confused with a tinted WPF brush: transparent HwndTarget pixels
/// and a full client glass frame are required before the material is visible.
/// </summary>
internal static class SpireGlassBackdrop
{
 const int DwmDarkMode=20, DwmSystemBackdrop=38, DwmCaptionColor=35, DwmCaptionTextColor=36;
 const int DesktopAcrylic=3, NoBackdrop=1;
 [DllImport("dwmapi.dll",PreserveSig=true)]
 static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
 [DllImport("dwmapi.dll",PreserveSig=true)]
 static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
 [StructLayout(LayoutKind.Sequential)]
 struct Margins {public int Left,Right,Top,Bottom;}
 [DllImport("dwmapi.dll",PreserveSig=true)]
 static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd,ref Margins margins);

 internal static string Status{get;private set;}="尚未检测窗口材质。";

 static bool TransparencyDisabled()
 {
  try
  {
   object? value=Registry.GetValue(
    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
    "EnableTransparency",null);
   return value!=null&&Convert.ToInt32(value)==0;
  }
  catch(Exception ex) when(ex is UnauthorizedAccessException or System.Security.SecurityException or FormatException or InvalidCastException)
  {
   return false; // Let DWM decide when the preference cannot be read.
  }
 }

 internal static bool Enable(Window window)
 {
  if(!OperatingSystem.IsWindowsVersionAtLeast(10,0,22621))
  {
   Status="系统不支持 Windows 11 22H2 原生 Desktop Acrylic；已使用应用内渐变玻璃层次。";
   return false;
  }
  if(window.AllowsTransparency||SystemParameters.HighContrast)
  {
   Status="透明窗口模式或高对比度主题不适合系统 Acrylic；已使用可读性优先的回退背景。";
   return false;
  }
  if(TransparencyDisabled())
  {
   Status="Windows「透明效果」已关闭；请在个性化设置中开启后重启程序以使用系统毛玻璃。";
   return false;
  }

  try
  {
   if(DwmIsCompositionEnabled(out bool composition)<0||!composition)
   {
    Status="DWM 桌面合成不可用；已使用应用内玻璃层次。";
    return false;
   }
   IntPtr hwnd=new WindowInteropHelper(window).Handle;
   HwndSource? source=hwnd==IntPtr.Zero?null:HwndSource.FromHwnd(hwnd);
   if(source?.CompositionTarget==null)
   {
    Status="窗口尚未初始化系统合成目标，使用应用内回退背景。";
    return false;
   }

   var target=source.CompositionTarget;
   Color previousColor=target.BackgroundColor;
   bool extended=false;
   try
   {
    // An opaque HwndTarget background would completely hide the DWM backdrop.
    target.BackgroundColor=Colors.Transparent;
    var margins=new Margins{Left=-1,Right=-1,Top=-1,Bottom=-1};
    if(DwmExtendFrameIntoClientArea(hwnd,ref margins)<0)
      throw new InvalidOperationException("无法将 DWM 桌面材质扩展到内容区域。");
    extended=true;

    int dark=1;
    DwmSetWindowAttribute(hwnd,DwmDarkMode,ref dark,sizeof(int));
    int acrylic=DesktopAcrylic; // DWMSBT_TRANSIENTWINDOW, real desktop blur
    if(DwmSetWindowAttribute(hwnd,DwmSystemBackdrop,ref acrylic,sizeof(int))<0)
      throw new InvalidOperationException("Windows 未接受 Desktop Acrylic 材质请求。");

    // Use a dark caption rather than an unrelated bright system accent color.
    int caption=0x00382215; // COLORREF BGR for #152238
    int captionText=0x00FCF5F0; // COLORREF BGR for #F0F5FC
    DwmSetWindowAttribute(hwnd,DwmCaptionColor,ref caption,sizeof(int));
    DwmSetWindowAttribute(hwnd,DwmCaptionTextColor,ref captionText,sizeof(int));

    Status="已请求 Windows 11 Desktop Acrylic（真实桌面模糊）；最终显示仍取决于系统透明设置及图形环境。";
    return true;
   }
   catch
   {
    int none=NoBackdrop;
    DwmSetWindowAttribute(hwnd,DwmSystemBackdrop,ref none,sizeof(int));
    if(extended)
    {
     var reset=new Margins();
     DwmExtendFrameIntoClientArea(hwnd,ref reset);
    }
    target.BackgroundColor=previousColor;
    throw;
   }
  }
  catch(Exception ex) when(ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or COMException)
  {
   Status="系统 Acrylic 未能启用："+ex.Message+"；已使用应用内回退玻璃层次。";
   return false;
  }
 }
}
