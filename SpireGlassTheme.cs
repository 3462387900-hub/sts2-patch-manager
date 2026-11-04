using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace SpirePatch;

/// <summary>Visual glass layering shared by native Acrylic and an opaque fallback.</summary>
internal static class SpireGlassTheme
{
 internal static Brush Background(bool nativeAcrylic)
 {
  // On Windows 11, the tint must remain substantially translucent:
  // 0xA0 (~63%) instead of the former 0xE8 (~91%) that hid the native blur.
  if(nativeAcrylic)
   return new LinearGradientBrush(Color.FromArgb(160,11,19,34),
    Color.FromArgb(173,21,34,55),58);
  // Without DWM Acrylic never leave a transparent WPF window exposing black pixels.
  return new LinearGradientBrush(Color.FromRgb(12,21,38),
   Color.FromRgb(27,43,69),58);
 }

 internal static Brush GlassPanel(string? color=null)
 {
  if(color!=null)return (Brush)new BrushConverter().ConvertFromString(color)!;
  return new LinearGradientBrush(Color.FromArgb(219,29,46,71),
   Color.FromArgb(199,19,33,55),110);
 }

 internal static Brush PanelStroke()=>new LinearGradientBrush(
  Color.FromArgb(94,196,222,255),Color.FromArgb(48,108,143,188),90);

 static Ellipse Halo(double width,double height,Color color,double opacity)
 {
  var fill=new RadialGradientBrush{
   Center=new Point(.5,.5),GradientOrigin=new Point(.5,.5),
   RadiusX=.5,RadiusY=.5,
   GradientStops=new GradientStopCollection{
    new GradientStop(Color.FromArgb(205,color.R,color.G,color.B),0),
    new GradientStop(Color.FromArgb(62,color.R,color.G,color.B),.53),
    new GradientStop(Color.FromArgb(0,color.R,color.G,color.B),1)
   }
  };
  return new Ellipse{
   Width=width,Height=height,Fill=fill,Opacity=opacity,
   Effect=new BlurEffect{Radius=28,RenderingBias=RenderingBias.Performance},
   CacheMode=new BitmapCache()
  };
 }

 internal static void AddAmbientGlow(Grid root)
 {
  // Static, non-interactive blurred lights make translucent surfaces legible and
  // visibly layered even on Windows versions that cannot provide Desktop Acrylic.
  var ambient=new Canvas{IsHitTestVisible=false,ClipToBounds=true};
  Grid.SetColumnSpan(ambient,2);
  var blue=Halo(650,490,Color.FromRgb(73,118,199),.58);
  Canvas.SetRight(blue,-140);Canvas.SetTop(blue,-190);ambient.Children.Add(blue);
  var teal=Halo(620,510,Color.FromRgb(38,115,151),.44);
  Canvas.SetLeft(teal,55);Canvas.SetBottom(teal,-265);ambient.Children.Add(teal);
  var violet=Halo(410,380,Color.FromRgb(124,85,175),.27);
  Canvas.SetRight(violet,-195);Canvas.SetBottom(violet,-145);ambient.Children.Add(violet);
  root.Children.Add(ambient); // Added first so it never covers interactive UI.
 }
}
