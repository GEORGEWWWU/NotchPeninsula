using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using NAudio.Wave;

namespace NotchPeninsula;

/// <summary>
/// 🎵 通知提示音的**配置与校验**层（纯逻辑，不碰播放）。
///
/// 关键约定：
/// 1. **提示音列表是「动态扫描目录」得来的，不是硬编码数组**。
///    扫描 exe 同级 <c>data\sound\*.wav</c>，按文件名派生显示名；丢一个新 wav 进去
///    就自动出现在设置里的下拉菜单中，**不需要改一行代码**。
///    可选地放一个 <c>data\sound\sound.json</c> 覆盖显示名（见 <see cref="LoadDisplayNames"/>）。
/// 2. **音频一律只记路径、不复制文件**。内置音也是引用 <c>data\sound\</c> 下的原文件。
/// 3. **文件丢失 / 超限 / 格式不支持 → 一律回落到「无」**，并顺手清掉注册表里的失效记忆。
///    绝不因为一个音频文件缺失就让岛体启动失败或弹异常。
/// 4. **入队前先做完整校验**，宁可静默不响，也不把几十小时的音频塞进播放队列。
/// 5. 时长上限 <see cref="MaxDurationSec"/> + 文件体积上限 <see cref="MaxFileSizeBytes"/>
///    是防止「选到演唱会全场录音」把内存 / 设备占死的双保险。
/// </summary>
internal static class ToastSoundConfig
{
    /// <summary>「无」选项的显示名（默认，不播放任何声音）。</summary>
    internal const string NoneName = "无";

    /// <summary>「加一个自定义文件…」选项的显示名。</summary>
    internal const string BrowseName = "浏览音频…";

    /// <summary>内置提示音所在目录（相对 exe），也是「拖个 wav 进去就能用」的那个目录。</summary>
    internal const string SoundFolderName = "data\\sound";

    /// <summary>音频文件体积上限：2 MB。远超任何提示音，但能挡住长录音。</summary>
    internal const long MaxFileSizeBytes = 2 * 1024 * 1024;

    /// <summary>播放时长上限：5 秒。读文件头即可判定，不用真正解码。</summary>
    internal const double MaxDurationSec = 5.0;

    /// <summary>动态列表里最多收录多少个内置音（防止有人往目录里倒进一整个音效库）。</summary>
    private const int MaxBuiltinCount = 40;

    /// <summary>用户可选的播放音量档（百分比），10% 步进，从 0%（静音）到 100%。</summary>
    internal static readonly int[] VolumeOptions =
        [0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100];

    /// <summary>
    /// 提示音出厂默认音量（百分比）。**唯一真源** —— 字段初值、注册表读不到时的兜底、
    /// 「重置」按钮、<see cref="VolumeIndex"/> 的异常兜底全部由它派生。
    /// ⚠️ 曾经这里散成三个字面量：字段写 70、注册表兜底写 70、「重置」写 `VolumeOptions[1]`（其实是 10），
    ///    导致重置后音量与出厂默认不一致。改默认值只改这一处。
    /// </summary>
    internal const int DefaultVolumePercent = 10;

    /// <summary>文件对话框的格式筛选器（支持系统解码的常见音频格式）。</summary>
    internal const string FileFilter =
        "音频文件 (*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac;*.aiff;*.aif)\0*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac;*.aiff;*.aif\0" +
        "WAV 波形 (*.wav)\0*.wav\0" +
        "所有文件 (*.*)\0*.*\0";

    // ------------------------------------------------------------------
    //  动态内置列表
    // ------------------------------------------------------------------

    /// <summary>内置音的一条：绝对路径 + 界面显示名（文件名去掉扩展名，或 sound.json 的覆盖）。</summary>
    internal readonly record struct BuiltinEntry(string Path, string Label)
    {
        /// <summary>不含扩展名的文件名，用作自定义显示名的查找键。</summary>
        public string Stem => System.IO.Path.GetFileNameWithoutExtension(Path);

        /// <summary>
        /// 含扩展名的文件名，作为这条内置音的**稳定身份**（见 <see cref="SelectedKey"/>）。
        /// ⚠️ 不要拿它在列表里的位置当身份 —— 目录是动态扫描的，增删一个 wav 会让后面所有项平移。
        /// </summary>
        public string FileName => System.IO.Path.GetFileName(Path);
    }

    private static BuiltinEntry[] _builtins = [];
    private static string _folder = "";

    /// <summary>当前扫描到的内置音（只读视图）。</summary>
    internal static IReadOnlyList<BuiltinEntry> Builtins => _builtins;

