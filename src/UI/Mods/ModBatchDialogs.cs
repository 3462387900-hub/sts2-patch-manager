using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SpirePatch;

internal sealed record ModBatchReview(string Name,string State,string Explanation);

internal static class ModBatchDialogs
{
 static readonly Brush Light=new SolidColorBrush(Color.FromRgb(238,245,253));
 static readonly Brush Secondary=new SolidColorBrush(Color.FromRgb(184,204,225));
 internal static TextBlock Text(string value,int size=13,bool strong=false)=>new(){
  Text=value,FontSize=size,Foreground=strong?Light:Secondary,FontWeight=strong?FontWeights.SemiBold:FontWeights.Normal,
  TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,7)
 };
 internal static Button Action(string value,bool primary=false)
 {
  var button=new Button{Content=value,Padding=new Thickness(14,9,14,9),Margin=new Thickness(0,0,8,0),MinWidth=78};
  if(Application.Current.TryFindResource(primary?"Primary":"Ghost") is Style style)button.Style=style;
  return button;
 }
 internal static bool Confirm(Window owner,string title,string description,IReadOnlyList<ModBatchReview> entries)
 {
  var window=new Window{Title=title,Owner=owner,Width=670,Height=530,MinWidth=510,MinHeight=400,
   WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false,
   Background=new SolidColorBrush(Color.FromRgb(24,39,62)),Foreground=Light,ResizeMode=ResizeMode.CanResize};
  var root=new DockPanel{Margin=new Thickness(19)};
  var footer=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
  var confirm=Action("确认执行",true);var cancel=Action("取消");
  confirm.Click+=(_,_)=>window.DialogResult=true;
  cancel.Click+=(_,_)=>window.DialogResult=false;
  footer.Children.Add(confirm);footer.Children.Add(cancel);
  DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
  var heading=new StackPanel();
  heading.Children.Add(Text(title,19,true));heading.Children.Add(Text(description,13));
  var ready=entries.Count(e=>e.State=="可处理");
  var pending=entries.Count(e=>e.State=="需复核");
  heading.Children.Add(Text($"共 {entries.Count} 项 · 可直接处理 {ready} · 依赖待复核 {pending} · 跳过 {entries.Count-ready-pending}",13,true));
  DockPanel.SetDock(heading,Dock.Top);root.Children.Add(heading);
  var items=new StackPanel();
  foreach(var entry in entries){
   var card=new StackPanel{Margin=new Thickness(0,0,0,8)};
   card.Children.Add(Text(entry.Name+"   ·   "+entry.State,14,true));
   card.Children.Add(Text(entry.Explanation,12));
   items.Children.Add(new Border{Child=card,Padding=new Thickness(10),
    Background=new SolidColorBrush(Color.FromRgb(35,57,81)),CornerRadius=new CornerRadius(7)});
  }
  root.Children.Add(new ScrollViewer{Content=items,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
  window.Content=root;
  confirm.IsEnabled=ready+pending>0;
  return window.ShowDialog()==true;
 }
}

internal sealed class ModBatchProgressWindow : Window
{
 readonly TextBlock heading=ModBatchDialogs.Text("",15,true);
 readonly TextBlock detail=ModBatchDialogs.Text("",12);
 readonly ProgressBar progress=new(){Minimum=0,Height=14,Margin=new Thickness(0,8,0,12)};
 readonly StackPanel entries=new();
 readonly Button stop=ModBatchDialogs.Action("停止剩余任务");
 readonly Button close=ModBatchDialogs.Action("关闭");
 bool finished;
 public bool CancelRequested{get;private set;}

 public ModBatchProgressWindow(Window owner,string title,int count)
 {
  Title=title;Owner=owner;Width=640;Height=510;MinWidth=490;MinHeight=380;
  WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;
  Background=new SolidColorBrush(Color.FromRgb(24,39,62));ResizeMode=ResizeMode.CanResize;
  progress.Maximum=Math.Max(1,count);heading.Text=title;
  var root=new DockPanel{Margin=new Thickness(18)};
  var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
  stop.Click+=(_,_)=>{CancelRequested=true;stop.IsEnabled=false;detail.Text="将在当前模组处理结束后停止其余任务。";};
  close.IsEnabled=false;close.Click+=(_,_)=>Close();
  buttons.Children.Add(stop);buttons.Children.Add(close);
  DockPanel.SetDock(buttons,Dock.Bottom);root.Children.Add(buttons);
  var top=new StackPanel();top.Children.Add(heading);top.Children.Add(detail);top.Children.Add(progress);
  DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
  root.Children.Add(new ScrollViewer{Content=entries,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
  Content=root;
  Closing+=(_,e)=>{if(!finished){CancelRequested=true;e.Cancel=true;stop.IsEnabled=false;}};
 }
 public void Current(string name,int index,int count)
 {
  detail.Text=$"正在处理 {index} / {count}：{name}";
 }
 public void Record(string name,string state,string explanation,int completed)
 {
  entries.Children.Add(new Border{Padding=new Thickness(8),Margin=new Thickness(0,0,0,5),
   Background=new SolidColorBrush(Color.FromRgb(35,57,81)),
   Child=ModBatchDialogs.Text(name+" · "+state+"："+explanation,13)});
  progress.Value=completed;
 }
 public void Finish(string summary)
 {
  finished=true;heading.Text=summary;detail.Text="以下为逐项处理结果。";
  stop.IsEnabled=false;close.IsEnabled=true;
 }
}
