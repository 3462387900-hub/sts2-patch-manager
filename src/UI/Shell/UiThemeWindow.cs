using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SpirePatch;

public partial class MainWindow
{
 string uiTheme=UiThemes.Workspace;
 Button? neonThemeButton,classicThemeButton,workspaceThemeButton;

 UIElement ThemeSwitcher()
 {
  var root=V();
  var heading=T("界面风格  ·  BETA",11,muted);
  heading.Margin=new Thickness(2,0,0,7);
  root.Children.Add(heading);
  neonThemeButton=Btn("Neon 工作台（可选）",()=>ChangeUiTheme(UiThemes.Neon),"ModSegment");
  neonThemeButton.ToolTip="Neon 工作台：暖纸质工作台、果冻开关直启停与因果依恋检视";
  neonThemeButton.FontSize=12;neonThemeButton.Padding=new Thickness(8,7,8,7);
  neonThemeButton.HorizontalAlignment=HorizontalAlignment.Stretch;
  neonThemeButton.Margin=new Thickness(0,0,0,4);
  var buttons=new Grid();
  buttons.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
  buttons.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
  classicThemeButton=Btn("经典 Beta",()=>ChangeUiTheme(UiThemes.Classic),"ModSegment");
  classicThemeButton.ToolTip="经典 Beta：v0.13.1 风格的按钮与完整信息卡片";
  classicThemeButton.FontSize=12;classicThemeButton.Padding=new Thickness(8,7,8,7);
  workspaceThemeButton=Btn("新版 Beta",()=>ChangeUiTheme(UiThemes.Workspace),"ModSegment");
  workspaceThemeButton.ToolTip="新版 Beta：轻量标签、组合筛选与浮层详情";
  workspaceThemeButton.FontSize=12;workspaceThemeButton.Padding=new Thickness(8,7,8,7);
  var classic=classicThemeButton;
  var modern=workspaceThemeButton;
  buttons.Children.Add(classic);
  Grid.SetColumn(modern,1);buttons.Children.Add(modern);
  var group=new StackPanel();
  group.Children.Add(neonThemeButton);
  group.Children.Add(buttons);
  root.Children.Add(new Border{Child=group,Background=new SolidColorBrush(Color.FromArgb(100,38,57,82)),
   CornerRadius=new CornerRadius(9),Padding=new Thickness(3)});
  var note=T("切换立即生效 · 不影响 MOD 文件",10,muted);
  note.Margin=new Thickness(2,7,0,0);
  root.Children.Add(note);
  RefreshThemeButtons();
  return new Border{Child=root,Margin=new Thickness(2,15,2,0)};
 }

 void RefreshThemeButtons()
 {
  if(neonThemeButton!=null)
   neonThemeButton.Style=(Style)FindResource(uiTheme==UiThemes.Neon?"ModSegmentActive":"ModSegment");
  if(classicThemeButton!=null)
   classicThemeButton.Style=(Style)FindResource(uiTheme==UiThemes.Classic?"ModSegmentActive":"ModSegment");
  if(workspaceThemeButton!=null)
   workspaceThemeButton.Style=(Style)FindResource(uiTheme==UiThemes.Workspace?"ModSegmentActive":"ModSegment");
 }

 void ChangeUiTheme(string theme)
 {
  if(theme==uiTheme)return;
  // Do not let an unresolved Neon recovery warning be bypassed by switching
  // to an old layout with different write entry points.
  if(UiThemes.IsNeon&&selectedInstallRoot is string root&&NeonRecoveryBlocked(root))
  {
   neonWorkbenchView?.ShowToast("存在未解决的事务恢复异常，恢复完成前不能切换至旧界面。",NeonTone.Amber);
   return;
  }
  if(busy||transactionWriting)
  {
   if(UiThemes.IsNeon)neonWorkbenchView?.ShowToast("文件操作进行中，暂不能切换界面布局。",NeonTone.Amber);
   else toast.Text="文件操作进行中，暂不能切换界面布局。";
   return;
  }
  try
  {
   if(rememberEnvironment)UiThemes.Save(installer.DataRoot,theme);
  }
  catch(Exception e)
  {
   if(UiThemes.IsNeon)neonWorkbenchView?.ShowToast("主题偏好保存失败，未切换："+e.Message,NeonTone.Cinnabar);
   else toast.Text="主题偏好保存失败，未切换："+e.Message;
   return;
  }
  bool wasNeon=uiTheme==UiThemes.Neon;
  if(wasNeon)NeonCancelOwnedPreview();
  uiTheme=theme;
  UiThemes.Active=theme;
  selectedModDetailFolder=null;
  RefreshThemeButtons();
  if(UiThemes.IsNeon)
  {
   ShowNeonWorkbench();
   neonWorkbenchView?.ShowToast("已切换到 Neon 工作台；MOD 与补丁文件没有变化。");
   return;
  }
  if(wasNeon)
  {
   Background=Brushes.Transparent;
   TextOptions.SetTextFormattingMode(this,TextFormattingMode.Display);
   TextOptions.SetTextRenderingMode(this,TextRenderingMode.Grayscale);
   Title="尖塔修补站 · "+ManagerUpdates.CurrentVersion;
   if(!nativeAcrylicActive){nativeAcrylicActive=SpireGlassBackdrop.Enable(this);SetBareGlassTest(false,false);}
   Content=shell;
  }
  if(backButton!=null)
   backButton.Style=(Style)FindResource(UiThemes.IsClassic?"Ghost":"ModToolbarAction");
  if(page=="模组管理"||page=="本地补丁")ShowMods();
  else if(page=="工具与设置")ShowTools();
  else if(IsPatchCenterPage(page))Navigate(page);
  else ShowOverview();
  toast.Text="已切换到"+(UiThemes.IsClassic?"经典 Beta":"新版工作台 Beta")+"；MOD 与补丁文件没有变化。";
 }
}
