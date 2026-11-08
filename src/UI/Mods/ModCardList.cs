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
  // ListBoxItem selection is not part of MOD management; keep it from recycling a card when a child checkbox changes.
  style.Setters.Add(new Setter(Control.FocusableProperty,false));
  style.Setters.Add(new Setter(System.Windows.Input.KeyboardNavigation.IsTabStopProperty,false));
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
  return list;
 }
 internal static void SetItems(ListBox list,IReadOnlyList<ModListRow> rows,int columns)
 {
  list.ItemsSource=rows.Chunk(columns).Select(x=>new ModCardGroup(x)).ToArray();
 }

}
internal sealed record ModCardActions(Action<ModListRow> Detail,
 Action<ModListRow,Button> More,Action<ModListRow> Toggle,
 Action<ModListRow> Update,Action<ModListRow,bool> Selected);

public sealed class ModCardGroupControl : Grid
{
 ModCardGroup? displayedGroup;
 public ModCardGroupControl()
 {
  // Loaded must not recreate all card controls: it only attaches callbacks.
  DataContextChanged+=(_,_)=>Refresh();
  Loaded+=(_,_)=>BindActions();
 }
 void Refresh()
 {
  if(ReferenceEquals(DataContext,displayedGroup)){BindActions();return;}
  Children.Clear();ColumnDefinitions.Clear();
  displayedGroup=DataContext as ModCardGroup;
  if(displayedGroup==null)return;
  for(int i=0;i<displayedGroup.Cards.Length;i++)
  {
   ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
   ModCardControl card=UiThemes.IsClassic?new ClassicModCardControl():new ModCardControl();
   card.DataContext=displayedGroup.Cards[i];
   SetColumn(card,i);Children.Add(card);
  }
  BindActions();
 }
 internal void BindActions()
 {
  if(FindOwner()?.Tag is not ModCardActions callbacks)return;
  foreach(var card in Children.OfType<ModCardControl>())
  {
   card.DetailsRequested=callbacks.Detail;card.MoreRequested=callbacks.More;
   card.ToggleRequested=callbacks.Toggle;card.UpdateRequested=callbacks.Update;
   card.SelectionChanged=callbacks.Selected;
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
