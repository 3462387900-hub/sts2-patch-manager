using System.Windows;
using System.Windows.Controls;

namespace SpirePatch;

public partial class MainWindow
{
 // One persistent secondary navigation pattern across the MOD workspace.
 UIElement ModWorkspaceTabs(string active)
 {
  var tabs=new WrapPanel{Margin=new Thickness(0,0,0,11)};
  foreach(var (name,open) in new (string,Action)[]
  {
   ("已安装",ShowMods),
   ("创意工坊下载",ShowWorkshopPage),
   ("依赖关系",()=>ShowDependencyGraph()),
   ("环境快照",ShowModEnvironmentSnapshots)
  })
  {
   var tab=Btn(name,open,name==active?"ModWorkspaceTabActive":"ModWorkspaceTab");
   tab.Margin=new Thickness(0,0,13,0);
   tab.IsEnabled=!busy&&!transactionWriting;
   tabs.Children.Add(tab);
  }
  return tabs;
 }

 void ShowWorkshopPage()
 {
  if(selectedInstallRoot==null){ShowMods();return;}
  var root=V(Header("MOD DOWNLOAD","创意工坊下载",
   "通过已授权的尖塔补给站接口下载《杀戮尖塔 2》创意工坊 MOD，安装前先预览并确认。"));
  Space(root,10);
  root.Children.Add(ModWorkspaceTabs("创意工坊下载"));
  Space(root,8);

  var input=new TextBox{MinHeight=38,ToolTip="Steam 创意工坊 ID、Steam 分享链接或尖塔补给站链接"};
  input.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,"创意工坊链接或 ID");
  var note=T("支持 Steam 创意工坊数字 ID、Steam 分享链接或尖塔补给站 MOD 链接。",12,muted);
  var get=Btn("获取并预览",()=>{
   if(!WorkshopModInstaller.TryParseWorkshopId(input.Text,out ulong workshopId))
   {
    note.Text="链接或 ID 无效，请确认属于《杀戮尖塔 2》（AppID 2868840）。";
    note.Foreground=gold;
    return;
   }
   DownloadWorkshopMod(presetWorkshopId:workshopId);
  },"Primary");
  get.IsEnabled=!busy&&!transactionWriting&&!modInventoryDirty;
  var field=V(T("粘贴创意工坊链接或 Workshop ID",16,ink,true),input,note);
  Space(field,9);field.Children.Add(get);
  root.Children.Add(Card(field,padding:15));Space(root,13);

  var help=V(T("下载与安装流程",15,ink,true),
   T("获取创意工坊内容 → 校验文件 → 预览新增或更新范围 → 由你确认后安装。",12,muted),
   T("更新前会保留旧版本，并自动创建环境保护快照。",12,muted),
   T("Manifest 签名和游戏分支匹配验证尚未完成；请仅安装你信任的 MOD。",12,gold));
  root.Children.Add(Card(help,padding:14));Space(root,9);
  if(modInventoryDirty)
   root.Children.Add(Card(V(T("本地模组环境发生变化",14,gold,true),
    T("请先完成模组刷新，再继续下载或更新。",12,muted),
    Btn("刷新模组",RefreshMods,"Ghost")),padding:12));
  SetPage("模组管理",root,"mod-workshop",ShowWorkshopPage);
 }
}
