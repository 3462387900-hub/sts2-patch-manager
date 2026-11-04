using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SpirePatch;

// Immutable, UI-only data. Safe enable/disable still goes through ModManager.Preview/Apply.
internal sealed record ModListRow(ManagedMod Mod,string Health,string? BlockReason,string GitHubText="")
{
 internal bool IsFix=>ModPresentation.IsIndependentPatch(Mod);
 public string Name=>Mod.Name;
 public string Details=>Mod.Id+"  ·  "+Mod.Version+"  ·  "+Mod.Author;
 public string State=>Mod.Enabled?"已启用":"已停用";
 public string HealthText=>IsFix?"补丁健康："+Health:"";
 public string WarningText=>BlockReason==null?"":ModListView.BriefBlockReason(BlockReason);
}

internal static class ModListView
{
 internal static ModListRow[] Rows(string root,ModInventory inventory,Func<string,string> health,Func<ManagedMod,string>? github=null)=>
  inventory.Mods.Select(mod=>new ModListRow(mod,
   ModPresentation.IsIndependentPatch(mod)?health(mod.Id):"",
   ModManager.BlockReason(root,mod,inventory),github?.Invoke(mod)??""))
  .OrderBy(row=>row.Name,StringComparer.OrdinalIgnoreCase).ToArray();

 internal static ModListRow[] Filter(IReadOnlyList<ModListRow> rows,string section,string state,string query)=>
  rows.Where(row=>row.IsFix==(section=="修复补丁")&&
   (state=="全部"||(state=="启用")==row.Mod.Enabled)&&
   (row.Mod.Name+" "+row.Mod.Id+" "+row.Mod.Author).Contains(query,StringComparison.OrdinalIgnoreCase))
  .ToArray();

 internal static string BriefBlockReason(string reason)
 {
  const string deps="这些启用项依赖它，请先停用依赖项：";
  if(reason.StartsWith(deps,StringComparison.Ordinal))
  {
   int total=reason[deps.Length..].Split('、',StringSplitOptions.RemoveEmptyEntries).Length;
   return $"依赖保护 · {total} 项已启用 MOD 需要它（详情查看完整名单）";
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

 readonly TextBlock title=new(){FontSize=15,FontWeight=FontWeights.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis};
 readonly TextBlock subtitle=new(){FontSize=11,Foreground=Paint("#6B796F"),TextTrimming=TextTrimming.CharacterEllipsis};
 readonly TextBlock health=new(){FontSize=11,Foreground=Paint("#44795B"),TextTrimming=TextTrimming.CharacterEllipsis};
 readonly TextBlock github=new(){FontSize=11,Foreground=Paint("#53705F"),TextTrimming=TextTrimming.CharacterEllipsis};
 readonly TextBlock warning=new(){FontSize=11,Foreground=Paint("#93642F"),TextTrimming=TextTrimming.CharacterEllipsis};
 readonly TextBlock status=new(){FontSize=11,FontWeight=FontWeights.SemiBold};
 readonly Border statusPill;
 readonly Border stripe;
 internal Button MoreButton{get;}

 static Brush Paint(string hex)=>(Brush)new BrushConverter().ConvertFromString(hex)!;

 public ModRowControl()
 {
  Background=Brushes.White;
  BorderBrush=Paint("#DCE4D9");
  BorderThickness=new Thickness(1);
  CornerRadius=new CornerRadius(10);
  Margin=new Thickness(0,3,6,4);
  Padding=new Thickness(0);
  MinHeight=82;
  HorizontalAlignment=HorizontalAlignment.Stretch;

  var grid=new Grid();
  grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(4)});
  grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
  grid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
  grid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
  stripe=new Border{Background=Paint("#7C9E86"),CornerRadius=new CornerRadius(9,0,0,9)};
  grid.Children.Add(stripe);
  var info=new StackPanel{Margin=new Thickness(14,11,8,11),VerticalAlignment=VerticalAlignment.Center};
  title.Foreground=Paint("#29392E");
  info.Children.Add(title);
  subtitle.Margin=new Thickness(0,2,0,0);info.Children.Add(subtitle);
  health.Margin=new Thickness(0,4,0,0);info.Children.Add(health);
  warning.Margin=new Thickness(0,4,0,0);info.Children.Add(warning);
  Grid.SetColumn(info,1);grid.Children.Add(info);

  statusPill=new Border{CornerRadius=new CornerRadius(6),Padding=new Thickness(8,5,8,5),
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
  MouseEnter+=(_,_)=>BorderBrush=Paint("#A3B9A7");
  MouseLeave+=(_,_)=>BorderBrush=Paint("#DCE4D9");
 }

 static Button MakeButton(string name,Action action)
 {
  var b=new Button{Content=name,MinWidth=56,Padding=new Thickness(9,7,9,7),
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
  status.Foreground=item.Mod.Enabled?Paint("#37694A"):Paint("#6F776F");
  statusPill.Background=item.Mod.Enabled?Paint("#E7F2E8"):Paint("#EEF0ED");
  stripe.Background=item.IsFix?Paint("#5D96A3"):item.Mod.Enabled?Paint("#78A48A"):Paint("#A9B0A7");
  health.Text=item.HealthText;
  health.Visibility=item.IsFix?Visibility.Visible:Visibility.Collapsed;
  health.ToolTip=item.HealthText;
  github.Text=item.GitHubText;
  github.ToolTip=item.GitHubText;
  github.Visibility=string.IsNullOrEmpty(item.GitHubText)?Visibility.Collapsed:Visibility.Visible;
  warning.Text=item.WarningText;
  warning.ToolTip=item.BlockReason;
  warning.Visibility=item.BlockReason==null?Visibility.Collapsed:Visibility.Visible;
 }
}
