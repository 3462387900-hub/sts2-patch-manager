using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SpirePatch;

internal sealed record ModCardGroup(ModListRow[] Cards);

// Virtualize by rows of cards: a 160-MOD installation still constructs only visible rows.
internal static class ModCardList
{
 internal static ListBox Create(Action<ModListRow> detail,Action<ModListRow,Button> more,
  Action<ModListRow> toggle,Action<ModListRow> update,Action<ModListRow,bool> selected)
 {
  var list=new ListBox{Background=Brushes.Transparent,BorderThickness=new(0),
   HorizontalContentAlignment=HorizontalAlignment.Stretch,
   HorizontalAlignment=HorizontalAlignment.Stretch,SelectionMode=SelectionMode.Single,MinHeight=120};
  VirtualizingPanel.SetIsVirtualizing(list,true);
  VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);
  ScrollViewer.SetCanContentScroll(list,true);
  ScrollViewer.SetVerticalScrollBarVisibility(list,ScrollBarVisibility.Auto);
  ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);
  var style=new Style(typeof(ListBoxItem));
  style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch));
  style.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0)));
  style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(0)));
  style.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.Transparent));
  list.ItemContainerStyle=style;
  list.ItemsPanel=new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
  var factory=new FrameworkElementFactory(typeof(ModCardGroupControl));
  list.ItemTemplate=new DataTemplate(typeof(ModCardGroup)){VisualTree=factory};
  list.Tag=new ModCardActions(detail,more,toggle,update,selected);
  list.AddHandler(ModCardControl.DetailsEvent,new RoutedEventHandler((_,e)=>{
   if(e.OriginalSource is ModCardControl card&&card.DataContext is ModListRow row)detail(row);
  }));
  list.AddHandler(ModCardControl.MoreEvent,new RoutedEventHandler((_,e)=>{
   if(e.OriginalSource is ModCardControl card&&card.DataContext is ModListRow row)more(row,card.MoreButton);
  }));
  list.SelectionChanged+=(_,_)=>list.SelectedItem=null;
  return list;
 }
 internal static void SetItems(ListBox list,IReadOnlyList<ModListRow> rows,int columns)
 {
  list.ItemsSource=rows.Chunk(columns).Select(x=>new ModCardGroup(x)).ToArray();
 }
 internal static void RefreshVisible(DependencyObject node)
 {
  if(node is ModCardControl card)card.Refresh();
  for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)
   RefreshVisible(VisualTreeHelper.GetChild(node,i));
 }
}
internal sealed record ModCardActions(Action<ModListRow> Detail,
 Action<ModListRow,Button> More,Action<ModListRow> Toggle,
 Action<ModListRow> Update,Action<ModListRow,bool> Selected);

public sealed class ModCardGroupControl : Grid
{
 public ModCardGroupControl(){DataContextChanged+=(_,_)=>Refresh();Loaded+=(_,_)=>Refresh();}
 void Refresh()
 {
  Children.Clear();ColumnDefinitions.Clear();
  if(DataContext is not ModCardGroup group)return;
  for(int i=0;i<group.Cards.Length;i++)
  {
   ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
   var card=new ModCardControl{DataContext=group.Cards[i]};
   var owner=FindOwner();
   if(owner?.Tag is ModCardActions callbacks)
   {
    card.DetailsRequested=callbacks.Detail;card.MoreRequested=callbacks.More;
    card.ToggleRequested=callbacks.Toggle;card.UpdateRequested=callbacks.Update;
    card.SelectionChanged=callbacks.Selected;
   }
   SetColumn(card,i);Children.Add(card);
  }
 }
 ListBox? FindOwner()
 {
  DependencyObject? node=this;
  while(node!=null)
  {
   if(node is ListBox list)return list;
   node=VisualTreeHelper.GetParent(node);
  }
  return null;
 }
}
