using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace SpirePatch;

// Data-only rows and recycled WPF item templates: typing no longer recreates every card.
internal sealed record ModListRow(ManagedMod Mod,string Health,string? BlockReason)
{
 internal bool IsFix=>ModPresentation.IsIndependentPatch(Mod);
 public string Name=>Mod.Name;
 public string Details=>Mod.Id+" · "+Mod.Version+" · "+Mod.Author+" · "+(Mod.Enabled?"已启用":"已停用");
 public string HealthText=>IsFix?"补丁健康："+Health:string.Empty;
 public string WarningText=>BlockReason==null?string.Empty:"启停受限："+BlockReason;
}

internal static class ModListView
{
 internal static ModListRow[] Rows(string root,ModInventory inventory,Func<string,string> health)=>
  inventory.Mods.Select(mod=>new ModListRow(mod,
    ModPresentation.IsIndependentPatch(mod)?health(mod.Id):"",
    ModManager.BlockReason(root,mod,inventory)))
   .OrderBy(row=>row.Name,StringComparer.OrdinalIgnoreCase).ToArray();

 internal static ModListRow[] Filter(IReadOnlyList<ModListRow> rows,string section,string state,string query)=>
  rows.Where(row=>row.IsFix==(section=="修复补丁") &&
   (state=="全部" || (state=="启用")==row.Mod.Enabled) &&
   (row.Mod.Name+" "+row.Mod.Id+" "+row.Mod.Author).Contains(query,StringComparison.OrdinalIgnoreCase))
  .ToArray();

 static FrameworkElementFactory Label(string property,double size,Brush color,bool strong=false)
 {
  var label=new FrameworkElementFactory(typeof(TextBlock));
  label.SetBinding(TextBlock.TextProperty,new Binding(property));
  label.SetValue(TextBlock.FontSizeProperty,size);
  label.SetValue(TextBlock.ForegroundProperty,color);
  label.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);
  if(strong)label.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);
  return label;
 }

 internal static ListBox Create(Action<ModListRow> details,Action<ModListRow,Button> more)
 {
  var list=new ListBox{Height=400,Background=Brushes.Transparent,BorderThickness=new Thickness(0),
   HorizontalContentAlignment=HorizontalAlignment.Stretch,SelectionMode=SelectionMode.Single};
  VirtualizingPanel.SetIsVirtualizing(list,true);
  VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);
  ScrollViewer.SetCanContentScroll(list,true);
  ScrollViewer.SetVerticalScrollBarVisibility(list,ScrollBarVisibility.Auto);
  var itemStyle=new Style(typeof(ListBoxItem));
  itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch));
  itemStyle.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0)));
  itemStyle.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.Transparent));
  itemStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(0)));
  list.ItemContainerStyle=itemStyle;
  var panel=new FrameworkElementFactory(typeof(VirtualizingStackPanel));
  list.ItemsPanel=new ItemsPanelTemplate(panel);

  var shell=new FrameworkElementFactory(typeof(Border));
  shell.SetValue(Border.BackgroundProperty,Brushes.White);
  shell.SetValue(Border.BorderBrushProperty,new SolidColorBrush(Color.FromRgb(221,227,216)));
  shell.SetValue(Border.BorderThicknessProperty,new Thickness(1));
  shell.SetValue(Border.CornerRadiusProperty,new CornerRadius(10));
  shell.SetValue(Border.PaddingProperty,new Thickness(13,10,13,10));
  shell.SetValue(FrameworkElement.MarginProperty,new Thickness(0,3,6,3));

  var dock=new FrameworkElementFactory(typeof(DockPanel));
  dock.SetValue(DockPanel.LastChildFillProperty,true);
  shell.AppendChild(dock);
  var actions=new FrameworkElementFactory(typeof(StackPanel));
  actions.SetValue(StackPanel.OrientationProperty,Orientation.Horizontal);
  actions.SetValue(DockPanel.DockProperty,Dock.Right);
  actions.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
  actions.SetValue(FrameworkElement.MarginProperty,new Thickness(8,0,0,0));
  RoutedEventHandler onRowAction=(sender,e)=>{
   if(sender is not Button button||button.DataContext is not ModListRow row)return;
   e.Handled=true;
   if((string?)button.Tag=="detail")details(row);
   else if((string?)button.Tag=="more")more(row,button);
  };
  foreach(var (label,tag) in new[]{("详情","detail"),("更多","more")})
  {
   var button=new FrameworkElementFactory(typeof(Button));
   button.SetValue(ContentControl.ContentProperty,label);
   button.SetValue(FrameworkElement.TagProperty,tag);
   button.SetValue(Control.PaddingProperty,new Thickness(10,7,10,7));
   button.SetValue(FrameworkElement.MarginProperty,new Thickness(0,0,6,0));
   button.AddHandler(Button.ClickEvent,onRowAction);
   actions.AppendChild(button);
  }
  dock.AppendChild(actions);
  var info=new FrameworkElementFactory(typeof(StackPanel));
  info.AppendChild(Label(nameof(ModListRow.Name),15,new SolidColorBrush(Color.FromRgb(40,53,45)),true));
  info.AppendChild(Label(nameof(ModListRow.Details),11,new SolidColorBrush(Color.FromRgb(97,112,100))));
  info.AppendChild(Label(nameof(ModListRow.HealthText),11,new SolidColorBrush(Color.FromRgb(57,115,79))));
  info.AppendChild(Label(nameof(ModListRow.WarningText),11,new SolidColorBrush(Color.FromRgb(135,93,37))));
  dock.AppendChild(info);
  list.ItemTemplate=new DataTemplate(typeof(ModListRow)){VisualTree=shell};
  list.SelectionChanged+=(_,_)=>{if(list.SelectedItem!=null)list.SelectedItem=null;};
  return list;
 }
}
