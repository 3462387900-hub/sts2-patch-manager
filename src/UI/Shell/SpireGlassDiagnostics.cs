using System.Windows;
using System.Windows.Controls;

namespace SpirePatch;

// A temporary *visual test*, not a user setting. It cannot change MOD files or safety rules.
public partial class MainWindow
{
 bool nativeAcrylicActive;
 bool bareGlassTestActive;
 Border? glassSidebarSurface;
 Canvas? glassAmbientSurface;

 void SetBareGlassTest(bool enabled,bool refreshPage=true)
 {
  bareGlassTestActive=enabled&&nativeAcrylicActive;
  shell.Background=SpireGlassTheme.Background(nativeAcrylicActive,bareGlassTestActive);
  if(glassSidebarSurface!=null)
   glassSidebarSurface.Background=B(bareGlassTestActive?"#14162A45":"#A0102038");
  if(glassAmbientSurface!=null)
   glassAmbientSurface.Visibility=bareGlassTestActive?Visibility.Collapsed:Visibility.Visible;
  if(refreshPage&&page=="工具与设置")ShowTools();
 }
}