    /// <summary>本次扫描时实际命中的目录绝对路径（供「打开目录」等使用）。</summary>
    internal static string SoundFolderPath => _folder;

    /// <summary>下拉框第一个选项：「无」。</summary>
    internal const int BuiltinOffset = 1;

    /// <summary>用户在自定义音频（浏览文件）选项里的索引（内置项之后的第一项）。</summary>
    internal static int CustomIndex => BuiltinOffset + _builtins.Length;

    /// <summary>下拉框选项总数（无 + 内置 + 浏览）。</summary>
    internal static int OptionCount => BuiltinOffset + _builtins.Length + 1;

    // ---------------- 运行期状态（由 ConsoleWindow 与 Program.LoadSettings 共同维护）----------------

    /// <summary>当前选中的下拉框索引。0 = 无。</summary>
    internal static int SelectedIndex = 0;

    /// <summary>
    /// 当前选中的**内置音文件名**（含扩展名），是内置项的**稳定身份**。
    ///
    /// ⚠️ 不能只用 <see cref="SelectedIndex"/>：下拉列表是每次展开都重扫目录得来的，
    ///    用户删掉 / 新增一个 wav 会让后面所有项整体平移，
    ///    于是「位置索引」会把用户的选择悄悄换成**另一个音**（删掉 Airdrop.wav
    ///    会让原本的「QQ（桌面端）」变成「QQ（经典咳咳）」）。
    ///    所以启动恢复与列表刷新后都用本字段回查真实位置（见 <see cref="NormalizeSelection"/>）。
    ///    选中自定义文件 / 「无」时为空串。
    /// </summary>
    internal static string SelectedKey = "";

    /// <summary>用户自定义音频文件路径（仅 <see cref="SelectedIndex"/> == <see cref="CustomIndex"/> 时有意义）。</summary>
    internal static string CustomPath = "";

    /// <summary>提示音总开关（默认关闭 —— 需求明确要求「默认关闭」，想听的人自己去开）。</summary>
    internal static bool IsEnabled = false;

    /// <summary>
    /// 「提示音设置」行（通知卡第 4 行）是否可用：父开关「消息提示音」必须打开。
    /// 与 tab 3「自动隐藏关掉 → 两个子开关整行置灰」的父子约定同源，
    /// **置灰 / 禁止指针 / 禁止点击三处判据都必须读它**。
    /// </summary>
    internal static bool IsRowEnabled => IsEnabled;

    /// <summary>
    /// 「音量 / 试听 / 重置」三个控件是否可用：父开关打开 **且** 已选中一个具体音源。
    /// ⚠️ 必须同时判 <see cref="SelectedIndex"/> 是否**在合法范围内** —— 曾经只写 `SelectedIndex > 0`，
    ///    目录里的 wav 被删掉几个后索引越界，界面上框里显示「无」、这几个按钮却还亮着。
    /// </summary>
    internal static bool IsSourceReady => IsEnabled && SelectedIndex > 0 && SelectedIndex < OptionCount;

    /// <summary>播放音量百分比（默认 <see cref="DefaultVolumePercent"/> = 10%）。</summary>
    internal static int VolumePercent = DefaultVolumePercent;

    /// <summary>
    /// 重新扫描 <c>data\sound\</c> 目录，重建内置列表。
    ///
    /// 扫描位置按优先级找（第一个存在的目录胜出）：
    /// 1. exe 同级 <c>data\sound</c>（发布后的正常位置）
    /// 2. 仓库根 <c>data\sound</c>（开发期直接从仓库运行 / 单文件发布的兜底）
    ///
    /// **必须在 <see cref="Restore"/> 之前调用一次**，否则下拉框里只有「无」和「浏览」。
    /// 之后想看到新丢进去的文件，再调一次即可（幂等，纯 IO 扫描，不缓存解码数据）。
    /// </summary>
    internal static void RefreshBuiltins()
    {
        var list = new List<BuiltinEntry>();
        _folder = "";

        foreach (string dir in CandidateFolders())
        {
            try
            {
                if (!Directory.Exists(dir)) continue;

                // 目录里可能会有 history / 备份之类的内容，这里只看顶层的 wav：
                // 内置音强制 wav —— 它是唯一无需解码器探测就能拿到时长、且启动零依赖的格式。
                string[] files = Directory.GetFiles(dir, "*.wav", SearchOption.TopDirectoryOnly);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);

                foreach (string f in files)
                {
                    if (list.Count >= MaxBuiltinCount) break;
                    string stem = Path.GetFileNameWithoutExtension(f);
                    if (stem.Length == 0) continue;
                    list.Add(new BuiltinEntry(f, stem));
                }

                _folder = dir;
                break; // 找到第一个能用的目录就不再往下找
            }
            catch
            {
                // 权限 / IO 异常：换下一个候选目录，绝不因为一个目录读不了就崩
            }
        }

