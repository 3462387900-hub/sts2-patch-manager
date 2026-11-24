using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpirePatch;

internal static class ModUpdateApplyWorkflowChecks
{
    private sealed class FaultFiles : IModUpdateApplyFileOperations
    {
        readonly PhysicalModUpdateApplyFileOperations inner = new();
        public int MoveCount { get; private set; }
        public int FailMoveAt { get; set; } = -1;
        public bool FailEveryMoveAfterFirst { get; set; }
        public bool DirectoryExists(string p) => inner.DirectoryExists(p);
        public bool FileExists(string p) => inner.FileExists(p);
        public void CreateDirectory(string p) => inner.CreateDirectory(p);
        public IReadOnlyList<string> EnumerateFileSystemEntries(string p) => inner.EnumerateFileSystemEntries(p);
        public FileAttributes GetAttributes(string p) => inner.GetAttributes(p);
        public void CopyFile(string s,string d,bool o) => inner.CopyFile(s,d,o);
        public void MoveDirectory(string s,string d)
        {
            MoveCount++;
            if(MoveCount==FailMoveAt || (FailEveryMoveAfterFirst && MoveCount>1))
                throw new IOException("injected move failure");
            inner.MoveDirectory(s,d);
        }
        public void MoveFile(string s,string d,bool o) => inner.MoveFile(s,d,o);
        public void DeleteDirectory(string p,bool r) => inner.DeleteDirectory(p,r);
        public void DeleteFile(string p) => inner.DeleteFile(p);
        public void WriteAllText(string p,string c) => inner.WriteAllText(p,c);
        public string ReadAllText(string p) => inner.ReadAllText(p);
    }

    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    static Dictionary<string,string> Snapshot(string root) =>
        Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)
            .ToDictionary(p=>Path.GetRelativePath(root,p).Replace('\\','/'),
                p=>Hash(File.ReadAllBytes(p)),StringComparer.OrdinalIgnoreCase);
    static string Fingerprint(string root) => string.Join("\n",Snapshot(root)
        .OrderBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).Select(p=>p.Key+":"+p.Value));

    static (string Game,string Target,ManagedMod Mod,string Stage,string Payload,
        ModUpdateStagingResult Staging) Fixture(string root, bool workshop)
    {
        string game=Path.Combine(root,"game");
        string target=Path.Combine(game,"mods","Fixture");
        string stage=Path.Combine(root,"stage-11111111111111111111111111111111");
        string payload=Path.Combine(stage,"payload");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(game,"SlayTheSpire2.exe"),"fixture-only");
        File.WriteAllText(Path.Combine(game,".spirepatch-fixture"),"never launch");
        string oldManifest="{\"id\":\"fixture.mod\",\"version\":\"1.0.0\",\"has_dll\":true}";
        string newManifest="{\"id\":\"fixture.mod\",\"version\":\"2.0.0\",\"has_dll\":true}";
        File.WriteAllText(Path.Combine(target,"mod_manifest.json"),oldManifest);
        File.WriteAllText(Path.Combine(target,"old.dll"),"old");
        File.WriteAllText(Path.Combine(payload,"mod_manifest.json"),newManifest);
        File.WriteAllText(Path.Combine(payload,"new.dll"),"new");
        var mod=new ManagedMod("fixture.mod","Fixture","1.0.0","test","",target,
            Path.Combine(target,"mod_manifest.json"),true,[]);
        var staging=new ModUpdateStagingResult(ModUpdateStagingState.Succeeded,
            workshop?ModUpdateStagingSource.Workshop:ModUpdateStagingSource.GitHub,"fixture.mod",workshop?"123456":"fixture/repo",target,stage,payload,
            "1.0.0","2.0.0",true,workshop?"987654":null,true,[],1,1,1,
            ModUpdateStagingFailureKind.None,null,null,DateTimeOffset.UtcNow)
        {
            CurrentFileHashes=Snapshot(target),
            StagedFileHashes=Snapshot(payload)
        };
        return(game,target,mod,stage,payload,staging);
    }

    static ModUpdateApplyWorkflowService Service(string root, Func<string,bool>? running=null,
        FaultFiles? fault=null, Action<ModUpdateApplyProgress>? progress=null, Action? defer=null) =>
        new(Path.Combine(root,"isolated-backups"),fault,running??(_=>false),progress,defer);

    static ModUpdateApplyPreview Preview(ModUpdateApplyWorkflowService service,
        (string Game,string Target,ManagedMod Mod,string Stage,string Payload,
        ModUpdateStagingResult Staging) f) => service.CreatePreview(f.Game,f.Mod,f.Staging);

    public static async Task RunAsync(string parent, Action<bool,string> check, bool workshop = false)
    {
        string root=Path.Combine(parent,"MOD 更新覆盖事务测试-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var f=Fixture(root,workshop);
            string original=Fingerprint(f.Target);
            int writingStarts=0, writingEnds=0, quietDefers=0;
            bool watcherWriting=false;
            var service=Service(root,progress:p=>
            {
                if(p.TransactionWriting && !watcherWriting)
                {
                    writingStarts++;
                    watcherWriting=true;
                }
                else if(!p.TransactionWriting && watcherWriting)
                {
                    writingEnds++;
                    watcherWriting=false;
                }
            },defer:()=>quietDefers++);
            var preview=Preview(service,f);
            string expectedUpdated=Fingerprint(f.Payload);
            var result=await service.ApplyAsync(service.Confirm(preview,true));
            string successBackupRoot=Path.Combine(root,"isolated-backups");
            bool resultOk=result.Success && result.Confirmed && !result.RolledBack;
            bool hashesMatch=Fingerprint(f.Target)==expectedUpdated;
            bool stagingClean=!Directory.Exists(f.Stage);
            bool backupClean=!Directory.Exists(successBackupRoot) ||
                !Directory.EnumerateFileSystemEntries(successBackupRoot).Any();
            bool watcherTransitions=writingStarts==1 && writingEnds==1 && !watcherWriting;
            bool quietPeriodNotified=quietDefers>=2;
            check(resultOk && hashesMatch && stagingClean && backupClean &&
                result.CleanupCompleted && watcherTransitions && quietPeriodNotified,
                "MOD 覆盖成功：逐文件 SHA-256 一致、备份与暂存清理、监测安静期回调正确" +
                $" [result={resultOk}; hashes={hashesMatch}; stagingClean={stagingClean}; " +
                $"backupClean={backupClean}; cleanupCompleted={result.CleanupCompleted}; " +
                $"cleanupError={result.CleanupFailureReason ?? "<none>"}; " +
                $"watcherStarts={writingStarts}; watcherEnds={writingEnds}; " +
                $"watcherWriting={watcherWriting}; quietDefers={quietDefers}]");

            // Decline does not create a backup or touch the target.
            f=Fixture(Path.Combine(root,"decline"),workshop); original=Fingerprint(f.Target);
            service=Service(Path.Combine(root,"decline"));
            result=await service.ApplyAsync(service.Confirm(Preview(service,f),false));
            check(!result.Success && result.FailureKind==ModUpdateApplyFailureKind.UserDeclined &&
                Fingerprint(f.Target)==original &&
                !Directory.Exists(Path.Combine(root,"decline","isolated-backups")),
                "拒绝确认不会创建备份或修改目标 MOD");

            // Each independent pre-commit guard rejects before touching the target.
            f=Fixture(Path.Combine(root,"changed"),workshop); original=Fingerprint(f.Target);
            service=Service(Path.Combine(root,"changed"));
            preview=Preview(service,f);
            File.WriteAllText(Path.Combine(f.Target,"outside.txt"),"external");
            string changed=Fingerprint(f.Target);
            result=await service.ApplyAsync(service.Confirm(preview,true));
            check(result.FailureKind==ModUpdateApplyFailureKind.TargetChanged &&
                Fingerprint(f.Target)==changed,"预览后目标 MOD 外部修改会拦截写入");

            f=Fixture(Path.Combine(root,"running"),workshop); original=Fingerprint(f.Target);
            service=Service(Path.Combine(root,"running"),_=>true);
            result=await service.ApplyAsync(service.Confirm(Preview(service,f),true));
            check(result.FailureKind==ModUpdateApplyFailureKind.GameRunning &&
                Fingerprint(f.Target)==original,"游戏运行时拒绝覆盖写入");

            f=Fixture(Path.Combine(root,"identity"),workshop); original=Fingerprint(f.Target);
            service=Service(Path.Combine(root,"identity"));
            preview=Preview(service,f);
            var wrong=preview with { Target=preview.Target with { Id="another.mod" } };
            result=await service.ApplyAsync(service.Confirm(wrong,true));
            check(result.FailureKind==ModUpdateApplyFailureKind.SourceIdentityMismatch &&
                Fingerprint(f.Target)==original,"来源与 MOD ID 不匹配时拒绝写入");

            f=Fixture(Path.Combine(root,"tampered"),workshop); original=Fingerprint(f.Target);
            service=Service(Path.Combine(root,"tampered"));
            preview=Preview(service,f);
            File.WriteAllText(Path.Combine(f.Payload,"new.dll"),"tampered");
            string tampered=Fingerprint(f.Target);
            result=await service.ApplyAsync(service.Confirm(preview,true));
            check(result.FailureKind==ModUpdateApplyFailureKind.StagedArtifactChanged &&
                Fingerprint(f.Target)==tampered,"暂存产物篡改会在写入前被拦截");

            // Cancellation before the critical boundary leaves the original tree intact.
            f=Fixture(Path.Combine(root,"cancel"),workshop); original=Fingerprint(f.Target);
            service=Service(Path.Combine(root,"cancel"));
            using(var cts=new CancellationTokenSource())
            {
                cts.Cancel();
                result=await service.ApplyAsync(service.Confirm(Preview(service,f),true),cts.Token);
            }
            check(result.Cancelled && Fingerprint(f.Target)==original &&
                !Directory.Exists(Path.Combine(root,"cancel","isolated-backups")),
                "关键提交前取消不会创建备份或改变目标");

            // A commit failure restores the exact original tree.
            f=Fixture(Path.Combine(root,"rollback"),workshop); original=Fingerprint(f.Target);
            var fault=new FaultFiles{FailMoveAt=2};
            service=Service(Path.Combine(root,"rollback"),fault:fault);
            result=await service.ApplyAsync(service.Confirm(Preview(service,f),true));
            check(result.RolledBack && result.RecoveryState==ModUpdateApplyRecoveryState.RolledBack &&
                Fingerprint(f.Target)==original,"提交中途失败后自动回滚，原 MOD 哈希完全一致");

            // If rollback itself fails, the only complete backup remains available.
            f=Fixture(Path.Combine(root,"rollback-fails"),workshop);
            fault=new FaultFiles{FailEveryMoveAfterFirst=true};
            service=Service(Path.Combine(root,"rollback-fails"),fault:fault);
            result=await service.ApplyAsync(service.Confirm(Preview(service,f),true));
            check(result.FailureKind==ModUpdateApplyFailureKind.RollbackFailed &&
                result.RecoveryState==ModUpdateApplyRecoveryState.PendingManualRecovery &&
                result.BackupDirectory is not null && Directory.Exists(result.BackupDirectory) &&
                File.Exists(Path.Combine(Path.GetDirectoryName(result.BackupDirectory)!,"transaction.json")),
                "回滚异常时保留完整备份、事务标记及人工恢复路径");

            // Simulate a crash after the original folder was moved aside.
            f=Fixture(Path.Combine(root,"crash"),workshop);
            string backupRoot=Path.Combine(root,"crash","isolated-backups");
            string tx=Guid.NewGuid().ToString("N");
            string txDir=Path.Combine(backupRoot,tx), backup=Path.Combine(txDir,"backup");
            string previous=Path.Combine(f.Game,"mods",".spirepatch-update-"+tx+".previous");
            string prepared=Path.Combine(f.Game,"mods",".spirepatch-update-"+tx+".prepared");
            Directory.CreateDirectory(backup);
            foreach(var pair in Snapshot(f.Target))
            {
                string dest=Path.Combine(backup,pair.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(Path.Combine(f.Target,pair.Key),dest);
            }
            var journal=new ModUpdateApplyTransactionJournal(1,tx,f.Game,f.Mod.Id,f.Target,
                txDir,backup,f.Stage,f.Payload,prepared,previous,"Committing","1.0.0","2.0.0",
                Snapshot(f.Target),Snapshot(f.Payload),DateTimeOffset.UtcNow);
            File.WriteAllText(Path.Combine(txDir,"transaction.json"),JsonSerializer.Serialize(journal));
            Directory.Move(f.Target,previous);
            Directory.CreateDirectory(f.Target);
            File.WriteAllText(Path.Combine(f.Target,"partial.txt"),"interrupted");
            service=Service(Path.Combine(root,"crash"));
            var recovery=service.RecoverPendingTransactions(f.Game);
            check(recovery.RecoveredCount==1 && Fingerprint(f.Target)==original &&
                !Directory.Exists(txDir),"中断恢复可将半写入目录还原为原始 MOD");

            // Cancellation arriving during the commit is intentionally ignored until completion.
            f=Fixture(Path.Combine(root,"cancel-during"),workshop);
            using var during=new CancellationTokenSource();
            bool signalled=false;
            fault=new FaultFiles();
            service=Service(Path.Combine(root,"cancel-during"),fault:fault,progress:p=>
            {
                if(p.Stage=="Committing"&&!signalled){signalled=true;during.Cancel();}
            });
            result=await service.ApplyAsync(service.Confirm(Preview(service,f),true),during.Token);
            check(result.Success && signalled && Fingerprint(f.Target)==expectedUpdated,
                "提交阶段收到取消信号仍完整提交，不留下半写入目录");
        }
        finally
        {
            if(Directory.Exists(root))Directory.Delete(root,true);
        }
    }
}
