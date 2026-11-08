using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SpirePatch;

// Classic beta preserves the v0.13 card's information density and three visible actions.
// All actions reuse ModCardControl's callbacks; no file writes happen in card handlers.
public sealed class ClassicModCardControl : ModCardControl
{
 readonly TextBlock heading=new(){FontSize=15,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,MaxHeight=42};
 readonly TextBlock state=new(){FontSize=11,FontWeight=FontWeights.SemiBold};
 readonly TextBlock metadata=new(){FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis};
 readonly TextBlock updateState=new(){FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis};
 readonly TextBlock dependencies=new(){FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis};
 readonly Border badge;
 readonly Button toggle,update;
 static Brush Paint(string code)=>(Brush)new BrushConverter().ConvertFromString(code)!;
 static readonly Brush Secondary=Paint("#C4D3E5");

 public ClassicModCardControl()
 {
  // Two shared controls already have their events wired by the base constructor.
  if(Child is Grid oldRoot)
  {
   foreach(var container in oldRoot.Children.OfType<Grid>().ToArray())
   {
    container.Children.Remove(SelectBox);
    container.Children.Remove(MoreButton);
   }
  }
  Child=null;
  Background=Paint("#B21A2D48");
  BorderBrush=SpireGlassTheme.PanelStroke();
  CornerRadius=new CornerRadius(12);
  Padding=new Thickness(11,9,11,9);
  Margin=new Thickness(0,2,8,6);
  MinHeight=156;

  var grid=new Grid();
  grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
  grid.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
  grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
  var top=new DockPanel{LastChildFill=true};
  SelectBox.Margin=new Thickness(0,0,7,0);
  SelectBox.VerticalAlignment=VerticalAlignment.Top;
  DockPanel.SetDock(SelectBox,Dock.Left);top.Children.Add(SelectBox);
  badge=new Border{Child=state,Padding=new Thickness(7,3,7,3),CornerRadius=new CornerRadius(6),
   Margin=new Thickness(5,0,0,0),VerticalAlignment=VerticalAlignment.Top};
  DockPanel.SetDock(badge,Dock.Right);top.Children.Add(badge);
  var detail=new Button{Content=heading,Padding=new Thickness(0),
   Background=Brushes.Transparent,BorderThickness=new Thickness(0),
   HorizontalContentAlignment=HorizontalAlignment.Left,ToolTip="查看模组详情"};
  detail.Click+=(_,_)=>{if(DataContext is ModListRow row)DetailsRequested?.Invoke(row);};
  top.Children.Add(detail);grid.Children.Add(top);

  var fields=new StackPanel{Margin=new Thickness(0,6,0,5)};
  foreach(var line in new[]{metadata,updateState,dependencies})line.Foreground=Secondary;
  updateState.Margin=new Thickness(0,3,0,0);
  dependencies.Margin=new Thickness(0,3,0,0);
  fields.Children.Add(metadata);fields.Children.Add(updateState);fields.Children.Add(dependencies);
  Grid.SetRow(fields,1);grid.Children.Add(fields);

  var actions=new Grid();
  actions.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
  actions.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
  actions.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
  toggle=Make("停用");update=Make("检查更新");
  MoreButton.Content="更多";MoreButton.FontSize=12;MoreButton.Margin=new Thickness(0);
  MoreButton.Padding=new Thickness(8,5,8,5);
  MoreButton.MinWidth=45;MoreButton.MinHeight=30;
  if(Application.Current?.TryFindResource("ModCardAction") is Style moreStyle)MoreButton.Style=moreStyle;
  toggle.Click+=(_,_)=>{if(DataContext is ModListRow row)ToggleRequested?.Invoke(row);};
  update.Click+=(_,_)=>{if(DataContext is ModListRow row)UpdateRequested?.Invoke(row);};
  actions.Children.Add(toggle);
  Grid.SetColumn(update,1);update.HorizontalAlignment=HorizontalAlignment.Stretch;actions.Children.Add(update);
  Grid.SetColumn(MoreButton,2);actions.Children.Add(MoreButton);
  Grid.SetRow(actions,2);grid.Children.Add(actions);
  Child=grid;
  DataContextChanged+=(_,_)=>RefreshClassic();
 }
 static Button Make(string caption)
 {
  var button=new Button{Content=caption,FontSize=12,
   Padding=new Thickness(8,5,8,5),Margin=new Thickness(0,0,5,0),MinWidth=45,MinHeight=30};
  if(Application.Current?.TryFindResource("ModCardAction") is Style style)button.Style=style;
  return button;
 }
 void RefreshClassic()
 {
  if(DataContext is not ModListRow row)return;
  heading.Text=row.Name;
  heading.ToolTip="模组名称："+row.Name+"\n模组 ID："+row.Mod.Id;
  state.Text=row.State;
  state.Foreground=row.Mod.Enabled?Brushes.LightGreen:Brushes.LightGray;
  badge.Background=new SolidColorBrush(row.Mod.Enabled?Color.FromArgb(118,31,75,66):Color.FromArgb(114,54,67,86));
  metadata.Text="版本号："+row.Mod.Version+"  ·  作者："+row.Mod.Author;
  metadata.ToolTip="模组 ID："+row.Mod.Id+"\n版本号："+row.Mod.Version+"\n作者："+row.Mod.Author;
  updateState.Text=row.IsFix?"补丁健康："+row.Health:
   "更新状态："+(string.IsNullOrWhiteSpace(row.GitHubText)?"未检查":row.GitHubText.Replace("GitHub：",""));
  updateState.ToolTip=updateState.Text;
  bool relevant=row.DependencyIssue||row.BlockReason!=null||row.Mod.Dependencies.Length>0||
   (!string.IsNullOrWhiteSpace(row.DependencyText)&&row.DependencyText!="未分析");
  dependencies.Visibility=relevant?Visibility.Visible:Visibility.Collapsed;
  dependencies.Text="依赖情况："+(row.BlockReason!=null?ModListView.BriefBlockReason(row.BlockReason):
   row.DependencyText.Length>0&&row.DependencyText!="未分析"?row.DependencyText:
   row.Mod.Dependencies.Length>0?"已声明 "+row.Mod.Dependencies.Length+" 项前置模组":"未声明前置");
  dependencies.ToolTip=string.Join("\n",new[]{row.BlockReason,row.DependencyText}
   .Where(text=>!string.IsNullOrWhiteSpace(text)));
  dependencies.Foreground=row.BlockReason!=null||row.DependencyIssue?Brushes.Khaki:Secondary;
  toggle.Content=row.Mod.Enabled?"停用":"启用";
  toggle.IsEnabled=row.BlockReason==null;toggle.ToolTip=row.BlockReason;
  update.Content=row.IsFix?"查看健康":row.GitHubText.Contains("有更新")?"下载并更新":"检查更新";
 }
}
