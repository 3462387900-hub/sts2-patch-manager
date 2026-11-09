param([Parameter(Mandatory=$true)][string]$ApplicationDirectory)

$ErrorActionPreference = 'Stop'
$app = [IO.Path]::GetFullPath($ApplicationDirectory)
if (!(Test-Path -LiteralPath $app -PathType Container)) {
    throw 'Application directory does not exist.'
}
$original = Join-Path $app 'SpirePatch.exe'
$shortcut = Join-Path $app '00-启动尖塔修补站.lnk'
if (!(Test-Path -LiteralPath $original -PathType Leaf)) {
    throw 'SpirePatch.exe is missing; cannot create the Chinese shortcut.'
}

# Use the Unicode IShellLinkW API instead of WScript.Shell. WScript.Shell
# can reject Chinese shortcut names or Unicode target/working-directory paths.
if (-not ('SpirePatch.ReleasePackaging.WindowsShortcut' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace SpirePatch.ReleasePackaging
{
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRelative, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    public static class WindowsShortcut
    {
        private static object NewLink()
        {
            var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true);
            return Activator.CreateInstance(type);
        }

        public static void Create(string shortcutPath, string targetPath, string workingDirectory, string description)
        {
            object instance = NewLink();
            try
            {
                var link = (IShellLinkW)instance;
                link.SetPath(Path.GetFullPath(targetPath));
                link.SetWorkingDirectory(Path.GetFullPath(workingDirectory));
                link.SetDescription(description);
                link.SetShowCmd(1);
                ((IPersistFile)instance).Save(Path.GetFullPath(shortcutPath), false);
            }
            finally
            {
                if (Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
            }
        }

        public static string GetTarget(string shortcutPath)
        {
            object instance = NewLink();
            try
            {
                ((IPersistFile)instance).Load(Path.GetFullPath(shortcutPath), 0);
                var value = new StringBuilder(32768);
                ((IShellLinkW)instance).GetPath(value, value.Capacity, IntPtr.Zero, 0);
                return value.ToString();
            }
            finally
            {
                if (Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
            }
        }

        public static string GetWorkingDirectory(string shortcutPath)
        {
            object instance = NewLink();
            try
            {
                ((IPersistFile)instance).Load(Path.GetFullPath(shortcutPath), 0);
                var value = new StringBuilder(32768);
                ((IShellLinkW)instance).GetWorkingDirectory(value, value.Capacity);
                return value.ToString();
            }
            finally
            {
                if (Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
            }
        }
    }
}
'@
}

if (Test-Path -LiteralPath $shortcut -PathType Leaf) {
    $target = [IO.Path]::GetFullPath([SpirePatch.ReleasePackaging.WindowsShortcut]::GetTarget($shortcut))
    $working = [IO.Path]::GetFullPath([SpirePatch.ReleasePackaging.WindowsShortcut]::GetWorkingDirectory($shortcut))
    if (![string]::Equals($target, $original, [StringComparison]::OrdinalIgnoreCase) -or
        ![string]::Equals($working, $app, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The Chinese shortcut already exists but points to a different target; refusing to overwrite it.'
    }
}
else {
    [SpirePatch.ReleasePackaging.WindowsShortcut]::Create($shortcut, $original, $app, '启动尖塔修补站')
}

# Re-open and verify the persisted Unicode shortcut before moving documents.
$target = [IO.Path]::GetFullPath([SpirePatch.ReleasePackaging.WindowsShortcut]::GetTarget($shortcut))
$working = [IO.Path]::GetFullPath([SpirePatch.ReleasePackaging.WindowsShortcut]::GetWorkingDirectory($shortcut))
if (![string]::Equals($target, $original, [StringComparison]::OrdinalIgnoreCase) -or
    ![string]::Equals($working, $app, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The saved Chinese shortcut failed target or working-directory verification.'
}

# Documentation is safe to relocate: it is not a runtime or updater dependency.
$documents = @(
    '使用说明.md', '发布指南.md',
    '补丁发布指南.md', '本版更新内容.md',
    '鸣谢与来源.md', 'THIRD-PARTY-NOTICES.md'
)
$docs = Join-Path $app '使用说明'
foreach ($name in $documents) {
    $source = Join-Path $app $name
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) { continue }
    [IO.Directory]::CreateDirectory($docs) | Out-Null
    $target = Join-Path $docs $name
    if (Test-Path -LiteralPath $target) {
        throw ('Documentation already exists in both locations: ' + $name)
    }
    [IO.File]::Move($source, $target)
}

Write-Output ('Portable Chinese shortcut ready: ' + $shortcut)
