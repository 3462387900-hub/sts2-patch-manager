using System.IO;
using System.Windows;

namespace SpirePatch;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if(e.Args.Contains("--update-self-test")) { await UpdateChecks.RunAsync(e.Args.Last()); Shutdown(0); return; }
            if(e.Args.Contains("--manager-update-check")||e.Args.Contains("--manager-update-download-check")) { using var updates=new ManagerUpdates();var release=await updates.CheckAsync();string? downloaded=null;if(e.Args.Contains("--manager-update-download-check")){downloaded=Path.Combine(Path.GetDirectoryName(e.Args.Last())!,release.FileName);await updates.DownloadAsync(release,downloaded);}File.WriteAllText(e.Args.Last(),System.Text.Json.JsonSerializer.Serialize(new{currentVersion=ManagerUpdates.CurrentVersion,newer=ManagerUpdates.IsNewer(release,ManagerUpdates.CurrentVersion),release,downloaded,sha256=downloaded==null?null:Catalog.Hash(downloaded)},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));Shutdown(0);return; }
            if(e.Args.Contains("--environment-check")) { var candidates=await Task.Run(()=>GameEnvironment.Discover());var reports=candidates.Select(c=>{var mods=ModManager.Scan(c.Directory);return new{c.Directory,c.Source,count=mods.Mods.Count,enabled=mods.Mods.Count(m=>m.Enabled),issues=mods.Issues};}).ToArray();File.WriteAllText(e.Args.Last(),System.Text.Json.JsonSerializer.Serialize(reports,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));Shutdown(0);return; }
            if(e.Args.Contains("--workspace-self-test")) { await WorkspaceChecks.RunAsync(e.Args[1],e.Args[2]); Shutdown(0); return; }
            if(e.Args.Contains("--selection-self-test")) { await SelectionChecks.RunAsync(e.Args[1],e.Args[2],e.Args[3]); Shutdown(0); return; }
            if(e.Args.Contains("--recommendation-self-test")) { RecommendationChecks.Run(e.Args.Last()); Shutdown(0); return; }
            if(e.Args.Contains("--self-test")) { CoreChecks.Run(e.Args.Last()); Shutdown(0); return; }
            if(e.Args.Contains("--online-self-test")) { await OnlineChecks.RunAsync(e.Args.Last()); Shutdown(0); return; }
            if(e.Args.Contains("--installer-self-test")) { await InstallerChecks.RunAsync(e.Args[1],e.Args[2],e.Args[3]); Shutdown(0); return; }
            if(e.Args.Contains("--online-check")) { using var source=new OnlineSource();using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));var data=await source.LoadAsync(timeout.Token);var destination=Path.Combine(Path.GetDirectoryName(e.Args.Last())!,data.Bundle.FileName);await source.DownloadAsync(data.Bundle,destination,ct:timeout.Token);File.WriteAllText(e.Args.Last(),System.Text.Json.JsonSerializer.Serialize(new{count=data.Patches.Count,data.Commit,download=destination,sha256=Catalog.Hash(destination),anonymous=true}));Shutdown(0);return; }
            if(e.Args.Contains("--online-scan-check")) { using var source=new OnlineSource();var catalog=await source.LoadAsync();var report=await Task.Run(()=>Catalog.Scan(e.Args[1],catalog.Patches,catalog.Commit));File.WriteAllText(e.Args.Last(),System.Text.Json.JsonSerializer.Serialize(report,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));Shutdown(0);return; }
            if(e.Args.Contains("--scan-check")) { var report=await Task.Run(()=>Catalog.Scan(e.Args[1])); File.WriteAllText(e.Args.Last(),System.Text.Json.JsonSerializer.Serialize(report,new System.Text.Json.JsonSerializerOptions{WriteIndented=true})); Shutdown(0);return; }
            var window=new MainWindow(!e.Args.Any(a=>a.StartsWith("--capture"))); MainWindow=window;
            if(e.Args.Any(a=>a.StartsWith("--capture"))) { window.Left=-15000;window.Top=-15000;window.ShowActivated=false; }
            window.Show();
            if(e.Args.Contains("--capture-updates")) { await window.CaptureUpdateChecksAsync(e.Args.Last()); Shutdown(); }
            if(e.Args.Contains("--capture-workspace")) { await window.CaptureWorkspaceAsync(e.Args[1],e.Args[2]); Shutdown(); }
            if(e.Args.Contains("--capture-mods")) { await window.CaptureLiveModsAsync(e.Args[1],e.Args[2]); Shutdown(); }
            if(e.Args.Contains("--capture-selection")) { await window.CaptureSelectionAsync(e.Args[1],e.Args[2],e.Args[3]); Shutdown(); }
            if(e.Args.Contains("--capture-maintenance")) { await window.CaptureMaintenanceAsync(e.Args[1],e.Args[2],e.Args[3]); Shutdown(); }
            if(e.Args.Contains("--capture-recommendations")) { await window.CaptureRecommendationsAsync(e.Args[1],e.Args[2]); Shutdown(); }
            if(e.Args.Contains("--capture")) { await window.CaptureAsync(e.Args.Last()); Shutdown(); }
            if(e.Args.Contains("--capture-online")) { await window.CaptureOnlineAsync(e.Args.Last()); Shutdown(); }
            if(e.Args.Contains("--capture-installer")) { await window.CaptureInstallerAsync(e.Args[1],e.Args[2],e.Args[3]); Shutdown(); }
        }
        catch(Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"startup-error.txt"),ex.ToString()); if(e.Args.Length==0)MessageBox.Show(ex.Message,"尖塔修补站");Shutdown(1); }
    }
}
