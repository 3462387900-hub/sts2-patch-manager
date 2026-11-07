using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SpirePatch;

public partial class MainWindow
{
 string uiTheme=UiThemes.Workspace;
 Button? classicThemeButton,workspaceThemeButton,atlasThemeButton;

 UIElement ThemeSwitcher()
 {
  var root=V();
  var heading=T("界面风格  ·  BETA",11,muted);
  heading.Margin=new Thickness(2,0,0,7);
  root.Children.Add(heading);
  var buttons=new Grid();
  for(int i=0;i<3;i++)buttons.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
  classicThemeButton=Btn("经典",()=>ChangeUiTheme(UiThemes.Classic),"ModSegment");
  classicThemeButton.ToolTip="经典 Beta：v0.13 风格";
  workspaceThemeButton=Btn("新版",()=>ChangeUiTheme(UiThemes.Workspace),"ModSegment");
  workspaceThemeButton.ToolTip="新版工作台：v0.14 风格";
  atlasThemeButton=Btn("ATLAS",()=>ChangeUiTheme(UiThemes.Atlas),"ModSegment");
  atlasThemeButton.ToolTip="ATLAS：WebView2 实验性界面，第一阶段只读";
  foreach(var b in new[]{classicThemeButton,workspaceThemeButton,atlasThemeButton})
  {b!.FontSize=11;b.Padding=new Thickness(4,7,4,7);}
  buttons.Children.Add(classicThemeButton);
  Grid.SetColumn(workspaceThemeButton,1);buttons.Children.Add(workspaceThemeButton);
  Grid.SetColumn(atlasThemeButton,2);buttons.Children.Add(atlasThemeButton);
  root.Children.Add(new Border{Child=buttons,Background=new SolidColorBrush(Color.FromArgb(100,38,57,82)),
   CornerRadius=new CornerRadius(9),Padding=new Thickness(3)});
  var note=T("切换立即生效 · 不影响 MOD 文件",10,muted);
  note.Margin=new Thickness(2,7,0,0);
  root.Children.Add(note);
  RefreshThemeButtons();
  return new Border{Child=root,Margin=new Thickness(2,15,2,0)};
 }

 void RefreshThemeButtons()
 {
  if(classicThemeButton!=null)
   classicThemeButton.Style=(Style)FindResource(uiTheme==UiThemes.Classic?"ModSegmentActive":"ModSegment");
  if(workspaceThemeButton!=null)
   workspaceThemeButton.Style=(Style)FindResource(uiTheme==UiThemes.Workspace?"ModSegmentActive":"ModSegment");
  if(atlasThemeButton!=null)
   atlasThemeButton.Style=(Style)FindResource(uiTheme==UiThemes.Atlas?"ModSegmentActive":"ModSegment");
 }

 async void ChangeUiTheme(string theme)
 {
  if(theme==uiTheme)return;
  if(busy||transactionWriting)
  {
   toast.Text="文件操作进行中，暂不能切换界面布局。";
   return;
  }
  try
  {
   if(rememberEnvironment)UiThemes.Save(installer.DataRoot,theme);
  }
  catch(Exception e)
  {
   toast.Text="主题偏好保存失败，未切换："+e.Message;
   return;
  }
  string previous=uiTheme;
  uiTheme=theme;
  UiThemes.Active=theme;
  if(theme==UiThemes.Atlas)
  {
   RefreshThemeButtons();
   await ShowAtlasAsync();
   return;
  }
  if(previous==UiThemes.Atlas)Content=shell;
  selectedModDetailFolder=null;
  RefreshThemeButtons();
  if(backButton!=null)
   backButton.Style=(Style)FindResource(UiThemes.IsClassic?"Ghost":"ModToolbarAction");
  if(page=="模组管理"||page=="本地补丁")ShowMods();
  else if(page=="工具与设置")ShowTools();
  else if(IsPatchCenterPage(page))Navigate(page);
  else ShowOverview();
  toast.Text="已切换到"+(UiThemes.IsClassic?"经典 Beta":"新版工作台 Beta")+"；MOD 与补丁文件没有变化。";
 }
}
