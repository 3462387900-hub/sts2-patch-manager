using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SpirePatch;

// Compact, virtualized MOD card: keep common actions visible and move full metadata to details.
public sealed class ModCardControl : Border
{
 public static readonly RoutedEvent DetailsEvent=EventManager.RegisterRoutedEvent("CardDetails",RoutingStrategy.Bubble,typeof(RoutedEventHandler),typeof(ModCardControl));
 public static readonly RoutedEvent MoreEvent=EventManager.RegisterRoutedEvent("CardMore",RoutingStrategy.Bubble,typeof(RoutedEventHandler),typeof(ModCardControl));
 public static readonly RoutedEvent SelectEvent=EventManager.RegisterRoutedEvent("CardSelect",RoutingStrategy.Bubble,typeof(RoutedEventHandler),typeof(ModCardControl));
 public static readonly RoutedEvent ToggleEvent=EventManager.RegisterRoutedEvent("CardToggle",RoutingStrategy.Bubble,typeof(RoutedEventHandler),typeof(ModCardControl));
 public static readonly RoutedEvent UpdateEvent=EventManager.RegisterRoutedEvent("CardUpdate",RoutingStrategy.Bubble,typeof(RoutedEventHandler),typeof(ModCardControl));
 public CheckBox SelectBox{get;}=new(){Margin=new(0,0,7,0),VerticalAlignment=VerticalAlignment.Top,ToolTip="勾选以批量操作模组"};
 public Button MoreButton{get;}
 readonly TextBlock title=new(){FontSize=15,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,MaxHeight=42};
 readonly TextBlock status=new(){FontSize=11,FontWeight=FontWeights.SemiBold};
 readonly TextBlock metadata=new(){FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis};
 readonly TextBlock updateState=new(){FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis};
 readonly TextBlock depends=new(){FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis};
 readonly Border stateBadge;
 readonly Button toggle,update;
 internal Action<ModListRow,bool>? SelectionChanged;
 internal Action<ModListRow>? DetailsRequested,ToggleRequested,UpdateRequested;
 internal Action<ModListRow,Button>? MoreRequested;
 bool setting;
 static Brush Paint(string hex)=>(Brush)new BrushConverter().ConvertFromString(hex)!;
 static readonly Brush NormalCardStroke=SpireGlassTheme.PanelStroke();
 static readonly Brush HoverCardStroke=Paint("#468CB3D7");
 static readonly Brush SelectedCardStroke=Paint("#7B85B8E8");
 static readonly Brush NormalCardFill=Paint("#B21A2D48");
 static readonly Brush SelectedCardFill=Paint("#C129405C");
 static readonly Brush SecondaryText=Paint("#C4D3E5");
 public ModCardControl()
 {
  Background=NormalCardFill;BorderBrush=NormalCardStroke;
  BorderThickness=new(1);CornerRadius=new(12);Padding=new(11,9,11,9);
  Margin=new(0,2,8,6);MinHeight=156;
  SnapsToDevicePixels=false;UseLayoutRounding=false;
  if(Application.Current?.TryFindResource("ModCardCheckBox") is Style checkStyle)SelectBox.Style=checkStyle;

  var root=new Grid();
  root.RowDefinitions.Add(new(){Height=GridLength.Auto});
  root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
  root.RowDefinitions.Add(new(){Height=GridLength.Auto});

  var top=new DockPanel{LastChildFill=true};
  DockPanel.SetDock(SelectBox,Dock.Left);top.Children.Add(SelectBox);
  stateBadge=new Border{Background=Brushes.Transparent,Child=status,
   Padding=new Thickness(7,3,7,3),CornerRadius=new CornerRadius(6),
   VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(5,0,0,0)};
  DockPanel.SetDock(stateBadge,Dock.Right);top.Children.Add(stateBadge);
  var detail=new Button{Content=title,Padding=new Thickness(0),
   Background=Brushes.Transparent,BorderThickness=new Thickness(0),
   HorizontalContentAlignment=HorizontalAlignment.Left,VerticalContentAlignment=VerticalAlignment.Center,
   ToolTip="查看模组详情"};
  detail.Click+=(_,_)=>{if(DataContext is ModListRow row)DetailsRequested?.Invoke(row);};
  top.Children.Add(detail);
  root.Children.Add(top);

  var summary=new StackPanel{Margin=new Thickness(0,6,0,5)};
  metadata.Foreground=SecondaryText;
  updateState.Foreground=SecondaryText;updateState.Margin=new Thickness(0,3,0,0);
  depends.Foreground=SecondaryText;depends.Margin=new Thickness(0,3,0,0);
  summary.Children.Add(metadata);
  summary.Children.Add(updateState);
  summary.Children.Add(depends);
  Grid.SetRow(summary,1);root.Children.Add(summary);

  var actions=new Grid();
  actions.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
  actions.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
  actions.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
  toggle=MakeButton("停用");update=MakeButton("检查更新");MoreButton=MakeButton("更多");
  toggle.Click+=(_,_)=>{if(DataContext is ModListRow row)ToggleRequested?.Invoke(row);};
  update.Click+=(_,_)=>{if(DataContext is ModListRow row)UpdateRequested?.Invoke(row);};
  MoreButton.Click+=(_,_)=>{if(DataContext is ModListRow row)MoreRequested?.Invoke(row,MoreButton);};
  actions.Children.Add(toggle);
  Grid.SetColumn(update,1);update.HorizontalAlignment=HorizontalAlignment.Stretch;actions.Children.Add(update);
  Grid.SetColumn(MoreButton,2);MoreButton.Margin=new Thickness(0);actions.Children.Add(MoreButton);
  Grid.SetRow(actions,2);root.Children.Add(actions);
  Child=root;

  SelectBox.Checked+=(_,_)=>Select(true);
  SelectBox.Unchecked+=(_,_)=>Select(false);
  DataContextChanged+=(_,_)=>Refresh();
  MouseEnter+=(_,_)=>{if(DataContext is ModListRow row&&!row.Selected)BorderBrush=HoverCardStroke;};
  MouseLeave+=(_,_)=>UpdateSelectionVisual();
 }
 static Button MakeButton(string name)
 {
  var button=new Button{Content=name,FontSize=12,Padding=new Thickness(8,5,8,5),
   Margin=new Thickness(0,0,5,0),MinWidth=45,MinHeight=30};
  if(Application.Current?.TryFindResource("ModCardAction") is Style style)button.Style=style;
  return button;
 }
 void Select(bool value)
 {
  if(setting||DataContext is not ModListRow row)return;
  row.Selected=value;
  UpdateSelectionVisual();
  SelectionChanged?.Invoke(row,value);
 }
 public void UpdateSelectionVisual()
 {
  if(DataContext is not ModListRow row)return;
  BorderBrush=row.Selected?SelectedCardStroke:IsMouseOver?HoverCardStroke:NormalCardStroke;
  Background=row.Selected?SelectedCardFill:NormalCardFill;
 }
 public void Refresh()
 {
  if(DataContext is not ModListRow row)return;
  title.Text=row.Name;
  title.ToolTip="模组名称："+row.Name+"\n模组 ID："+row.Mod.Id;
  status.Text=row.State;status.Foreground=row.Mod.Enabled?Brushes.LightGreen:Brushes.LightGray;
  stateBadge.Background=new SolidColorBrush(row.Mod.Enabled?Color.FromArgb(118,31,75,66):Color.FromArgb(114,54,67,86));
  metadata.Text="版本号："+row.Mod.Version+"  ·  作者："+row.Mod.Author;
  metadata.ToolTip="模组 ID："+row.Mod.Id+"\n版本号："+row.Mod.Version+"\n作者："+row.Mod.Author;
  updateState.Text=row.IsFix?"补丁健康："+row.Health:
   "更新状态："+(string.IsNullOrEmpty(row.GitHubText)?"未检查":row.GitHubText.Replace("GitHub：",""));
  updateState.ToolTip=updateState.Text;
  bool hasDependency=row.DependencyIssue||row.BlockReason!=null||
   row.Mod.Dependencies.Length>0||
   (!string.IsNullOrWhiteSpace(row.DependencyText)&&row.DependencyText!="未分析");
  depends.Visibility=hasDependency?Visibility.Visible:Visibility.Collapsed;
  depends.Text="依赖情况："+(row.BlockReason!=null?ModListView.BriefBlockReason(row.BlockReason):
   row.DependencyText.Length>0&&row.DependencyText!="未分析"?row.DependencyText:
   row.Mod.Dependencies.Length>0?"已声明 "+row.Mod.Dependencies.Length+" 项前置模组":"未声明前置");
  depends.ToolTip=string.Join("\n",new[]{row.BlockReason,row.DependencyText}
   .Where(value=>!string.IsNullOrWhiteSpace(value)));
  depends.Foreground=row.BlockReason!=null||row.DependencyIssue?Brushes.Khaki:SecondaryText;
  toggle.Content=row.Mod.Enabled?"停用":"启用";
  toggle.IsEnabled=row.BlockReason==null;toggle.ToolTip=row.BlockReason;
  update.Content=row.IsFix?"查看健康":row.GitHubText.Contains("有更新")?"下载并更新":"检查更新";
  setting=true;SelectBox.IsChecked=row.Selected;setting=false;
  UpdateSelectionVisual();
 }
}
