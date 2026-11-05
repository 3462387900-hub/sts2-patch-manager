using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace SpirePatch;

internal static class WorkshopZipChecks
{
 public static void Run(string output)
 {
  string parent=Path.GetDirectoryName(Path.GetFullPath(output))!;
  Directory.CreateDirectory(parent);
  string test=Path.Combine(parent,"zip-import-"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(test);
  var checks=new List<string>();
  void Check(bool yes,string title)
  {
   if(!yes)throw new InvalidOperationException(title);
   checks.Add("PASS "+title);
   File.WriteAllLines(output,checks);
  }
  void Reject(Action run,string title)
  {
   bool rejected=false;
   try{run();}catch(Exception ex)when(ex is InvalidDataException or InvalidOperationException or IOException){rejected=true;}
   Check(rejected,title);
  }
  string root=WorkspaceChecks.Fixture(test);
  string zip=Path.Combine(test,"simple.zip");
  const string manifest= """{"id":"ImportedSample","name":"Imported sample","version":"1.0","has_dll":true,"dependencies":[]}""";
  void Archive(string path,params (string Name,string Content)[] entries)
  {
   using var outputStream=File.Create(path);
   using var archive=new ZipArchive(outputStream,ZipArchiveMode.Create);
   foreach(var (name,content) in entries)
   {
    var entry=archive.CreateEntry(name);
    using var writer=new StreamWriter(entry.Open(),new UTF8Encoding(false));
    writer.Write(content);
   }
  }
  Archive(zip,("3830005491/ImportedSample/mod_manifest.json",manifest),
    ("3830005491/ImportedSample/ImportedSample.dll","safe fixture bytes"));
  var plan=WorkshopZipImport.Preview(root,zip);
  Check(plan.ModId=="ImportedSample"&&plan.FileCount==2&&plan.FolderName=="ImportedSample"&&
   !Directory.Exists(plan.Destination),"preview reads a wrapped browser ZIP without installing");
  WorkshopZipImport.Apply(plan);
  Check(File.Exists(Path.Combine(root,"mods","ImportedSample","ImportedSample.dll"))&&
    ModManager.Scan(root).Mods.Count(m=>m.Id=="ImportedSample")==1,
    "approved ZIP installs one MOD into a dedicated directory");
  Reject(()=>WorkshopZipImport.Preview(root,zip),"duplicate enabled MOD is refused");
  Archive(Path.Combine(test,"unsafe.zip"),("../escape.txt","bad"),("mod_manifest.json",manifest),("ImportedSample.dll","x"));
  Reject(()=>WorkshopZipImport.Preview(root,Path.Combine(test,"unsafe.zip")),"parent traversal is blocked");
  Archive(Path.Combine(test,"mixed.zip"),("a/mod_manifest.json",manifest),("a/ImportedSample.dll","x"),("b/unrelated.txt","bad"));
  Reject(()=>WorkshopZipImport.Preview(root,Path.Combine(test,"mixed.zip")),"unrelated ZIP content is blocked");
  string separateRoot=WorkspaceChecks.Fixture(test);
  var different=Path.Combine(test,"different.zip");
  Archive(different,("NewEntry/mod_manifest.json",manifest.Replace("ImportedSample","NewEntry")),
    ("NewEntry/NewEntry.dll","initial"));
  var approved=WorkshopZipImport.Preview(separateRoot,different);
  Archive(different,("NewEntry/mod_manifest.json",manifest.Replace("ImportedSample","NewEntry")),
    ("NewEntry/NewEntry.dll","tampered"));
  Reject(()=>WorkshopZipImport.Apply(approved),"modified ZIP invalidates previous approval");
  Check(!Directory.Exists(Path.Combine(separateRoot,"mods","NewEntry")),"failed approval leaves no MOD installed");
  Check(!Directory.EnumerateDirectories(separateRoot,".spirepatch-import-*").Any(),"failed import leaves no staging folders");
  Archive(Path.Combine(test,"reserved.zip"),("CON/mod_manifest.json",manifest),
    ("CON/ImportedSample.dll","x"));
  Reject(()=>WorkshopZipImport.Preview(separateRoot,Path.Combine(test,"reserved.zip")),"reserved Windows names are blocked");
  Archive(Path.Combine(test,"duplicate.zip"),("Dup/mod_manifest.json",manifest.Replace("ImportedSample","Dup")),
   ("Dup/Dup.dll","a"),("dup/dup.dll","b"));
  Reject(()=>WorkshopZipImport.Preview(separateRoot,Path.Combine(test,"duplicate.zip")),"case-insensitive duplicate archive paths are blocked");
  Check(checks.Count==10,"ZIP import checks completed");
 }
}
