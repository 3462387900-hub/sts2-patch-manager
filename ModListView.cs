using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SpirePatch;

// Immutable, UI-only data. Safe enable/disable still goes through ModManager.Preview/Apply.
internal sealed record ModListRow(ManagedMod Mod,string Health,string? BlockReason,string GitHubText="",string DependencyText="",bool DependencyIssue=false)
{
 public bool Selected{get;set;}
 internal bool IsFix=>ModPresentation.IsIndependentPatch(Mod);
 public string Name=>Mod.Name;
 public string Details=>"模组 ID："+Mod.Id+"    版本号："+Mod.Version+"    作者："+Mod.Author;
 public string State=>Mod.Enabled?"已启用":"已停用";
 public string HealthText=>IsFix?"补丁健康："+Health:"";
 public string WarningText=>BlockReason==null?"":ModListView.BriefBlockReason(BlockReason);
 public bool IsDependencyProtection=>BlockReason?.StartsWith("这些启用项依赖它，请先停用依赖项：",StringComparison.Ordinal)==true;
}

internal static class ModListView
{
 internal static ModListRow[] Rows(string root,ModInventory inventory,Func<string,string> health,
  Func<ManagedMod,string>? github=null,Func<ManagedMod,string>? dependency=null,
  Func<ManagedMod,bool>? dependencyIssue=null)=>
  inventory.Mods.Select(mod=>new ModListRow(mod,
   ModPresentation.IsIndependentPatch(mod)?health(mod.Id):"",
   ModManager.BlockReason(root,mod,inventory),github?.Invoke(mod)??"",
   dependency?.Invoke(mod)??"未分析",dependencyIssue?.Invoke(mod)??false))
  .OrderBy(row=>row.Name,StringComparer.OrdinalIgnoreCase).ToArray();

 // State (enabled/disabled) and attention filters compose instead of competing.
 // Preserve the original four-argument API for existing callers.
 internal static ModListRow[] Filter(IReadOnlyList<ModListRow> rows,string section,string state,string query,
  string attention="所有状态")=>
  rows.Where(row=>row.IsFix==(section=="修复补丁")&&
   (state=="全部"||(state=="启用"&&row.Mod.Enabled)||(state=="停用"&&!row.Mod.Enabled)||
   (state=="有更新"&&row.GitHubText.Contains("有更新",StringComparison.Ordinal))||
   (state=="有问题"&&row.BlockReason!=null)||
   (state=="依赖异常"&&row.DependencyIssue))&&
   (attention=="所有状态"||
    (attention=="有更新"&&row.GitHubText.Contains("有更新",StringComparison.Ordinal))||
    (attention=="有问题"&&row.BlockReason!=null)||
    (attention=="依赖异常"&&row.DependencyIssue))&&
   (row.Mod.Name+" "+row.Mod.Id+" "+row.Mod.Author).Contains(query,StringComparison.OrdinalIgnoreCase))
  .ToArray();

 internal static int ColumnCount(double width)=>width>=1080?3:width>=700?2:1;
 internal static string BriefBlockReason(string reason)
 {
  const string deps="这些启用项依赖它，请先停用依赖项：";
  if(reason.StartsWith(deps,StringComparison.Ordinal))
  {
   int total=reason[deps.Length..].Split('、',StringSplitOptions.RemoveEmptyEntries).Length;
   return $"被 {total} 个已启用 MOD 依赖";
  }
  return reason.Length>78?"操作受限 · "+reason[..74]+"…":"操作受限 · "+reason;
 }

 internal static ListBox Create(Action<ModListRow> details,Action<ModListRow,Button> more)
 {
  var list=new ListBox{
   Background=Brushes.Transparent,BorderThickness=new Thickness(0),
   HorizontalContentAlignment=HorizontalAlignment.Stretch,
   HorizontalAlignment=HorizontalAlignment.Stretch,
   SelectionMode=SelectionMode.Single,MinHeight=130
  };
  VirtualizingPanel.SetIsVirtualizing(list,true);
  VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);
  ScrollViewer.SetCanContentScroll(list,true);
  ScrollViewer.SetVerticalScrollBarVisibility(list,ScrollBarVisibility.Auto);
  // Without this, an extremely long dependency message gives a row infinite width,
  // measuring the action buttons beyond the visible right edge of the list.
  ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);
  var itemStyle=new Style(typeof(ListBoxItem));
  itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch));
  itemStyle.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0)));
  itemStyle.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.Transparent));
  itemStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(0)));
  list.ItemContainerStyle=itemStyle;
  list.ItemsPanel=new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
  list.ItemTemplate=new DataTemplate(typeof(ModListRow)){VisualTree=new FrameworkElementFactory(typeof(ModRowControl))};
  list.AddHandler(ModRowControl.DetailsRequestedEvent,new RoutedEventHandler((_,e)=>{
   if(e.OriginalSource is ModRowControl view&&view.DataContext is ModListRow row)details(row);
  }));
  list.AddHandler(ModRowControl.MoreRequestedEvent,new RoutedEventHandler((_,e)=>{
   if(e.OriginalSource is ModRowControl view&&view.DataContext is ModListRow row)more(row,view.MoreButton);
  }));
  list.SelectionChanged+=(_,_)=>{if(list.SelectedItem!=null)list.SelectedItem=null;};
  return list;
 }
}

