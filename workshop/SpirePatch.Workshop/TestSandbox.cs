using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpirePatch.Workshop;

/// <summary>Explicitly initialized, isolated test storage. No game discovery or installation API.</summary>
public sealed class TestSandbox
{
    const string Marker = ".spirepatch-workshop-test";
    const string MarkerValue = "SpirePatch Workshop isolated test storage v1";
    public string Root { get; }
    public TestSandbox(string root)
    {
        Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        CheckAncestors(Root);
        if (!File.Exists(Child(Marker)) || File.ReadAllText(Child(Marker)) != MarkerValue)
            throw new WorkshopException("UnsafeDirectory", "测试目录未初始化。使用 init 创建全新的独立目录。");
    }

    public static TestSandbox Initialize(string root)
    {
        root = Path.GetFullPath(root);
        CheckAncestors(root);
        if (Directory.Exists(root) || File.Exists(root))
            throw new WorkshopException("UnsafeDirectory", "init 仅允许创建不存在的新目录，不接管任何已有目录。");
        Directory.CreateDirectory(root);
        CheckAncestors(root);
        using (var stream = new FileStream(Path.Combine(root, Marker), FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(stream)) writer.Write(MarkerValue);
        return new(root);
    }

    static void CheckAncestors(string path)
    {
        for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new WorkshopException("UnsafePath", "拒绝符号链接或目录联接：" + current.FullName);
            if (File.Exists(Path.Combine(current.FullName, "SlayTheSpire2.exe"))
                || new[] { "steamapps", "mods", "saves", "SlayTheSpire2", "AppData" }.Contains(current.Name, StringComparer.OrdinalIgnoreCase))
                throw new WorkshopException("UnsafeDirectory", "测试目录不能位于游戏、Steam 内容、MOD 或存档目录内。");
        }
    }

    public static string NormalizeRelative(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 220 || Path.IsPathRooted(path))
            throw new WorkshopException("UnsafePath", "无效的相对路径。");
        var parts = path.Replace('\\', '/').Split('/');
        foreach (var p in parts)
        {
            if (p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ')
                || p.Any(c => c < 32 || "<>:\"|?*".Contains(c))
                || Regex.IsMatch(p.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])$", RegexOptions.IgnoreCase))
                throw new WorkshopException("UnsafePath", "拒绝路径穿越、Windows 设备名或不安全文件名：" + path);
        }
        return string.Join('/', parts);
    }

    public string Child(string relative)
    {
        relative = NormalizeRelative(relative);
        CheckAncestors(Root);
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new WorkshopException("UnsafePath", "路径超出测试目录。");
        var current = Root;
        foreach (var part in relative.Split('/'))
        {
            current = Path.Combine(current, part);
            // GetAttributes also detects file links. Nonexistent paths alone are permitted.
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new WorkshopException("UnsafePath", "拒绝文件链接或目录联接：" + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }

    public void MakeDirectory(string relative) => Directory.CreateDirectory(Child(relative));
    public FileStream Lock()
    {
        try { return new FileStream(Child(".download.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new WorkshopException("Busy", "该测试目录已有下载任务。", e); }
    }
    public async Task WriteJsonAsync<T>(string relative, T value, CancellationToken ct = default)
        => await WriteBytesAsync(relative, JsonSerializer.SerializeToUtf8Bytes(value, WorkshopJson.Options), ct);

    public async Task WriteBytesAsync(string relative, byte[] bytes, CancellationToken ct)
    {
        var target = Child(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = relative + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var stream = new FileStream(Child(temporary), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await stream.WriteAsync(bytes, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(Child(temporary), Child(relative), overwrite: true); // replaces directory entry, never writes through hard links
        }
        finally { if (File.Exists(Child(temporary))) File.Delete(Child(temporary)); }
    }
}
