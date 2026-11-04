using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SpirePatch;

// A readable card with a check box and explicit, consistently aligned labels.
public sealed class ModCardControl : Border
{
 public static readonly RoutedEvent DetailsEvent=EventManager.RegisterRoutedEvent("CardDetails",RoutingStrategy.Bubble,typeof(RoutedEventHandler),typeof(ModCardControl));
 public static readonly RoutedEvent MoreEvent=EventManager.RegisterRoutedEvent("CardMore",RoutingStrategy.Bubble,typeof(RoutedEventHandler),typeof(ModCardControl));
 public static readonly RoutedEvent SelectEvent=EventManager.RegisterRoutedEvent("CardSelect",RoutingStrategy.Bubble,typeof(RoutedEventHandler),typeof(ModCardControl));
 public static readonly RoutedEvent ToggleEvent=EventManager.RegisterRoutedEvent("CardToggle",RoutingStrategy.Bubble,typeof(RoutedEventHandler),typeof(ModCardControl));
 public static readonly RoutedEvent UpdateEvent=EventManager.RegisterRoutedEvent("CardUpdate",RoutingStrategy.Bubble,typeof(RoutedEventHandler),typeof(ModCardControl));
 public CheckBox SelectBox{get;}=new(){Margin=new(0,0,9,0),VerticalAlignment=VerticalAlignment.Top};
 public Button MoreButton{get;}
 readonly TextBlock title=new(){FontSize=16,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,MaxHeight=50};
 readonly TextBlock status=new(){FontSize=12};
 readonly TextBlock id=new(),version=new(),author=new(),github=new(),depends=new();
 readonly TextBlock updateLabel;readonly Border stateBadge;
 readonly Button toggle,update;
 internal Action<ModListRow,bool>? SelectionChanged;
 internal Action<ModListRow>? DetailsRequested,ToggleRequested,UpdateRequested;
 internal Action<ModListRow,Button>? MoreRequested;
 bool setting;
 public ModCardControl()
 {
  Background=SpireGlassTheme.GlassPanel("#BE1A2D48");BorderBrush=SpireGlassTheme.PanelStroke();
  BorderThickness=new(1);CornerRadius=new(12);Padding=new(13);Margin=new(0,3,10,8);MinHeight=265;
  var root=new Grid();
  root.RowDefinitions.Add(new(){Height=GridLength.Auto});
  root.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});
  root.RowDefinitions.Add(new(){Height=GridLength.Auto});
  var top=new DockPanel();
  DockPanel.SetDock(SelectBox,Dock.Left);top.Children.Add(SelectBox);
  stateBadge=new Border{Background=Brushes.Transparent,Child=status,Padding=new Thickness(8,4,8,4),CornerRadius=new CornerRadius(6)};
  var state=stateBadge;
  DockPanel.SetDock(state,Dock.Right);top.Children.Add(state);
  var head=new StackPanel();
  head.Children.Add(new TextBlock{Text="模组名称",FontSize=12,Foreground=Brushes.LightGray});
  var detail=new Button{Content=title,Padding=new(0),Background=Brushes.Transparent,BorderThickness=new(0),HorizontalContentAlignment=HorizontalAlignment.Left};
  detail.Click+=(_,_)=>{if(DataContext is ModListRow row)DetailsRequested?.Invoke(row);};
  head.Children.Add(detail);top.Children.Add(head);root.Children.Add(top);
  var fields=new StackPanel{Margin=new(0,12,0,10)};
  fields.Children.Add(new Border{Height=1,Background=Brushes.SlateGray,Margin=new(0,0,0,9)});
  AddField(fields,"模组 ID",id);AddField(fields,"版本号",version);AddField(fields,"作者",author);
  updateLabel=AddField(fields,"更新状态",github);AddField(fields,"依赖情况",depends);
  Grid.SetRow(fields,1);root.Children.Add(fields);
  var foot=new StackPanel();foot.Children.Add(new Border{Height=1,Background=Brushes.SlateGray,Margin=new(0,0,0,8)});
  var actions=new Grid();
  actions.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
  actions.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
  actions.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
  toggle=MakeButton("停用模组");update=MakeButton("检查更新");MoreButton=MakeButton("更多");
  toggle.Click+=(_,_)=>{if(DataContext is ModListRow row)ToggleRequested?.Invoke(row);};
  update.Click+=(_,_)=>{if(DataContext is ModListRow row)UpdateRequested?.Invoke(row);};
  MoreButton.Click+=(_,_)=>{if(DataContext is ModListRow row)MoreRequested?.Invoke(row,MoreButton);};
  actions.Children.Add(toggle);Grid.SetColumn(update,1);actions.Children.Add(update);
  Grid.SetColumn(MoreButton,2);MoreButton.Margin=new Thickness(0,0,0,4);actions.Children.Add(MoreButton);
  foot.Children.Add(actions);Grid.SetRow(foot,2);root.Children.Add(foot);Child=root;
  SelectBox.Checked+=(_,_)=>Select(true);
  SelectBox.Unchecked+=(_,_)=>Select(false);
  DataContextChanged+=(_,_)=>Refresh();
 }
 static Button MakeButton(string name)=>new(){Content=name,FontSize=13,Padding=new(9,7,9,7),Margin=new(0,0,5,4),MinWidth=49};
 static TextBlock AddField(Panel panel,string label,TextBlock value)
 {
  var row=new Grid{Margin=new(0,3,0,3)};
  row.ColumnDefinitions.Add(new(){Width=new(82)});
  row.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
  var fieldName=new TextBlock{Text=label+"：",FontSize=14,Foreground=Brushes.LightGray};row.Children.Add(fieldName);
  value.FontSize=14;value.TextWrapping=TextWrapping.NoWrap;value.TextTrimming=TextTrimming.CharacterEllipsis;
  Grid.SetColumn(value,1);row.Children.Add(value);panel.Children.Add(row);return fieldName;
 }
 void Select(bool value){if(!setting&&DataContext is ModListRow row)SelectionChanged?.Invoke(row,value);}
 public void Refresh()
 {
  if(DataContext is not ModListRow row)return;
  title.Text=row.Name;title.ToolTip=row.Name;
  status.Text=row.State;status.Foreground=row.Mod.Enabled?Brushes.LightGreen:Brushes.LightGray;
  stateBadge.Background=new SolidColorBrush(row.Mod.Enabled?Color.FromRgb(31,75,66):Color.FromRgb(54,67,86));
  id.Text=row.Mod.Id;id.ToolTip=id.Text;
  version.Text=row.Mod.Version;version.ToolTip=version.Text;
  author.Text=row.Mod.Author;author.ToolTip=author.Text;
  updateLabel.Text=row.IsFix?"补丁健康：":"更新状态：";
  github.Text=row.IsFix?row.Health:string.IsNullOrEmpty(row.GitHubText)?"未检查":row.GitHubText.Replace("GitHub：","");
  github.ToolTip=row.GitHubText;
  const string dep="这些启用项依赖它，请先停用依赖项：";
  depends.Text=row.BlockReason?.StartsWith(dep,StringComparison.Ordinal)==true?
   row.BlockReason[dep.Length..].Split('、',StringSplitOptions.RemoveEmptyEntries).Length+" 项已启用 MOD 依赖此模组":
   row.Mod.Dependencies.Length==0?"未声明前置模组":"已声明 "+row.Mod.Dependencies.Length+" 项前置模组";
  if(row.BlockReason!=null&&!row.BlockReason.StartsWith(dep,StringComparison.Ordinal))depends.Text=row.BlockReason;
  depends.ToolTip=row.BlockReason??depends.Text;
  depends.Foreground=row.BlockReason!=null?Brushes.Khaki:Brushes.White;
  toggle.Content=row.Mod.Enabled?"停用模组":"启用模组";
  toggle.IsEnabled=row.BlockReason==null;toggle.ToolTip=row.BlockReason;
  update.Content=row.IsFix?"查看健康":row.GitHubText.Contains("有更新")?"下载并更新":"检查更新";
  setting=true;SelectBox.IsChecked=row.Selected;setting=false;
  BorderBrush=row.Selected?Brushes.SkyBlue:SpireGlassTheme.PanelStroke();
  BorderThickness=new Thickness(row.Selected?2:1);
 }
}
