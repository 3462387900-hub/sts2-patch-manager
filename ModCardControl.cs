using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SpirePatch;

// v0.14: the whole compact card opens context; checkbox and overflow remain independent controls.
// Existing callbacks are retained for virtualized groups and safe operation previews.
public sealed class ModCardControl : Border
{
 public static readonly RoutedEvent DetailsEvent=EventManager.RegisterRoutedEvent("CardDetails",RoutingStrategy.Bubble,typeof(RoutedEventHandler),typeof(ModCardControl));
 public static readonly RoutedEvent MoreEvent=EventManager.RegisterRoutedEvent("CardMore",RoutingStrategy.Bubble,typeof(RoutedEventHandler),typeof(ModCardControl));
 public static readonly RoutedEvent SelectEvent=EventManager.RegisterRoutedEvent("CardSelect",RoutingStrategy.Bubble,typeof(RoutedEventHandler),typeof(ModCardControl));
 public static readonly RoutedEvent ToggleEvent=EventManager.RegisterRoutedEvent("CardToggle",RoutingStrategy.Bubble,typeof(RoutedEventHandler),typeof(ModCardControl));
 public static readonly RoutedEvent UpdateEvent=EventManager.RegisterRoutedEvent("CardUpdate",RoutingStrategy.Bubble,typeof(RoutedEventHandler),typeof(ModCardControl));

 public CheckBox SelectBox{get;}=new(){Margin=new Thickness(0,0,5,0),VerticalAlignment=VerticalAlignment.Center,
  ToolTip="勾选此 MOD，用于批量操作"};
 public Button MoreButton{get;}
 readonly TextBlock title=new(){FontSize=15,FontWeight=FontWeights.SemiBold,
  TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis};
 readonly TextBlock version=new(){FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis,
  TextWrapping=TextWrapping.NoWrap};
 readonly TextBlock status=new(){FontSize=12,FontWeight=FontWeights.SemiBold};
 readonly TextBlock highlight=new(){FontSize=11,TextTrimming=TextTrimming.CharacterEllipsis,
  TextWrapping=TextWrapping.NoWrap,HorizontalAlignment=HorizontalAlignment.Right};
 readonly Ellipse stateDot=new(){Width=7,Height=7,Margin=new Thickness(0,0,6,0)};
 internal Action<ModListRow,bool>? SelectionChanged;
 internal Action<ModListRow>? DetailsRequested,ToggleRequested,UpdateRequested;
 internal Action<ModListRow,Button>? MoreRequested;
 bool setting;

 static Brush Paint(string color)=>(Brush)new BrushConverter().ConvertFromString(color)!;
 static readonly Brush Panel=Paint("#A6192B43");
 static readonly Brush PanelHover=Paint("#BA20364F");
 static readonly Brush PanelSelected=Paint("#B5304865");
 static readonly Brush Stroke=Paint("#37587898");
 static readonly Brush HoverStroke=Paint("#6389B3D7");
 static readonly Brush SelectedStroke=Paint("#8898C6F2");
 static readonly Brush Secondary=Paint("#AFC3D9");
 static readonly Brush Good=Paint("#8ED8BA");
 static readonly Brush Neutral=Paint("#AABACD");
 static readonly Brush Warning=Paint("#ECC487");

 public ModCardControl()
 {
  Background=Panel;BorderBrush=Stroke;BorderThickness=new Thickness(1);
  CornerRadius=new CornerRadius(11);Padding=new Thickness(12,9,10,9);
  Margin=new Thickness(0,2,9,6);MinHeight=106;
  Focusable=true;Cursor=Cursors.Hand;
  SnapsToDevicePixels=false;UseLayoutRounding=false;
  if(Application.Current?.TryFindResource("ModCardCheckBox") is Style checkStyle)SelectBox.Style=checkStyle;
  SelectBox.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,"选择模组");

