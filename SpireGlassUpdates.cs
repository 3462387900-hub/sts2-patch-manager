using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SpirePatch;

// Spire Glass update workspace: reuses the validated source discovery and installer operations.
// It never bypasses the existing review, SHA-256, backup, or safe-recovery flow.
public partial class MainWindow
{
 void ShowModUpdatesCenter()
 {
  var root=V(Header("MOD UPDATES","更新中心",
   "检查 GitHub 来源与版本，更新前始终确认安装包及安全校验。"));
  Space(root,15);
  if(selectedInstallRoot==null)
  {
   root.Children.Add(Card(V(T("请先定位游戏目录",17,ink,true),
    Btn("选择游戏目录",ChooseFolder,"Primary"))));
   SetPage("更新中心",root,restore:ShowModUpdatesCenter);return;
  }
  var bar=new WrapPanel{Margin=new Thickness(0,0,0,13)};
  void AddButton(string label,Action action,string style="Ghost")
  {
   var button=Btn(label,action,style);
   button.IsEnabled=!busy&&!transactionWriting;
   button.Margin=new Thickness(0,0,9,8);
   bar.Children.Add(button);
  }
  AddButton("检查全部 GitHub 更新",()=>{
   if(modInventory==null||modInventoryDirty)
   {
    toast.Text="MOD 清单未刷新，请先刷新列表。";
    RefreshMods();return;
   }
   CheckGitHubMods();
  },"Primary");
  AddButton("查找未关联的来源",DiscoverGitHubMods);
  AddButton("刷新本地 MOD",RefreshMods);
  root.Children.Add(bar);
  if(modInventoryDirty)
  {
   root.Children.Add(Card(V(T("本地 MOD 文件已变化",15,gold,true),
    T("请先刷新再检查 GitHub 版本，避免使用过期清单。",12,muted)),"#27364A",13));
   Space(root,12);
  }
  if(modInventory==null)
  {
   root.Children.Add(Card(V(T("尚未读取本地 MOD",16,ink,true),
    Btn("开始扫描",RefreshMods,"Primary"))));
   SetPage("更新中心",root,restore:ShowModUpdatesCenter);return;
  }
  var updates=GitHubUpdates();
  var normal=modInventory.Mods.Where(m=>!ModPresentation.IsIndependentPatch(m)).ToArray();
  int available=normal.Count(m=>updates.Cached(m.Id)?.Status=="有更新");
  int unbound=normal.Count(m=>updates.Repository(m.Id)==null);
  var stats=new WrapPanel{Margin=new Thickness(0,0,0,14)};
  foreach(var (value,label) in new[]{
   (normal.Length.ToString(),"普通 MOD"),(available.ToString(),"有新版本"),(unbound.ToString(),"待确认来源")})
  {
   var stat=V(T(value,25,ink,true),T(label,12,muted));
   var tile=Card(stat,"#20344E",14);tile.Width=176;tile.Margin=new Thickness(0,0,11,8);
   stats.Children.Add(tile);
  }
  root.Children.Add(stats);
  var search=new TextBox{MinWidth=160,Margin=new Thickness(0,0,0,10)};
  search.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,"搜索 MOD 更新");
  root.Children.Add(search);
  var list=new StackPanel();
  var count=T("",12,muted);root.Children.Add(count);Space(root,8);
  root.Children.Add(list);
  void Render()
  {
   list.Children.Clear();
   var matches=normal.Where(m=>(m.Name+" "+m.Id+" "+m.Author)
    .Contains(search.Text,StringComparison.OrdinalIgnoreCase)).ToArray();
   count.Text="显示 "+matches.Length+" / "+normal.Length+" 项 · "+modStatus;
   foreach(var mod in matches)
   {
    var info=updates.Cached(mod.Id);
    var repo=updates.Repository(mod.Id);
    string state=repo==null?"来源未关联":info==null?"尚未检查":
     info.Status+(info.LatestVersion.Length>0?" · "+info.LatestVersion:"");
    var item=V(T(mod.Name,15,ink,true),
      T("本地 "+mod.Version+" · "+mod.Author+" · "+state,12,muted));
    var actions=new WrapPanel{Margin=new Thickness(0,8,0,0)};
    var detail=Btn("查看详情",()=>ShowModDetail(mod.Folder),"Ghost");
    var bind=Btn(repo==null?"关联来源":"修改来源",()=>BindGitHubMod(mod),"Ghost");
    var check=Btn("检查更新",()=>CheckGitHubMod(mod),"Ghost");
    check.IsEnabled=repo!=null&&!busy;
    var install=Btn("下载并更新",()=>UpdateGitHubMod(mod),"Primary");
    install.IsEnabled=info?.Status=="有更新"&&mod.Enabled&&!busy&&!modInventoryDirty;
    foreach(var b in new[]{detail,bind,check,install})
    {b.Margin=new Thickness(0,0,8,5);actions.Children.Add(b);}
    item.Children.Add(actions);
    var container=Card(item,"#182942",14);
    container.Margin=new Thickness(0,0,0,9);list.Children.Add(container);
   }
  }
  search.TextChanged+=(_,_)=>Render();
  Render();
  SetPage("更新中心",root,restore:ShowModUpdatesCenter);
 }
}
