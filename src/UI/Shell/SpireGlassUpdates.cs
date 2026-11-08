using System.Windows;
using System.Windows.Controls;

namespace SpirePatch;

// Spire Glass update workspace: GitHub and Workshop sources share one read-only update overview.
public partial class MainWindow
{
 // Read-only MOD update checks are independent of file-transaction `busy`.
 // Checking never holds the global navigation lock.
 bool modUpdateCheckRunning;
 CancellationTokenSource? modUpdateCheckCancellation;
 void CancelModUpdateCheck()=>modUpdateCheckCancellation?.Cancel();

 void ShowModUpdatesCenter()
 {
  var root=V(Header("MOD UPDATES","更新中心",
   "集中查看 GitHub 与 Steam 创意工坊来源。发现更新后仍需下载预览并由你确认安装。"));
  Space(root,15);
  if(selectedInstallRoot==null)
  {
   root.Children.Add(Card(V(T("请先定位游戏目录",17,ink,true),
    Btn("选择游戏目录",ChooseFolder,"Primary"))));
   SetPage("更新中心",root,restore:ShowModUpdatesCenter);return;
  }
  var bar=new WrapPanel{Margin=new Thickness(0,0,0,12)};
  void AddButton(string label,Action action,string style="Ghost",bool enabled=true)
  {
   var button=Btn(label,action,style);
   button.IsEnabled=enabled&&!busy&&!transactionWriting;
   button.Margin=new Thickness(0,0,8,8);bar.Children.Add(button);
  }
  AddButton("检查全部更新",()=>{
   if(modInventory==null||modInventoryDirty){toast.Text="请先刷新本地 MOD 清单。";RefreshMods();return;}
   CheckAllModUpdates();
  },"Primary",!modUpdateCheckRunning);
  if(modUpdateCheckRunning)
   AddButton("取消检查",CancelModUpdateCheck,"Ghost");
  AddButton("查找 GitHub 来源",DiscoverGitHubMods);
  AddButton("刷新本地 MOD",RefreshMods);
  root.Children.Add(bar);
  root.Children.Add(T(modUpdateCheckRunning?
   "正在后台检查来源更新 · 不锁定导航，随时可切换页面或取消。":
   "不会在打开页面时自动联网检查。点击「检查全部更新」后只读取更新信息，不会安装 MOD。",11,muted));
  Space(root,8);
  if(modInventoryDirty)
  {
   root.Children.Add(Card(V(T("本地 MOD 文件发生变化",14,gold,true),
    T("请先刷新，避免使用过期版本与补丁健康结论。",12,muted)),"#C727364A",12));
   Space(root,11);
  }
  if(modInventory==null)
  {
   root.Children.Add(Card(V(T("尚未读取本地 MOD",17,ink,true),
    Btn("开始扫描",RefreshMods,"Primary"))));
   SetPage("更新中心",root,restore:ShowModUpdatesCenter);return;
  }
  var updates=GitHubUpdates();
  var workshopSources=WorkshopSources();
  var workshopUpdates=WorkshopUpdates();
  var normal=modInventory.Mods.Where(m=>!ModPresentation.IsIndependentPatch(m)).ToArray();
  int githubAvailable=normal.Count(m=>updates.Cached(m.Id)?.Status=="有更新");
  int workshopAvailable=normal.Count(m=>workshopUpdates.Cached(m.Id)?.Status=="有更新");
  int workshopReview=normal.Count(m=>workshopUpdates.Cached(m.Id)?.Status is "待首次核验" or "本地文件已变化");
  int workshopBound=normal.Count(m=>workshopSources.Get(m.Id)!=null);
  var stats=new WrapPanel{Margin=new Thickness(0,0,0,14)};
  foreach(var (value,label) in new[]{
   (normal.Length.ToString(),"普通 MOD"),
   (githubAvailable.ToString(),"GitHub 有更新"),
   (workshopAvailable.ToString(),"创意工坊有更新"),
   (workshopReview.ToString(),"创意工坊待核验"),
   (workshopBound.ToString(),"已关联创意工坊")})
  {
   var stat=V(T(value,25,ink,true),T(label,12,muted));
   var tile=Card(stat,"#B720344E",14);tile.Width=165;tile.Margin=new Thickness(0,0,11,8);
   stats.Children.Add(tile);
  }
  root.Children.Add(stats);
  if(modInventory.Issues.Count>0)
  {
   root.Children.Add(Card(V(T("本地 MOD 清单存在待核对问题",13,gold,true),
    T(string.Join("\n",modInventory.Issues.Take(3)),11,muted)),"#C727364A",11));
   Space(root,10);
  }
  var search=new TextBox{MinWidth=160,Margin=new Thickness(0,0,0,10)};
  search.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,"搜索 MOD 更新");
  root.Children.Add(search);
  var split=new Grid();
  split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
  split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(12)});
  split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(300)});
  var left=new StackPanel();var right=new StackPanel();
  split.Children.Add(left);Grid.SetColumn(right,2);split.Children.Add(right);
  void Select(ManagedMod mod)
  {
   right.Children.Clear();
   var repo=updates.Repository(mod.Id);var githubInfo=updates.Cached(mod.Id);
   ulong? workshopId=workshopSources.Get(mod.Id);
   var workshopInfo=workshopUpdates.Cached(mod.Id);
   var detail=V(T(mod.Name,17,ink,true),T("本地版本 "+mod.Version+" · "+mod.Author,12,muted));
   Space(detail,12);
   detail.Children.Add(T("GitHub 更新检测",14,ink,true));Space(detail,6);
   detail.Children.Add(T(repo==null?"尚未关联 GitHub 仓库":"已关联："+repo,12,muted));
   Space(detail,6);
   detail.Children.Add(T(githubInfo==null?"尚未检查 GitHub 正式版本":
    githubInfo.Status+(githubInfo.LatestVersion.Length>0?" · 最新 "+githubInfo.LatestVersion:""),13,
    githubInfo?.Status=="有更新"?gold:muted));
   if(githubInfo!=null){Space(detail,5);detail.Children.Add(T(githubInfo.Message,11,muted));}
   Space(detail,9);
   var bind=Btn(repo==null?"关联 GitHub 来源":"管理 GitHub 来源",()=>BindGitHubMod(mod),"Ghost");
   bind.Margin=new Thickness(0,0,0,7);detail.Children.Add(bind);
   var check=Btn("检查 GitHub",()=>CheckGitHubMod(mod),"Ghost");
   check.IsEnabled=repo!=null&&!busy;check.Margin=new Thickness(0,0,0,7);
   detail.Children.Add(check);
   var install=Btn("下载并更新 GitHub MOD",()=>UpdateGitHubMod(mod),"Ghost");
   install.IsEnabled=mod.Enabled&&!busy&&!modInventoryDirty&&githubInfo?.Status=="有更新";
   install.Margin=new Thickness(0,0,0,7);detail.Children.Add(install);

   Space(detail,12);detail.Children.Add(Rule());
   detail.Children.Add(T("Steam 创意工坊更新检测",14,ink,true));Space(detail,6);
   detail.Children.Add(T(workshopId.HasValue?"已关联 Workshop ID："+workshopId.Value:
    "尚未关联创意工坊来源。",12,muted));
   Space(detail,6);
   if(workshopInfo!=null)
   {
    detail.Children.Add(T(workshopInfo.Status,13,workshopInfo.Status=="有更新"?gold:muted));
    Space(detail,4);detail.Children.Add(T(workshopInfo.Message,11,muted));
    if(workshopInfo.RemoteManifestId.Length>0)
    {
     Space(detail,4);
     detail.Children.Add(T("远端 Manifest："+workshopInfo.RemoteManifestId,10,muted));
    }
    Space(detail,4);detail.Children.Add(T("检查于 "+workshopInfo.CheckedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),10,muted));
   }
   else
   {
    detail.Children.Add(T(workshopId.HasValue?"尚未检查创意工坊内容。":
     "关联来源后即可进行只读更新检测。",12,muted));
   }
   Space(detail,9);
   if(workshopId.HasValue)
   {
    var workshopCheck=Btn("检查创意工坊",()=>CheckWorkshopMod(mod),"Ghost");
    workshopCheck.IsEnabled=!busy&&!modInventoryDirty;
    workshopCheck.Margin=new Thickness(0,0,0,7);detail.Children.Add(workshopCheck);
   }
   string workshopAction=workshopId is null?"关联并核验创意工坊":
    workshopInfo?.Status switch
    {
     "待首次核验"=>"首次核验并建立基线",
     "本地文件已变化"=>"重新核验本地文件",
     "有更新"=>"获取更新并预览",
     _=>"从创意工坊重新下载 / 核验"
    };
   var workshopDownload=Btn(workshopAction,()=>DownloadWorkshopMod(mod),"Primary");
   workshopDownload.IsEnabled=mod.Enabled&&!busy&&!transactionWriting&&!modInventoryDirty;
   workshopDownload.Margin=new Thickness(0,0,0,7);detail.Children.Add(workshopDownload);
   detail.Children.Add(Btn("查看完整详情与备份",()=>ShowModDetail(mod.Folder),"Ghost"));
   right.Children.Add(Card(detail,"#B720344E",15));
  }
  void Render()
  {
   left.Children.Clear();
   var matches=normal.Where(m=>(m.Name+" "+m.Id+" "+m.Author)
    .Contains(search.Text,StringComparison.OrdinalIgnoreCase)).ToArray();
   var header=T("显示 "+matches.Length+" / "+normal.Length+" 项 · "+modStatus,12,muted);
   header.Margin=new Thickness(0,0,0,8);left.Children.Add(header);
   foreach(var mod in matches)
   {
    var githubState=updates.Cached(mod.Id);
    var repo=updates.Repository(mod.Id);
    ulong? workshopId=workshopSources.Get(mod.Id);
    var workshopState=workshopUpdates.Cached(mod.Id);
    string githubText=repo==null?"GitHub 未关联":githubState==null?"GitHub 待检查":
     "GitHub "+githubState.Status;
    string workshopText=workshopId==null?"创意工坊未关联":
     "创意工坊 "+(workshopState?.Status??"待检查");
    var row=V(T(mod.Name,14,ink,true),
     T("本地 v"+mod.Version,11,muted),
     T(githubText+" · "+workshopText,12,muted));
    var button=new Button{Content=row,HorizontalContentAlignment=HorizontalAlignment.Stretch,
     HorizontalAlignment=HorizontalAlignment.Stretch,Margin=new Thickness(0,0,0,7),
     Padding=new Thickness(13,10,13,10),MinHeight=72};
    button.Click+=(_,_)=>Select(mod);
    left.Children.Add(button);
   }
   if(matches.Length==0)left.Children.Add(T("没有符合搜索条件的 MOD。",13,muted));
  }
  search.TextChanged+=(_,_)=>Render();
  Render();
  if(normal.Length>0)Select(normal.FirstOrDefault(m=>
   updates.Cached(m.Id)?.Status=="有更新"||workshopUpdates.Cached(m.Id)?.Status=="有更新")??normal[0]);
  else right.Children.Add(Card(T("当前没有可管理的普通 MOD。",13,muted)));
  SetPage("更新中心",root,restore:ShowModUpdatesCenter);

 }
}
