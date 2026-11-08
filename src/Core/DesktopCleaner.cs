using System.Text.Json;

namespace DeskTidy.Core;

internal enum ItemKind { Folder, Shortcut, HiddenOrSystem, Regular }

internal sealed record CleanItem(string Path, string Name, ItemKind Kind, bool WillMove, string Reason);

internal sealed class CleanPlan
{
    public List<CleanItem> Items { get; } = new();
    public int MoveCount => Items.Count(i => i.WillMove);
    public int KeepCount => Items.Count(i => !i.WillMove);
    public IEnumerable<CleanItem> Moves => Items.Where(i => i.WillMove);
}

internal sealed record JournalEntry(
    string From, string To, string Name, int CellX, int CellY, string MovedAt);

/// <summary>
/// 清理引擎。
///
/// 规则（和你说定的一致）：
///   保留：目录、.lnk/.url/.appref-ms、隐藏/系统属性、desktop.ini、锚点文件夹本身
///   清理：其余普通文件
///   永不触碰：C:\Users\Public\Desktop、锚点文件夹内部
/// 移动用 File.Move（同盘 rename，毫秒级，不触发桌面重绘）。
/// </summary>
internal static class DesktopCleaner
{
    private static readonly HashSet<string> ShortcutExt = new(StringComparer.OrdinalIgnoreCase)
        { ".lnk", ".url", ".appref-ms", ".website" };

    public static string DesktopDir
    {
        get => string.IsNullOrWhiteSpace(DesktopOverride)
            ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            : DesktopOverride;
    }

    /// <summary>仅测试用：把"桌面"指到另一个目录。</summary>
    public static string DesktopOverride { get; set; } = "";

    public static CleanPlan Scan(string anchor)
    {
        var plan = new CleanPlan();
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(DesktopDir);
        }
        catch (Exception ex)
        {
            AppLog.Error($"枚举桌面失败：{ex.Message}");
            return plan;
        }

