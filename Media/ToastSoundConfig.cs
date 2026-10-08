using System.IO;
using System.Text.Json;
using NAudio.Wave;

namespace NotchPeninsula;

internal static class ToastSoundConfig
{
    internal const string NoneName = "无";

    internal const string BrowseName = "浏览音频…";

    internal const string SoundFolderName = "data\\sound";

    internal const long MaxFileSizeBytes = 2 * 1024 * 1024;

    internal const double MaxDurationSec = 5.0;

    private const int MaxBuiltinCount = 40;

    internal static readonly int[] VolumeOptions =
        [0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100];

    internal const int DefaultVolumePercent = 10;

    internal const string FileFilter =
        "音频文件 (*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac;*.aiff;*.aif)\0*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac;*.aiff;*.aif\0" +
        "WAV 波形 (*.wav)\0*.wav\0" +
        "所有文件 (*.*)\0*.*\0";

    // 动态内置列表

    internal readonly record struct BuiltinEntry(string Path, string Label, string ResourceName = "")
    {
        public string FileName => System.IO.Path.GetFileName(
            ResourceName.Length > 0 ? ResourceName : Path);

        public string Stem => System.IO.Path.GetFileNameWithoutExtension(FileName);

        public bool IsEmbedded => ResourceName.Length > 0;
    }

    private static BuiltinEntry[] _builtins = [];
    private static string _folder = "";

    internal static IReadOnlyList<BuiltinEntry> Builtins => _builtins;

    internal static string SoundFolderPath => _folder;

    internal const int BuiltinOffset = 1;

    internal static int CustomIndex => BuiltinOffset + _builtins.Length;

    internal static int OptionCount => BuiltinOffset + _builtins.Length + 1;

    internal static int SelectedIndex = 0;

    internal static string SelectedKey = "";

    internal static string CustomPath = "";

    internal static bool IsEnabled = false;

    internal static bool IsRowEnabled => IsEnabled;

    internal static bool IsSourceReady => IsEnabled && SelectedIndex > 0 && SelectedIndex < OptionCount;

    internal static int VolumePercent = DefaultVolumePercent;

    internal static void RefreshBuiltins()
    {
        _optionLabelsCache = null;

        var byName = new Dictionary<string, BuiltinEntry>(StringComparer.OrdinalIgnoreCase);
        _folder = "";

        // ① exe 内嵌资源：exe 里始终带着的那份内置音。
        foreach (string name in DataResources.ListEmbedded("data/sound", ".wav"))
        {
            string stem = Path.GetFileNameWithoutExtension(name);
            if (stem.Length == 0) continue;
            byName[name] = new BuiltinEntry("", stem, "data/sound/" + name);
        }

        foreach (string dir in CandidateFolders())
        {
            try
            {
                if (!Directory.Exists(dir)) continue;

                foreach (string f in Directory.GetFiles(dir, "*.wav", SearchOption.TopDirectoryOnly))
                {
                    string stem = Path.GetFileNameWithoutExtension(f);
                    if (stem.Length == 0) continue;
                    byName[Path.GetFileName(f)] = new BuiltinEntry(f, stem, "");
                }

                _folder = dir;
                break; // 找到第一个能用的目录就不再往下找
            }
            catch
            {
            }
        }

        var list = byName.Values
            .OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase)
            .Take(MaxBuiltinCount)
            .ToList();

        ApplyDisplayNames(list);
        _builtins = list.ToArray();