        // 应用 sound.json 里的显示名覆盖（可选文件，缺失即跳过）
        ApplyDisplayNames(_folder, list);
        _builtins = list.ToArray();

        // 🔻 列表一变就必须把当前选择「按文件名身份」重新对齐一次：
        //    否则删掉 / 新增一个 wav 之后，位置索引会把用户的选择悄悄换成另一个音，
        //    或者越界到「框里显示无、音量/试听按钮却还亮着」。
        //    放在这里而不是各调用点，是为了让所有「重建列表」的路径都自动自愈。
        if (NormalizeSelection()) PersistSelection();
    }

    /// <summary>按优先级列出候选目录（exe 同级 → 仓库根兜底）。</summary>
    private static IEnumerable<string> CandidateFolders()
    {
        string baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "data", "sound");
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "data", "sound"));
    }

    /// <summary>
    /// 可选的显示名覆盖：<c>data\sound\sound.json</c>，形如 <c>{ "Tri-Tone": "三全音", "QQ": "QQ 消息" }</c>。
    /// 键是不带扩展名的文件名，值是界面上显示的文本。解析失败一律静默忽略（用文件名当显示名）。
    /// </summary>
    private static void ApplyDisplayNames(string folder, List<BuiltinEntry> list)
    {
        if (string.IsNullOrEmpty(folder) || list.Count == 0) return;
        string json = Path.Combine(folder, "sound.json");
        if (!File.Exists(json)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
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

    /// <summary>
    /// 构建下拉框的显示文本数组（「无」+ 内置名 + 「浏览音频…」）。
    /// 每次 Render 时按需生成 —— 选项数很小，且能保证与磁盘现状完全一致。
    /// </summary>
    internal static string[] BuildOptionLabels()
    {
        var labels = new string[OptionCount];
        labels[0] = NoneName;
        for (int i = 0; i < _builtins.Length; i++)
            labels[BuiltinOffset + i] = _builtins[i].Label;
        labels[CustomIndex] = BrowseName;
        return labels;
    }

    /// <summary>下拉框上显示的当前选中文本。自定义项若文件已失效则显示「自定义（不可用）」。</summary>
    internal static string CurrentDisplayText()
    {
        if (SelectedIndex <= 0 || SelectedIndex >= OptionCount) return NoneName;
        if (SelectedIndex == CustomIndex)
            return IsUsableFile(CustomPath, out _) ? ShortenPath(CustomPath) : "自定义（不可用）";
        return _builtins[SelectedIndex - BuiltinOffset].Label;
    }

    /// <summary>
    /// 解析出「现在该播哪个文件」。返回 null 表示不该播（选了无 / 文件失效）。
    /// </summary>
    internal static string? ResolveCurrentPath()
    {
        if (SelectedIndex <= 0 || SelectedIndex >= OptionCount) return null;

        if (SelectedIndex == CustomIndex)
            return IsUsableFile(CustomPath, out _) ? CustomPath : null;

        string path = _builtins[SelectedIndex - BuiltinOffset].Path;
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// 校验一个音频文件是否可用作提示音。
    ///
    /// 三层检查，任何一层不过就返回 false 并给出可展示的原因：
    /// 1. 存在性 —— 路径为空 / 文件不存在。
    /// 2. 体积 —— 超过 <see cref="MaxFileSizeBytes"/>（防止选到整张专辑）。
    /// 3. 时长 —— 用 NAudio 只读文件头拿 <c>TotalTime</c>，超过 <see cref="MaxDurationSec"/> 拒绝。
    ///    读取器构造本身就会对不支持的格式抛异常，顺带完成了格式校验。
    /// </summary>
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

    /// <summary>与播放器共用的读取器选择逻辑（只按扩展名分发，构造失败即视为不支持）。</summary>
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

    /// <summary>界面展示用：把长路径压成「…\父目录\文件名.wav」。</summary>
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

    // ------------------------------------------------------------------
    //  选择项的「身份对齐」——索引会漂，文件名不会
    // ------------------------------------------------------------------

    /// <summary>按文件名（含扩展名、忽略大小写）在当前内置列表里查位置；找不到返回 -1。</summary>
    private static int IndexOfKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return -1;
        for (int i = 0; i < _builtins.Length; i++)
            if (string.Equals(_builtins[i].FileName, key, StringComparison.OrdinalIgnoreCase))
                return BuiltinOffset + i;
        return -1;
    }

    /// <summary>把当前选择写回注册表（索引 / 文件名身份 / 自定义路径三者一起）。</summary>
    internal static void PersistSelection()
    {
        Program.SaveSetting("ToastSoundIndex", SelectedIndex);
        Program.SaveSetting("ToastSoundKey", SelectedKey);
        Program.SaveSetting("ToastSoundPath", CustomPath);
    }

    /// <summary>
    /// 把内存里的选择**按文件名身份**重新对齐一次，顺手自愈越界 / 失效。
    ///
    /// 必须在**每次重建内置列表之后**调用（<see cref="RefreshBuiltins"/> 之后、
    /// 展开下拉之前，以及启动恢复时）。返回 true 表示内存态被改过，调用方应写回注册表。
    ///
    /// 判定顺序（**顺序本身就是正确性的一部分**）：
    /// 1. **有身份就先按身份找回** —— 必须排在「按索引解释」之前。列表缩短后，一个内置项的
    ///    位置索引可能正好撞上 <see cref="CustomIndex"/>（实测：删到只剩 2 个内置音时
    ///    `CustomIndex` 也等于 3），先按索引解释会把它误判成自定义项、直接回落「无」。
    /// 2. 记了身份却找不到 → 该音源真被删了，回落「无」，**不许拿索引去顶替**。
    /// 3. 选「无」→ 清掉残留的身份与自定义路径。
    /// 4. 自定义项 → 文件还在就保持，失效则回落「无」。
    /// 5. 只有索引、没有身份（老版本注册表）→ 用索引做一次性迁移。
    /// </summary>
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
            // ⑤ 老版本注册表只有索引：迁移一次，之后就以文件名身份为准
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

    /// <summary>
    /// 当前选中项「为什么播不了」的可展示原因（选中「无」或一切正常时返回空串）。
    /// ⚠️ 不要拿 <see cref="CustomPath"/> 去套内置项 —— 内置项的路径不在那个字段里，
    ///    否则内置音文件缺失时会把「尚未选择音频文件」这种错文案糊到用户脸上。
    /// </summary>
    internal static string DescribeUnavailable()
    {
        if (SelectedIndex <= 0 || SelectedIndex >= OptionCount) return "";

        if (SelectedIndex == CustomIndex)
            return IsUsableFile(CustomPath, out string why) ? "" : why;

        string path = _builtins[SelectedIndex - BuiltinOffset].Path;
        if (!File.Exists(path)) return "内置提示音文件缺失（重新放入 data\\sound 目录即可）";
        return IsUsableFile(path, out string why2) ? "" : why2;
    }

    /// <summary>
    /// 启动时恢复 + 自愈。与 <c>FontConfig.Restore</c> 同一套语义：
    /// 记忆的文件已失效就回落，并把内存态与注册表一起修正，保证三者永远一致。
    /// 由 <see cref="Program.LoadSettings"/> 在创建窗口前调用（**先 RefreshBuiltins 再调本方法**）。
    ///
    /// <paramref name="selectedKey"/> 是内置音的文件名身份，优先于 <paramref name="selectedIndex"/>；
    /// 老版本注册表没有这个值，传空串即可（会按位置索引做一次性迁移）。
    /// </summary>
    internal static void Restore(int selectedIndex, string selectedKey, string customPath,
                                 bool enabled, int volumePercent)
    {
        IsEnabled = enabled;
        CustomPath = customPath ?? "";
        SelectedKey = selectedKey ?? "";
        VolumePercent = NormalizeVolume(volumePercent);

        // 注册表里的索引**不在这里夹**，原样交给 NormalizeSelection：
        // 它先按文件名身份找回真实位置，找不回才回落「无」——
        // 若先夹到 0，就会把「索引越界但文件名还在」这种可救的情况一起丢掉。
        SelectedIndex = selectedIndex;

        if (NormalizeSelection()) PersistSelection();
    }

    /// <summary>把任意整数音量吸附到最近的合法档位（注册表被手改时自愈）。</summary>
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

    /// <summary>音量档位在 <see cref="VolumeOptions"/> 里的索引（用于下拉菜单选中态）。
    /// 找不到时回落到 <see cref="DefaultVolumePercent"/>，而不是数组第 1 项（0% 会静音，不能当兜底）。</summary>
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
