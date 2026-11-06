using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SpirePatch;

public partial class MainWindow
{
 // Pure source binding: never download/install MOD files or establish a manifest baseline.
 void BindWorkshopMod(ManagedMod mod)
 {
  if(selectedInstallRoot==null||busy||transactionWriting)return;
  string gameRoot=selectedInstallRoot;
  ulong? previous=WorkshopSources().Get(mod.Id);
  var dialog=new Window{Title="手动关联 Steam 创意工坊 · "+mod.Name,Owner=this,
   Width=600,Height=345,MinWidth=540,WindowStartupLocation=WindowStartupLocation.CenterOwner,
   ResizeMode=ResizeMode.NoResize};
  var body=new StackPanel{Margin=new Thickness(18)};
  body.Children.Add(T("当前 MOD："+mod.Name+"（"+mod.Id+"）",14,ink,true));
  Space(body,9);
  body.Children.Add(T("输入 Steam Workshop 数字 ID 或 Steam / 尖塔补给站分享链接：",12,muted));
  var input=new TextBox{Text=previous?.ToString()??"",MinHeight=34,Margin=new Thickness(0,8,0,9)};
  input.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,"手动关联 Workshop ID");
  body.Children.Add(input);
  var preview=T("尚未查询远端条目。可以点击“核对条目”读取补给站标题；最终对应关系需要你确认。",11,muted);
  preview.TextWrapping=TextWrapping.Wrap;
  body.Children.Add(preview);
  Space(body,8);
  var confirmed=new CheckBox{Content="我已确认此 Workshop ID 对应当前 MOD，愿意将关联用于导出公共索引",
   Margin=new Thickness(0,5,0,11)};
  body.Children.Add(confirmed);
  var verify=Btn("核对条目",()=>_ = VerifyAsync(),"Ghost");
  var save=Btn(previous.HasValue?"保存 / 修改关联":"保存手动关联",SaveBinding,"Primary");
  body.Children.Add(H(verify,save,Btn("取消",()=>dialog.DialogResult=false,"Ghost")));
  body.Children.Add(T("只保存来源编号，不修改 MOD 文件，也不会建立“已验证本地版本”的更新基线。",11,muted));
  input.TextChanged+=(_,_)=>{confirmed.IsChecked=false;preview.Text="输入已变化，请重新核对来源并确认。";};
  dialog.Content=body;
  input.Focus();

  async Task VerifyAsync()
  {
   if(!WorkshopModInstaller.TryParseWorkshopId(input.Text,out ulong id))
   {
    preview.Text="ID 或链接格式无效。请选择《杀戮尖塔 2》（AppID 2868840）的创意工坊条目。";
    return;
   }
   verify.IsEnabled=false;
   string checkedText=input.Text;
   preview.Text="正在只读查询尖塔补给站条目信息…";
   try
   {
    using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(15));
    using var source=new SpireSupplyStationClient();
    var identity=await source.ReadContentIdentityAsync(SpireSupplyStationClient.SupportedAppId,id,cancel.Token);
    if(!dialog.IsVisible||input.Text!=checkedText)return;
    preview.Text="补给站条目："+identity.Title+"\nWorkshop ID："+identity.PublishedFileId+
     "。请与本地 MOD 名称和作者核对后再勾选确认；远端标题相似不等于内容完全一致。";
   }
   catch(Exception e) when(e is HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException or TimeoutException)
   {
    if(dialog.IsVisible&&input.Text==checkedText)
     preview.Text="在线核对失败："+e.Message+"。可以自行查看创意工坊页面后手动确认，但请勿猜测 ID。";
   }
   finally {if(dialog.IsVisible)verify.IsEnabled=true;}
  }
  void SaveBinding()
  {
   if(!WorkshopModInstaller.TryParseWorkshopId(input.Text,out ulong id))
   {
    preview.Text="Workshop ID 或链接格式无效。";return;
   }
   if(confirmed.IsChecked!=true)
   {
    preview.Text="请先核对 MOD 与创意工坊条目，并勾选人工确认。";return;
   }
   if(!string.Equals(gameRoot,selectedInstallRoot,StringComparison.OrdinalIgnoreCase))
   {
    preview.Text="游戏目录已切换，请重新打开关联窗口。";return;
   }
   if(previous.HasValue&&previous.Value!=id&&MessageBox.Show(dialog,
    "即将把 "+mod.Id+" 的 Workshop ID 从 "+previous+" 改为 "+id+
    "。旧来源的版本验证基线会失效。确定继续？",
    "确认修改来源",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
   try
   {
    WorkshopSources().Bind(mod.Id,id);
    WorkshopUpdates().Invalidate(mod.Id);
    dialog.DialogResult=true;
   }
   catch(Exception e) when(e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
   {preview.Text="无法保存关联："+e.Message;}
  }
  if(dialog.ShowDialog()==true)
  {
   toast.Text="已手动关联 "+mod.Id+" → Workshop "+WorkshopSources().Get(mod.Id)+"；尚未核验本地版本基线。";
   if(page=="更新中心")ShowModUpdatesCenter();
   else if(currentVisit?.Key.StartsWith("mod:",StringComparison.Ordinal)==true)ShowModDetail(mod.Folder);
   else if(page=="模组管理")ShowMods();
  }
 }

 void ExportPublicWorkshopIndex()
 {
  if(selectedInstallRoot==null||modInventory==null)
  {
   MessageBox.Show(this,"请先选择游戏目录并刷新 MOD 清单。","无法导出");
   return;
  }
  try
  {
   PublicWorkshopIndex result=PublicWorkshopSourceExport.Build(modInventory.Mods,WorkshopSources());
   if(result.Mods.Length==0)
   {
    MessageBox.Show(this,"当前 MOD 尚无可导出的 Workshop 关联。请先在 MOD 详情手动绑定来源。",
     "没有可导出的来源",MessageBoxButton.OK,MessageBoxImage.Information);
    return;
   }
   if(MessageBox.Show(this,
    "准备导出 "+result.Mods.Length+" 条 MOD ↔ Workshop ID 关联。\n\n"+
    "导出文件只包含 MOD ID、名称、作者、Workshop ID 与关联方式；不包含本地目录、版本基线、日志和下载凭据。\n\n"+
    "这些记录仅表示本地已关联，并不等于服务器验证过 MOD 内容。请在发给他人或上传 GitHub 前复核条目。",
    "导出公共来源索引",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
   var dialog=new SaveFileDialog{Title="导出可分享的公共 MOD 来源索引",
    FileName=PublicWorkshopSourceExport.DefaultFileName,DefaultExt=".json",Filter="JSON 文件|*.json",
    AddExtension=true,OverwritePrompt=true};
   if(dialog.ShowDialog(this)!=true)return;
   string target=dialog.FileName;
   string temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
   try
   {
    File.WriteAllText(temp,PublicWorkshopSourceExport.Serialize(result),new UTF8Encoding(false));
    File.Move(temp,target,true);
   }
   finally {if(File.Exists(temp))File.Delete(temp);}
   toast.Text="已导出 "+result.Mods.Length+" 条来源关联。检查 JSON 后可在对话中发送给我。";
   MessageBox.Show(this,"已导出 "+result.Mods.Length+" 条 MOD 来源关联。\n\n"+
    "可以把生成的 JSON 发给我审核，再加入 GitHub 公共索引。","导出完成");
  }
  catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidOperationException)
  {MessageBox.Show(this,"导出失败："+e.Message,"来源索引导出错误",MessageBoxButton.OK,MessageBoxImage.Warning);}
 }
}
