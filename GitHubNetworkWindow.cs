using System.Windows;
using System.Windows.Controls;

namespace SpirePatch;
public partial class MainWindow
{
 void ConfigureGitHubNetwork()
 {
  if(busy||transactionWriting||managerUpdateBusy)return;
  var existing=GitHubNetwork.Load(installer.DataRoot);
  var dialog=new Window{
   Title="GitHub 网络连接设置",Owner=this,Width=610,Height=335,
   MinWidth=540,WindowStartupLocation=WindowStartupLocation.CenterOwner,
   ResizeMode=ResizeMode.NoResize
  };
  var body=new StackPanel{Margin=new Thickness(20)};
  body.Children.Add(T("网络模式",17,ink,true));
  Space(body,8);
  var modes=new ComboBox{ItemsSource=new[]{
    "跟随 Windows 系统代理（默认）",
    "使用本机 HTTP 代理（例如 Clash / sing-box）"
   },SelectedIndex=existing.Mode=="local"?1:0,MinHeight=34};
  body.Children.Add(modes);
  Space(body,12);
  var proxy=new TextBox{
   Text=existing.Mode=="local"?existing.ProxyUrl:"http://127.0.0.1:7890",
   MinHeight=31,ToolTip="只支持本机 HTTP 代理地址，不支持账号密码。"
  };
  proxy.IsEnabled=modes.SelectedIndex==1;
  modes.SelectionChanged+=(_,_)=>proxy.IsEnabled=modes.SelectedIndex==1;
  body.Children.Add(T("本机代理地址（只在选择手动代理时生效）",12,muted));
  body.Children.Add(proxy);
  Space(body,11);
  var hint=T("不会修改 Windows 代理设置，不会读取或保存 GitHub Token。保存后请重新启动管理器，再测试连接。",12,muted);
  hint.TextWrapping=TextWrapping.Wrap;
  body.Children.Add(hint);
  Space(body,16);
  var save=Btn("保存网络设置",()=>{
   try
   {
    var mode=modes.SelectedIndex==1?"local":"system";
    GitHubNetwork.Save(new(1,mode,mode=="local"?proxy.Text:""),installer.DataRoot);
    dialog.DialogResult=true;
   }
   catch(Exception ex){hint.Text="保存失败："+ex.Message;}
  },"Primary");
  body.Children.Add(H(save,Btn("取消",()=>dialog.DialogResult=false,"Ghost")));
  dialog.Content=body;
  if(dialog.ShowDialog()==true)
  {
   toast.Text="GitHub 网络设置已保存。请完全退出并重新打开管理器，使所有连接使用新设置。";
   if(currentVisit?.Key=="工具与设置")ShowTools();
  }
 }
 async void DiagnoseGitHubNetwork()
 {
  if(busy||managerUpdateBusy||transactionWriting)return;
  busy=true;
  toast.Text="正在检查 GitHub API、原始文件和发布页连接…";
  try
  {
   var entries=await GitHubNetwork.DiagnoseAsync(root:installer.DataRoot);
   var message="当前模式："+(GitHubNetwork.Load(installer.DataRoot).Mode=="local"?
    "本机 HTTP 代理":"Windows 系统网络")+"\n\n"+
    string.Join("\n\n",entries.Select(e=>e.Name+" ("+e.Host+")："+e.Status+
     " · "+e.ElapsedMilliseconds+" 毫秒"))+
    "\n\n如果只有下载速度慢，这个测试不能代表附件下载速度。可以尝试管理器更新页的官方备用通道。";
   MessageBox.Show(this,message,"GitHub 网络诊断",MessageBoxButton.OK,
    entries.All(e=>e.Success)?MessageBoxImage.Information:MessageBoxImage.Warning);
  }
  catch(Exception ex){MessageBox.Show(this,"诊断未完成："+ex.Message,"GitHub 网络诊断");}
  finally{busy=false;}
 }
}
