using System.Windows;
using System.Windows.Controls;

namespace SpirePatch;

// Spire Glass update workspace: conservative source discovery and update operations are reused.
public partial class MainWindow
{
 void ShowModUpdatesCenter()
 {
  var root=V(Header("MOD UPDATES","更新中心",
   "GitHub 来源、版本更新与历史备份集中管理。更新前仍需审核安装包。"));
  Space(root,15);
  if(selectedInstallRoot==null)
  {
   root.Children.Add(Card(V(T("请先定位游戏目录",17,ink,true),
    Btn("选择游戏目录",ChooseFolder,"Primary"))));
   SetPage("更新中心",root,restore:ShowModUpdatesCenter);return;
  }
  var bar=new WrapPanel{Margin=new Thickness(0,0,0,12)};
  void AddButton(string label,Action action,string style="Ghost")
  {
   var button=Btn(label,action,style);button.IsEnabled=!busy&&!transactionWriting;
   button.Margin=new Thickness(0,0,8,8);bar.Children.Add(button);
  }
  AddButton("检查全部更新",()=>{
   if(modInventory==null||modInventoryDirty){toast.Text="请先刷新本地 MOD 清单。";RefreshMods();return;}
   CheckGitHubMods();
  },"Primary");
  AddButton("查找 GitHub 来源",DiscoverGitHubMods);
  AddButton("刷新本地 MOD",RefreshMods);
  root.Children.Add(bar);
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
  var normal=modInventory.Mods.Where(m=>!ModPresentation.IsIndependentPatch(m)).ToArray();
  int available=normal.Count(m=>updates.Cached(m.Id)?.Status=="有更新");
  int unbound=normal.Count(m=>updates.Repository(m.Id)==null);
  var stats=new WrapPanel{Margin=new Thickness(0,0,0,14)};
  foreach(var (value,label) in new[]{
   (normal.Length.ToString(),"普通 MOD"),(available.ToString(),"可更新"),(unbound.ToString(),"待关联来源")})
  {
   var stat=V(T(value,25,ink,true),T(label,12,muted));
   var tile=Card(stat,"#B720344E",14);tile.Width=165;tile.Margin=new Thickness(0,0,11,8);
   stats.Children.Add(tile);
  }
  root.Children.Add(stats);
  var search=new TextBox{MinWidth=160,Margin=new Thickness(0,0,0,10)};
  search.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,"搜索 MOD 更新");
  root.Children.Add(search);
  var split=new Grid();
  split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
  split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(12)});
  split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(280)});
  var left=new StackPanel();var right=new StackPanel();
  split.Children.Add(left);Grid.SetColumn(right,2);split.Children.Add(right);
  root.Children.Add(split);
  void Select(ManagedMod mod)
  {
   right.Children.Clear();
   var repo=updates.Repository(mod.Id);var info=updates.Cached(mod.Id);
   var detail=V(T(mod.Name,17,ink,true),T("本地版本 "+mod.Version+" · "+mod.Author,12,muted));
   Space(detail,10);
   detail.Children.Add(T(repo==null?"来源未关联":"已关联："+repo,12,muted));
   Space(detail,7);
   detail.Children.Add(T(info==null?"尚未检查 GitHub 正式版本":info.Status+
    (info.LatestVersion.Length>0?" · 最新 "+info.LatestVersion:""),13,
     info?.Status=="有更新"?gold:muted));
   if(info!=null)
   {
    Space(detail,8);
    detail.Children.Add(T(info.Message,12,muted));
   }
   Space(detail,13);
   var bind=Btn(repo==null?"关联 GitHub 来源":"管理来源关联",()=>BindGitHubMod(mod),"Ghost");
   bind.Margin=new Thickness(0,0,0,7);detail.Children.Add(bind);
   var check=Btn("检查此 MOD",()=>CheckGitHubMod(mod),"Ghost");
   check.IsEnabled=repo!=null&&!busy;check.Margin=new Thickness(0,0,0,7);
   detail.Children.Add(check);
   var install=Btn("下载并更新",()=>UpdateGitHubMod(mod),"Primary");
   install.IsEnabled=mod.Enabled&&!busy&&!modInventoryDirty&&info?.Status=="有更新";
   install.Margin=new Thickness(0,0,0,7);detail.Children.Add(install);
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
    var state=updates.Cached(mod.Id);var repo=updates.Repository(mod.Id);
    string text=repo==null?"待关联 GitHub 来源":state==null?"待检查版本":
     state.Status+(state.LatestVersion.Length>0?" · "+state.LatestVersion:"");
    var row=V(T(mod.Name,14,ink,true),T("v"+mod.Version+" · "+text,12,muted));
    var button=new Button{Content=row,HorizontalContentAlignment=HorizontalAlignment.Stretch,
     HorizontalAlignment=HorizontalAlignment.Stretch,Margin=new Thickness(0,0,0,7),
     Padding=new Thickness(13,10,13,10),MinHeight=59};
    button.Click+=(_,_)=>Select(mod);
    left.Children.Add(button);
   }
   if(matches.Length==0)left.Children.Add(T("没有符合搜索条件的 MOD。",13,muted));
  }
  search.TextChanged+=(_,_)=>Render();
  Render();
  if(normal.Length>0)Select(normal.FirstOrDefault(m=>updates.Cached(m.Id)?.Status=="有更新")??normal[0]);
  else right.Children.Add(Card(T("当前没有可管理的普通 MOD。",13,muted)));
  SetPage("更新中心",root,restore:ShowModUpdatesCenter);
 }
}
