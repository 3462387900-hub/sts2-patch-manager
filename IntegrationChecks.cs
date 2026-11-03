using System.IO;
namespace SpirePatch;
internal static class IntegrationChecks
{
 public static async Task WorkspaceAsync(string output){
  string home=Path.Combine(Path.GetDirectoryName(output)!,"工作台集成-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(home);
  using var source=new OnlineSource();using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
  string zip=Path.Combine(home,BundleInstaller.SupportedBundle.FileName);
  await source.DownloadAsync(BundleInstaller.SupportedBundle,zip,ct:timeout.Token);
  await WorkspaceChecks.RunAsync(zip,output);
 }
 public static async Task RunAsync(string zip,string fixture,string output){
  InstallerChecks.AssertFixture(fixture);
  string home=Path.Combine(Path.GetDirectoryName(output)!,"完整集成-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(home);
  void Copy(string from,string to){GameEnvironment.NoLinks(from);Directory.CreateDirectory(to);foreach(var file in Directory.EnumerateFiles(from)){GameEnvironment.NoLinks(file);File.Copy(file,Path.Combine(to,Path.GetFileName(file)));}foreach(var dir in Directory.EnumerateDirectories(from))Copy(dir,Path.Combine(to,Path.GetFileName(dir)));}
  string Fresh(string name){string root=Path.Combine(home,name);Directory.CreateDirectory(root);foreach(var folder in new[]{"mods","data_sts2_windows_x86_64"})Copy(Path.Combine(fixture,folder),Path.Combine(root,folder));File.WriteAllText(Path.Combine(root,"SlayTheSpire2.exe"),"");File.WriteAllText(Path.Combine(root,".spirepatch-fixture"),"isolated");return root;}
  var lines=new List<string>();
  async Task Run(string name,Func<string,Task> check){string result=Path.Combine(home,name+".txt");await check(result);lines.Add("PASS "+name+" : "+result);File.WriteAllLines(output,lines);}
  await Run("安装回滚",p=>InstallerChecks.RunAsync(zip,Fresh("安装"),p));
  await Run("选择安装",p=>SelectionChecks.RunAsync(zip,Fresh("选择"),p));
  await Run("模组工作台",p=>WorkspaceChecks.RunAsync(zip,p));
  await Run("联机对齐",p=>CoopActionChecks.IntegrationAsync(zip,Fresh("联机"),p));
  await Run("十七项事务",p=>CatalogV3Checks.RunAsync(zip,Fresh("十七项"),p));
 }
}