        foreach (var path in entries.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path);
            try
            {
                // 绝不移动程序自己（exe 放在桌面时尤其危险）
                if (IsSelf(path))
                {
                    plan.Items.Add(new CleanItem(path, name, ItemKind.HiddenOrSystem, false, "本程序"));
                    continue;
                }

                if (Directory.Exists(path))
                {
                    plan.Items.Add(new CleanItem(path, name, ItemKind.Folder, false, "目录"));
                    continue;
                }

                var attrs = File.GetAttributes(path);
                if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                {
                    plan.Items.Add(new CleanItem(path, name, ItemKind.HiddenOrSystem, false, "隐藏/系统"));
                    continue;
                }

                if (string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                {
                    plan.Items.Add(new CleanItem(path, name, ItemKind.HiddenOrSystem, false, "desktop.ini"));
                    continue;
                }

                var ext = Path.GetExtension(path);
                if (ShortcutExt.Contains(ext))
                {
                    plan.Items.Add(new CleanItem(path, name, ItemKind.Shortcut, false, "快捷方式"));
                    continue;
                }

                // 锚点文件夹的同级内容不参与（它本身是目录，已在上面保留）
                plan.Items.Add(new CleanItem(path, name, ItemKind.Regular, true, "普通文件"));
            }
            catch (Exception ex)
            {
                plan.Items.Add(new CleanItem(path, name, ItemKind.Regular, false, $"读取失败：{ex.Message}"));
            }
        }
        return plan;
    }

    private static bool IsSelf(string path)
    {
        try
        {
            var self = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(self) &&
                string.Equals(Path.GetFullPath(path), Path.GetFullPath(self), StringComparison.OrdinalIgnoreCase))
                return true;

            var selfDir = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(selfDir))
            {
                var full = Path.GetFullPath(path);
                var dir = Path.GetFullPath(selfDir);
                if (full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    public static string MakeTargetFolder(string anchor)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmm");
        var dir = Path.Combine(anchor, $"收纳_{stamp}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>执行移动。返回 (移动数, 失败数, 目标目录, 日志条目)。</summary>
    public static (int Moved, int Failed, string Dir, List<JournalEntry> Entries) Execute(
        CleanPlan plan, string anchor, IReadOnlyDictionary<string, (int X, int Y)> cells)
    {
        var entries = new List<JournalEntry>();
        int moved = 0, failed = 0;
        string dir = "";
        try
        {
            dir = MakeTargetFolder(anchor);
        }
        catch (Exception ex)
        {
            AppLog.Error($"创建收纳目录失败：{ex.Message}");
            return (0, 0, "", entries);
        }

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in plan.Moves)
        {
            try
            {
                var dest = UniqueDest(dir, item.Name, taken);
                taken.Add(dest);
                File.Move(item.Path, dest);

                cells.TryGetValue(item.Path, out var cell);
                entries.Add(new JournalEntry(item.Path, dest, item.Name, cell.X, cell.Y,
                    DateTime.Now.ToString("O")));
                moved++;
            }
            catch (Exception ex)
            {
                failed++;
                AppLog.Warn($"移动失败 {item.Name}：{ex.Message}");
            }
        }

        Journal.Save(entries);
        AppLog.Info($"清理完成：移动 {moved}，失败 {failed}，目标 {dir}");
        return (moved, failed, dir, entries);
    }

    /// <summary>
    /// 按日志把文件移回原位。返回（成功数、失败数、仍需保留的条目）。
    /// 绝不覆盖同名文件：目标已存在时加后缀。
    /// </summary>
    public static (int Ok, int Fail, List<JournalEntry> Remaining) RestoreFiles(IEnumerable<JournalEntry> entries)
    {
        int ok = 0, fail = 0;
        var remaining = new List<JournalEntry>();

        foreach (var e in entries.Reverse())
        {
            try
            {
                if (!File.Exists(e.To))
                {
                    AppLog.Warn($"还原跳过（文件已不在收纳夹）：{e.Name}");
                    fail++;
                    remaining.Add(e);
                    continue;
                }

                var dir = Path.GetDirectoryName(e.From);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var target = e.From;
                if (File.Exists(target))
                {
                    var stem = Path.GetFileNameWithoutExtension(target);
                    var ext = Path.GetExtension(target);
                    for (int i = 2; ; i++)
                    {
                        var cand = Path.Combine(dir ?? "", $"{stem} ({i}){ext}");
                        if (!File.Exists(cand)) { target = cand; break; }
                    }
                }

                File.Move(e.To, target);
                ok++;
            }
            catch (Exception ex)
            {
                fail++;
                remaining.Add(e);
                AppLog.Warn($"还原失败 {e.Name}：{ex.Message}");
            }
        }

        Journal.Save(remaining);
        AppLog.Info($"还原：成功 {ok}，失败 {fail}");
        return (ok, fail, remaining);
    }

    private static string UniqueDest(string dir, string name, HashSet<string> taken)    {
        var dest = Path.Combine(dir, name);
        if (!File.Exists(dest) && !taken.Contains(dest)) return dest;

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !taken.Contains(candidate)) return candidate;
        }
    }
}

/// <summary>撤销日志。还原时按记录的 From/To 精确回推，不扫目录。</summary>
internal static class Journal
{
    private static readonly object Gate = new();

    private static string File_ => Path.Combine(AppPaths.DataDir, "journal.json");

    public static List<JournalEntry> Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(File_)) return new List<JournalEntry>();
                var list = JsonSerializer.Deserialize<List<JournalEntry>>(File.ReadAllText(File_));
                return list ?? new List<JournalEntry>();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"读日志失败：{ex.Message}");
                return new List<JournalEntry>();
            }
        }
    }

    public static void Save(List<JournalEntry> entries)
    {
        lock (Gate)
        {
            try
            {
                File.WriteAllText(File_, JsonSerializer.Serialize(entries,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AppLog.Warn($"写日志失败：{ex.Message}"); }
        }
    }

    public static void Append(List<JournalEntry> entries)
    {
        var all = Load();
        all.AddRange(entries);
        Save(all);
    }

    public static void Clear() => Save(new List<JournalEntry>());
}