  var main=new Grid();
  main.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
  main.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
  main.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});

  var top=new Grid();
  top.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
  top.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
  top.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
  top.Children.Add(SelectBox);
  title.VerticalAlignment=VerticalAlignment.Center;
  title.Foreground=Paint("#F1F6FD");Grid.SetColumn(title,1);top.Children.Add(title);

  MoreButton=new Button{Content="⋯",FontSize=19,MinWidth=32,MinHeight=30,
   ToolTip="更多模组操作",Margin=new Thickness(7,0,0,0),
   VerticalAlignment=VerticalAlignment.Center};
  if(Application.Current?.TryFindResource("ModToolbarAction") is Style actionStyle)
   MoreButton.Style=actionStyle;
  MoreButton.Click+=(_,_)=>{if(DataContext is ModListRow row)MoreRequested?.Invoke(row,MoreButton);};
  Grid.SetColumn(MoreButton,2);top.Children.Add(MoreButton);
  main.Children.Add(top);

  version.Foreground=Secondary;
  version.Margin=new Thickness(30,2,0,0);
  Grid.SetRow(version,1);main.Children.Add(version);

  var bottom=new Grid{Margin=new Thickness(30,7,0,0)};
  bottom.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
  bottom.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
  var statusLine=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
  statusLine.Children.Add(stateDot);statusLine.Children.Add(status);
  bottom.Children.Add(statusLine);
  highlight.Foreground=Warning;
  highlight.VerticalAlignment=VerticalAlignment.Center;
  highlight.Margin=new Thickness(12,0,0,0);
  Grid.SetColumn(highlight,1);bottom.Children.Add(highlight);
  Grid.SetRow(bottom,2);main.Children.Add(bottom);
  Child=main;

  SelectBox.Checked+=(_,_)=>Select(true);
  SelectBox.Unchecked+=(_,_)=>Select(false);
  DataContextChanged+=(_,_)=>Refresh();
  MouseEnter+=(_,_)=>UpdateSelectionVisual();
  MouseLeave+=(_,_)=>UpdateSelectionVisual();
  MouseLeftButtonUp+=(_,e)=>{
   if(e.ChangedButton!=MouseButton.Left||IsFromInteractiveControl(e.OriginalSource as DependencyObject))return;
   RequestDetails();e.Handled=true;
  };
  KeyDown+=(_,e)=>{
   if(e.Key==Key.Enter&&e.OriginalSource==this){RequestDetails();e.Handled=true;}
  };
 }
 bool IsFromInteractiveControl(DependencyObject? origin)
 {
  while(origin!=null&&origin!=this)
  {
   if(origin is ButtonBase)return true;
   origin=VisualTreeHelper.GetParent(origin);
  }
  return false;
 }
 void RequestDetails()
 {
  if(DataContext is ModListRow row)DetailsRequested?.Invoke(row);
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
  Background=row.Selected?PanelSelected:IsMouseOver?PanelHover:Panel;
  BorderBrush=row.Selected?SelectedStroke:IsMouseOver?HoverStroke:Stroke;
 }
 public void Refresh()
 {
  if(DataContext is not ModListRow row)return;
  title.Text=row.Name;
  title.ToolTip="模组："+row.Name+"\nID："+row.Mod.Id+"\n作者："+row.Mod.Author;
  version.Text="版本号："+row.Mod.Version;
  version.ToolTip="版本号："+row.Mod.Version+"\n作者："+row.Mod.Author+"\n模组 ID："+row.Mod.Id;
  status.Text=row.State;
  status.Foreground=row.Mod.Enabled?Good:Neutral;
  stateDot.Fill=row.Mod.Enabled?Good:Neutral;

  string text=row.BlockReason!=null?ModListView.BriefBlockReason(row.BlockReason):
   row.DependencyIssue?"依赖异常":
   row.IsFix?row.Health:
   row.GitHubText.Contains("有更新",StringComparison.Ordinal)?"有更新":"";
  highlight.Text=text;
  highlight.ToolTip=string.Join("\n",new[]{row.BlockReason,row.GitHubText,row.DependencyText,row.Health}
   .Where(s=>!string.IsNullOrWhiteSpace(s)));
  highlight.Visibility=string.IsNullOrWhiteSpace(text)?Visibility.Collapsed:Visibility.Visible;
  highlight.Foreground=row.BlockReason!=null||row.DependencyIssue?Warning:Good;
  setting=true;SelectBox.IsChecked=row.Selected;setting=false;
  UpdateSelectionVisual();
 }
}