// A real Grid enforces a finite text column and permanently reserves width for actions.
// The virtualizing ItemsControl only creates this control for rows currently on screen.
public sealed class ModRowControl : Border
{
 internal static readonly RoutedEvent DetailsRequestedEvent=EventManager.RegisterRoutedEvent(
  "DetailsRequested",RoutingStrategy.Bubble,typeof(RoutedEventHandler),typeof(ModRowControl));
 internal static readonly RoutedEvent MoreRequestedEvent=EventManager.RegisterRoutedEvent(
  "MoreRequested",RoutingStrategy.Bubble,typeof(RoutedEventHandler),typeof(ModRowControl));

 readonly TextBlock title=new(){FontSize=16,FontWeight=FontWeights.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis};
 readonly TextBlock subtitle=new(){FontSize=14,Foreground=Paint("#B3C5DB"),TextTrimming=TextTrimming.CharacterEllipsis};
 readonly TextBlock health=new(){FontSize=11,Foreground=Paint("#9AD7BC"),TextTrimming=TextTrimming.CharacterEllipsis};
 // One compact, ellipsized context line keeps GitHub and dependency details visible
 // without forcing the entire row to grow to four or five lines.
 readonly TextBlock context=new(){FontSize=11,Foreground=Paint("#A8BCD4"),TextTrimming=TextTrimming.CharacterEllipsis};
 readonly TextBlock status=new(){FontSize=11,FontWeight=FontWeights.SemiBold};
 readonly Border statusPill;
 readonly Border stripe;
 internal Button MoreButton{get;}

 static Brush Paint(string hex)=>(Brush)new BrushConverter().ConvertFromString(hex)!;

 public ModRowControl()
 {
  Background=SpireGlassTheme.GlassPanel("#BE1A2D48");
  BorderBrush=SpireGlassTheme.PanelStroke();
  BorderThickness=new Thickness(1);
  CornerRadius=new CornerRadius(11);
  SnapsToDevicePixels=false;
  Margin=new Thickness(0,2,6,3);
  Padding=new Thickness(0);
  MinHeight=60;
  HorizontalAlignment=HorizontalAlignment.Stretch;

  var grid=new Grid();
  grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(4)});
  grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
  grid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
  grid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
  stripe=new Border{Background=Paint("#739BCB"),CornerRadius=new CornerRadius(9,0,0,9)};
  grid.Children.Add(stripe);
  var info=new StackPanel{Margin=new Thickness(14,6,7,6),VerticalAlignment=VerticalAlignment.Center};
  title.Foreground=Paint("#F0F5FC");
  info.Children.Add(title);
  subtitle.Margin=new Thickness(0,1,0,0);info.Children.Add(subtitle);
  health.Margin=new Thickness(0,3,0,0);info.Children.Add(health);
  context.Margin=new Thickness(0,3,0,0);info.Children.Add(context);
  Grid.SetColumn(info,1);grid.Children.Add(info);

  statusPill=new Border{CornerRadius=new CornerRadius(6),Padding=new Thickness(7,4,7,4),
   VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(5,0,8,0),Child=status};
  Grid.SetColumn(statusPill,2);grid.Children.Add(statusPill);

  var actions=new StackPanel{Orientation=Orientation.Horizontal,
   Margin=new Thickness(0,0,9,0),VerticalAlignment=VerticalAlignment.Center};
  var details=MakeButton("详情",()=>RaiseEvent(new RoutedEventArgs(DetailsRequestedEvent,this)));
  MoreButton=MakeButton("更多",()=>RaiseEvent(new RoutedEventArgs(MoreRequestedEvent,this)));
  actions.Children.Add(details);actions.Children.Add(MoreButton);
  Grid.SetColumn(actions,3);grid.Children.Add(actions);
  Child=grid;

  DataContextChanged+=(_,_)=>Refresh();
  MouseEnter+=(_,_)=>BorderBrush=Paint("#608EB3D6");
  MouseLeave+=(_,_)=>BorderBrush=SpireGlassTheme.PanelStroke();
 }

 static Button MakeButton(string name,Action action)
 {
  var b=new Button{Content=name,MinWidth=54,Padding=new Thickness(8,6,8,6),
   Margin=new Thickness(0,0,6,0),VerticalAlignment=VerticalAlignment.Center};
  if(Application.Current?.TryFindResource("Ghost") is Style style)b.Style=style;
  b.Click+=(_,_)=>action();
  return b;
 }

 void Refresh()
 {
  if(DataContext is not ModListRow item)return;
  title.Text=item.Name;title.ToolTip=item.Name;
  subtitle.Text=item.Details;subtitle.ToolTip=item.Details;
  status.Text=item.State;
  status.Foreground=item.Mod.Enabled?Paint("#A6E4C6"):Paint("#B5C2D2");
  statusPill.Background=item.Mod.Enabled?Paint("#204A43"):Paint("#34435A");
  stripe.Background=item.IsFix?Paint("#5E9CBF"):item.Mod.Enabled?Paint("#599F97"):Paint("#62748C");
  health.Text=item.HealthText;
  health.Visibility=item.IsFix?Visibility.Visible:Visibility.Collapsed;
  health.ToolTip=item.HealthText;
  context.Text=string.Join("  ·  ",new[]{item.WarningText,item.GitHubText,item.DependencyText}.Where(t=>!string.IsNullOrWhiteSpace(t)));
  context.ToolTip=string.Join("\n",new[]{item.BlockReason,item.GitHubText,item.DependencyText}.Where(t=>!string.IsNullOrWhiteSpace(t)));
  context.Foreground=(item.BlockReason!=null&&!item.IsDependencyProtection)||item.DependencyIssue?Paint("#F0C47C"):Paint("#A8BCD4");
  context.Visibility=context.Text.Length==0?Visibility.Collapsed:Visibility.Visible;
 }
}
