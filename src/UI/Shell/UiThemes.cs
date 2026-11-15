using System.IO;
using System.Text.Json;

namespace SpirePatch;

internal sealed record UiThemePreference(int Schema, string Theme);

internal static class UiThemes
{
 internal const string Classic="classic";
 internal const string Workspace="workspace";
 internal const string AtlasNative="atlas-native";
 internal const string FileName="界面主题.json";
 internal static string Active {get;set;}=Workspace;
 internal static bool IsClassic=>Active==Classic;
 internal static bool IsWorkspace=>Active==Workspace;
 internal static bool IsAtlasNative=>Active==AtlasNative;

 internal static string SettingsPath(string root)=>Path.Combine(root,FileName);

 internal static string Load(string root)
 {
  try
  {
   string file=SettingsPath(root);
   if(!File.Exists(file))return Workspace;
   var pref=JsonSerializer.Deserialize<UiThemePreference>(File.ReadAllText(file));
   if(pref?.Schema==1&&pref.Theme is Classic or Workspace or AtlasNative)return pref.Theme;
  }
  catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException){}
  return Workspace; // A corrupted preference cannot prevent the app from starting.
 }

 internal static void Save(string root,string theme)
 {
  if(theme is not(Classic or Workspace or AtlasNative))throw new ArgumentOutOfRangeException(nameof(theme));
  var file=SettingsPath(root);
  Directory.CreateDirectory(Path.GetDirectoryName(file)!);
  var temp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
  try
  {
   File.WriteAllText(temp,JsonSerializer.Serialize(new UiThemePreference(1,theme),new JsonSerializerOptions{WriteIndented=true}));
   File.Move(temp,file,true);
  }
  finally{if(File.Exists(temp))File.Delete(temp);}
 }
}
