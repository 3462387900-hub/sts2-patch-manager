using System.IO;
using System.Text.Json;

namespace SpirePatch;
public partial class MainWindow
{
 string? coopComparison;
 async Task<JsonElement> FreshReportAsync()
 {
  coopComparison=null;
  if(!demo){
   if(selectedInstallRoot==null)throw new InvalidOperationException("请先选择游戏目录。");
   string root=selectedInstallRoot;var catalog=sourceCurrent?onlineCatalog:null;
   var definitions=catalog?.Patches??Catalog.Bundled;
   // Invalidate the old snapshot before IO: failures must never reuse a previous success.
   report=null;states=[];ClearSelection();installPreview=null;lastInstallResult=null;modInventory=null;
   var fresh=await Task.Run(()=>Catalog.Scan(root,definitions,catalog?.Commit));
   report=fresh;states=fresh.Patches;scannedCatalog=definitions;
  }
  return CoopReports.Create(report,states,demo);
 }
 internal async Task ExportFreshReportAsync(string path)=>CoopReports.Write(path,await FreshReportAsync());
 internal async Task CompareFreshReportAsync(string path)
 {
  coopComparison=null;
  var peer=await Task.Run(()=>CoopReports.Read(path));
  var local=await FreshReportAsync();
  coopComparison=CoopReports.Compare(local,peer);
 }
 async Task RunReportActionAsync(Func<Task> action)
 {
  if(busy||transactionWriting)return;
  busy=true;content.IsEnabled=false;coopComparison=null;UpdateGlobalControls();
  toast.Text=demo?"正在处理演示清单…":"正在重新扫描本机文件，请稍候…";
  try{await action();}
  catch(Exception ex){coopComparison="本次操作未完成，不能确认一致。\n"+ex.Message;toast.Text="清单操作未完成："+ex.Message;}
  finally{busy=false;content.IsEnabled=true;UpdateGlobalControls();if(page=="联机检查")ShowCoop();}
 }
}
