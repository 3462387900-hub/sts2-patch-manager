using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SpirePatch;
public partial class MainWindow
{
 record PageVisit(string Key,Action Render,double Offset);
 readonly List<PageVisit> backHistory=[];
 PageVisit? currentVisit;
 bool returning;
 Button? backButton,launchButton;
 ScrollViewer? pageScroll;
 void TrackPage(string key,Action render)
 {
  bool changed=currentVisit?.Key!=key;
  if(changed&&!returning&&currentVisit!=null){backHistory.Add(currentVisit with{Offset=pageScroll?.VerticalOffset??0});if(backHistory.Count>60)backHistory.RemoveAt(0);}
  currentVisit=new(key,render,0);
  if(changed&&!returning)pageScroll?.ScrollToTop();UpdateGlobalControls();
 }
 void UpdateGlobalControls()
 {
  if(backButton!=null)backButton.IsEnabled=!busy&&!transactionWriting&&backHistory.Count>0;
  if(launchButton!=null)launchButton.IsEnabled=!busy&&!transactionWriting&&selectedInstallRoot!=null;
 }
 void ClearNavigation(){backHistory.Clear();currentVisit=null;UpdateGlobalControls();}
 void RememberPage(string key,Action render){if(currentVisit?.Key==key)currentVisit=currentVisit with{Render=render};}
 void GoBack()
 {
  if(busy||transactionWriting||backHistory.Count==0)return;
  var visit=backHistory[^1];backHistory.RemoveAt(backHistory.Count-1);returning=true;
  try{visit.Render();Dispatcher.BeginInvoke(()=>{if(currentVisit?.Key==visit.Key)pageScroll?.ScrollToVerticalOffset(visit.Offset);},DispatcherPriority.Loaded);}
  catch(Exception e){toast.Text="页面状态已变化："+e.Message;ShowOverview();}
  finally{returning=false;UpdateGlobalControls();}
 }
}