        // 列表一变就必须把当前选择「按文件名身份」重新对齐一次：
        if (NormalizeSelection()) PersistSelection();
    }

    private static IEnumerable<string> CandidateFolders()
    {
        string baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "data", "sound");
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "data", "sound"));
    }

    /// 可选的显示名覆盖：data\sound\sound.json，形如 { "Tri-Tone": "三全音", "QQ": "QQ 消息" }。
    private static void ApplyDisplayNames(List<BuiltinEntry> list)
    {
        if (list.Count == 0) return;

        string? json = DataResources.ReadAllText("data/sound/sound.json");
        if (string.IsNullOrEmpty(json)) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

            for (int i = 0; i < list.Count; i++)
            {
                if (doc.RootElement.TryGetProperty(list[i].Stem, out var v)
                    && v.ValueKind == JsonValueKind.String)
                {
                    string? label = v.GetString();
                    if (!string.IsNullOrWhiteSpace(label)) list[i] = list[i] with { Label = label! };
                }
            }
        }
        catch
        {
            // 显示名只是锦上添花，坏了就用文件名，不打扰用户
        }
    }

    internal static string[] BuildOptionLabels()
    {
        var cached = _optionLabelsCache;
        if (cached != null) return cached;

        var labels = new string[OptionCount];
        labels[0] = NoneName;
        for (int i = 0; i < _builtins.Length; i++)
            labels[BuiltinOffset + i] = _builtins[i].Label;
        labels[CustomIndex] = BrowseName;

        _optionLabelsCache = labels;
        return labels;
    }

    private static string[]? _optionLabelsCache;

    internal static string CurrentDisplayText()
    {
        if (SelectedIndex <= 0 || SelectedIndex >= OptionCount) return NoneName;
        if (SelectedIndex == CustomIndex)
            return IsUsableFile(CustomPath, out _) ? ShortenPath(CustomPath) : "自定义（不可用）";
        return _builtins[SelectedIndex - BuiltinOffset].Label;
    }

    internal readonly record struct SoundSource(string Path, string ResourceName)
    {
        internal bool IsValid => Path.Length > 0 || ResourceName.Length > 0;
    }

    internal static SoundSource ResolveCurrentSource()
    {
        if (SelectedIndex <= 0 || SelectedIndex >= OptionCount) return default;

        if (SelectedIndex == CustomIndex)
            return IsUsableFile(CustomPath, out _) ? new SoundSource(CustomPath, "") : default;

        var entry = _builtins[SelectedIndex - BuiltinOffset];
        if (entry.IsEmbedded) return new SoundSource("", entry.ResourceName);
        return File.Exists(entry.Path) ? new SoundSource(entry.Path, "") : default;
    }

    internal static bool IsUsableFile(string? path, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(path)) { reason = "尚未选择音频文件"; return false; }

        string full;
        try { full = Path.GetFullPath(path); }
        catch { reason = "路径无效"; return false; }

        FileInfo fi;
        try { fi = new FileInfo(full); }
        catch { reason = "路径无效"; return false; }

        if (!fi.Exists) { reason = "文件不存在或已被移动"; return false; }
        if (fi.Length == 0) { reason = "文件为空"; return false; }
        if (fi.Length > MaxFileSizeBytes)
        {
            reason = $"文件过大（{fi.Length / 1024.0 / 1024.0:F1} MB，上限 {MaxFileSizeBytes / 1024 / 1024} MB）";
            return false;
        }

        try
        {
            using WaveStream reader = OpenReader(full);
            double sec = reader.TotalTime.TotalSeconds;
            if (sec <= 0.05) { reason = "音频时长过短或无法解析"; return false; }
            if (sec > MaxDurationSec)
            {
                reason = $"音频过长（{sec:F1} 秒，上限 {MaxDurationSec:F0} 秒）";
                return false;
            }
        }
        catch
        {
            reason = "不支持的音频格式或文件已损坏";
            return false;
        }

        return true;
    }

    internal static WaveStream OpenReader(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".wav" => new WaveFileReader(path),
            ".aif" or ".aiff" => new AiffFileReader(path),
            _ => new MediaFoundationReader(path),
        };
    }

    internal static WaveStream OpenReader(Stream stream, string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".wav" => new WaveFileReader(stream),
            ".aif" or ".aiff" => new AiffFileReader(stream),
            _ => throw new NotSupportedException($"嵌入资源只支持 wav / aiff：{extension}"),
        };
    }

    internal static bool IsUsableResource(string resourceName, out string reason)
    {
        reason = "";
        if (string.IsNullOrEmpty(resourceName)) { reason = "内置提示音资源缺失"; return false; }

        try
        {
            using var stream = DataResources.OpenRead(resourceName);
            if (stream == null) { reason = "内置提示音资源缺失"; return false; }
            if (stream.Length == 0) { reason = "内置提示音为空"; return false; }
            if (stream.Length > MaxFileSizeBytes)
            {
                reason = $"文件过大（{stream.Length / 1024.0 / 1024.0:F1} MB，上限 {MaxFileSizeBytes / 1024 / 1024} MB）";
                return false;
            }

            using WaveStream reader = OpenReader(stream, Path.GetExtension(resourceName));
            double sec = reader.TotalTime.TotalSeconds;
            if (sec <= 0.05) { reason = "音频时长过短或无法解析"; return false; }
            if (sec > MaxDurationSec)
            {
                reason = $"音频过长（{sec:F1} 秒，上限 {MaxDurationSec:F0} 秒）";
                return false;
            }
        }
        catch
        {
            reason = "不支持的音频格式或资源已损坏";
            return false;
        }

        return true;
    }

    private static string ShortenPath(string path)
    {
        try
        {
            string name = Path.GetFileName(path);
            string? dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return name;
            string parent = Path.GetFileName(dir);
            return string.IsNullOrEmpty(parent) ? name : $@"…\{parent}\{name}";
        }
        catch { return path; }
    }

    // 选择项的「身份对齐」——索引会漂，文件名不会

    private static int IndexOfKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return -1;
        for (int i = 0; i < _builtins.Length; i++)
            if (string.Equals(_builtins[i].FileName, key, StringComparison.OrdinalIgnoreCase))
                return BuiltinOffset + i;
        return -1;
    }

    internal static void PersistSelection()
    {
        Program.SaveSetting("ToastSoundIndex", SelectedIndex);
        Program.SaveSetting("ToastSoundKey", SelectedKey);
        Program.SaveSetting("ToastSoundPath", CustomPath);
    }

    internal static bool NormalizeSelection()
    {
        int oldIndex = SelectedIndex;
        string oldKey = SelectedKey;
        string oldPath = CustomPath;

        int byKey = IndexOfKey(SelectedKey);

        if (byKey >= 0)
        {
            // ① 按身份找回真实位置（位置可能已经漂了）
            SelectedIndex = byKey;
        }
        else if (SelectedKey.Length > 0)
        {
            // ② 有身份但找不到 → 音源已被删除
            SelectedIndex = 0;
            SelectedKey = "";
            CustomPath = "";
        }
        else if (SelectedIndex <= 0)
        {
            // ③ 「无」
            SelectedIndex = 0;
            SelectedKey = "";
            CustomPath = "";
        }
        else if (SelectedIndex == CustomIndex)
        {
            // ④ 自定义项：文件失效 → 回落「无」
            if (IsUsableFile(CustomPath, out _)) SelectedKey = "";
            else { SelectedIndex = 0; SelectedKey = ""; CustomPath = ""; }
        }
        else if (SelectedIndex >= BuiltinOffset && SelectedIndex < OptionCount)
        {
            SelectedKey = _builtins[SelectedIndex - BuiltinOffset].FileName;
        }
        else
        {
            // 索引越界
            SelectedIndex = 0;
            SelectedKey = "";
            CustomPath = "";
        }

        return SelectedIndex != oldIndex
            || !string.Equals(SelectedKey, oldKey, StringComparison.Ordinal)
            || !string.Equals(CustomPath, oldPath, StringComparison.Ordinal);
    }

    internal static string DescribeUnavailable()
    {
        if (SelectedIndex <= 0 || SelectedIndex >= OptionCount) return "";

        if (SelectedIndex == CustomIndex)
            return IsUsableFile(CustomPath, out string why) ? "" : why;

        var entry = _builtins[SelectedIndex - BuiltinOffset];
        if (entry.IsEmbedded)
            return IsUsableResource(entry.ResourceName, out string whyRes) ? "" : whyRes;

        if (!File.Exists(entry.Path)) return "内置提示音文件缺失（重新放入 data\\sound 目录即可）";
        return IsUsableFile(entry.Path, out string why2) ? "" : why2;
    }

    internal static void Restore(int selectedIndex, string selectedKey, string customPath,
                                 bool enabled, int volumePercent)
    {
        IsEnabled = enabled;
        CustomPath = customPath ?? "";
        SelectedKey = selectedKey ?? "";
        VolumePercent = NormalizeVolume(volumePercent);

        // 它先按文件名身份找回真实位置，找不回才回落「无」——
        SelectedIndex = selectedIndex;

        if (NormalizeSelection()) PersistSelection();
    }

    internal static int NormalizeVolume(int v)
    {
        int best = VolumeOptions[0];
        int bestDiff = int.MaxValue;
        foreach (int opt in VolumeOptions)
        {
            int d = Math.Abs(opt - v);
            if (d < bestDiff) { bestDiff = d; best = opt; }
        }
        return best;
    }

    internal static int VolumeIndex
    {
        get
        {
            int i = Array.IndexOf(VolumeOptions, VolumePercent);
            if (i >= 0) return i;
            int d = Array.IndexOf(VolumeOptions, DefaultVolumePercent);
            return d >= 0 ? d : VolumeOptions.Length / 2;
        }
    }
}
