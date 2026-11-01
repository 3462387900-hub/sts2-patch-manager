using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SpirePatch;
public record GameCandidate(string Directory,string Source);
public static class GameEnvironment
{
 public static string Root(string path)
 {
  string root=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
  NoLinks(root);
  if(!File.Exists(Path.Combine(root,"SlayTheSpire2.exe")))throw new InvalidOperationException("请选择包含 SlayTheSpire2.exe 的游戏根目录。");
  NoLinks(Path.Combine(root,"SlayTheSpire2.exe"));return root;
 }
 public static void NoLinks(string path)
 {
  for(string? p=Path.GetFullPath(path);p!=null;p=Path.GetDirectoryName(p))if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("暂不支持链接路径："+p);
 }
 public static string Child(string root,string relative)=>BundleInstaller.Child(root,relative);
 public static string? LoadRoot(string settings)
 {
  try{using var d=JsonDocument.Parse(File.ReadAllText(settings));return Root(d.RootElement.GetProperty("gameDirectory").GetString()!);}catch{return null;}
 }
 public static void SaveRoot(string settings,string root)
 {
  root=Root(root);Directory.CreateDirectory(Path.GetDirectoryName(settings)!);string temp=settings+"."+Guid.NewGuid().ToString("N")+".tmp";
  try{File.WriteAllText(temp,JsonSerializer.Serialize(new{gameDirectory=root}));File.Move(temp,settings,true);}finally{if(File.Exists(temp))File.Delete(temp);}
 }
 static IEnumerable<string> Values(string text,string key)=>Regex.Matches(text,"\""+Regex.Escape(key)+"\"\\s*\"((?:\\\\.|[^\"\\\\])*)\"",RegexOptions.IgnoreCase).Select(m=>Regex.Unescape(m.Groups[1].Value));
 public static List<GameCandidate> Discover(string? saved=null,IEnumerable<string>? steamRoots=null,IEnumerable<string>? drives=null)
 {
  var found=new Dictionary<string,GameCandidate>(StringComparer.OrdinalIgnoreCase);
  void Add(string? path,string source){if(string.IsNullOrWhiteSpace(path))return;try{var root=Root(path);if(File.Exists(Path.Combine(root,".spirepatch-fixture")))return;found.TryAdd(root,new(root,source));}catch(Exception ex)when(ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException){}}
  Add(saved,"上次使用的目录");
  if(steamRoots==null){var steam=new List<string>();foreach(var key in new[]{@"HKEY_CURRENT_USER\Software\Valve\Steam",@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam"}){try{if(Registry.GetValue(key,"SteamPath",null) is string p)steam.Add(p);if(Registry.GetValue(key,"InstallPath",null) is string q)steam.Add(q);}catch{}}
   steam.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Steam"));steamRoots=steam;
   foreach(var process in Process.GetProcessesByName("SlayTheSpire2")){using(process){try{Add(Path.GetDirectoryName(process.MainModule?.FileName),"正在运行的游戏");}catch{}}}
  }
  var libraries=new HashSet<string>(steamRoots,StringComparer.OrdinalIgnoreCase);
  foreach(var steam in libraries.ToArray())try{foreach(var path in Values(File.ReadAllText(Path.Combine(steam,"steamapps","libraryfolders.vdf")),"path"))libraries.Add(path);}catch(Exception ex)when(ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException){}
  foreach(var library in libraries){
   try{string apps=Path.Combine(library,"steamapps");if(!Directory.Exists(apps))continue;
    foreach(var file in Directory.EnumerateFiles(apps,"appmanifest_*.acf",SearchOption.TopDirectoryOnly)){
     string text=File.ReadAllText(file);string? name=Values(text,"name").FirstOrDefault();
     if(name==null||!Regex.IsMatch(name,@"^Slay\s*(the\s*)?Spire\s*2$",RegexOptions.IgnoreCase))continue;
     string? dir=Values(text,"installdir").FirstOrDefault();if(dir!=null)Add(Child(Path.Combine(apps,"common"),dir),"Steam 库");
    }
    Add(Path.Combine(apps,"common","Slay the Spire 2"),"Steam 库");Add(Path.Combine(apps,"common","SlayTheSpire2"),"Steam 库");
   }catch(Exception ex)when(ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException){}
  }
  drives??=DriveInfo.GetDrives().Where(d=>d.IsReady&&d.DriveType==DriveType.Fixed).Select(d=>d.RootDirectory.FullName);
  foreach(var drive in drives)foreach(var parent in new[]{"","Games","游戏","SteamLibrary/steamapps/common","Steam/steamapps/common"})foreach(var name in new[]{"Slay the Spire 2","SlayTheSpire2"})Add(Path.Combine(drive,parent.Replace('/',Path.DirectorySeparatorChar),name),"常用安装位置");
  return found.Values.ToList();
 }
 public static string MutexName(string root)
 {
  string json=JsonSerializer.Serialize(Path.GetFullPath(root).TrimEnd('\\').ToLowerInvariant(),new JsonSerializerOptions{Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
  return "Local\\SpirePatch-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
 }
 public static T WithLock<T>(string root,Func<T> action)
 {
  using var mutex=new Mutex(false,MutexName(root));bool held=false;
  try{try{held=mutex.WaitOne(0);}catch(AbandonedMutexException){held=true;}if(!held)throw new InvalidOperationException("另一个管理器正在处理此目录，请稍后重试。");return action();}finally{if(held)mutex.ReleaseMutex();}
 }
 public static bool Running(string root)
 {
  foreach(var name in new[]{"SlayTheSpire2","sts2"})foreach(var process in Process.GetProcessesByName(name))using(process){try{string? path=process.MainModule?.FileName;if(path==null||path.StartsWith(root.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))return true;}catch{return true;}}
  return false;
 }
 public static ProcessStartInfo LaunchInfo(string path)
 {
  string root=Root(path),exe=Path.Combine(root,"SlayTheSpire2.exe");
  if(File.Exists(Path.Combine(root,".spirepatch-fixture")))throw new InvalidOperationException("测试目录不能启动游戏。");
  using var input=File.OpenRead(exe);if(input.Length<64||input.ReadByte()!='M'||input.ReadByte()!='Z')throw new InvalidOperationException("所选目录的游戏程序无效，请重新选择。");
  return new(exe){WorkingDirectory=root,UseShellExecute=false};
 }
 public static bool Launch(string path)=>WithLock(Root(path),()=>{if(Running(Root(path)))return false;var start=LaunchInfo(path);using var process=Process.Start(start)??throw new InvalidOperationException("游戏启动失败。");return true;});
}
