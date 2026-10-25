using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Services;

public sealed record RenderReadiness(SynthKind Synth, string Status, string Detail, bool CanAttemptRender, bool IsVerified = false);

public static class RenderReadinessService
{
    private const string Disclaimer = "此为配置检查，未完成实际加载验证。";

    public static void ValidateSettings(IEnumerable<PluginConfiguration> configurations)
    {
        foreach (var configuration in configurations)
        {
            if (!Enum.IsDefined(configuration.Backend)) throw new ArgumentException("未知的渲染方式，请重新选择。");
            if (configuration.Backend == RenderBackend.LogicPatch && configuration.Synth != SynthKind.LogicPatch)
                throw new ArgumentException("Logic Patch 后端只能用于 .patch 包。");
            if (configuration.Backend == RenderBackend.Unconfigured) continue;
            if (configuration.TimeoutSeconds is < 5 or > 3600) throw new ArgumentException("超时须为 5 到 3600 秒。");
            if (configuration.Backend == RenderBackend.VitalCli && configuration.Synth != SynthKind.Vital)
                throw new ArgumentException("Vital 原生方式只能用于 Vital。");
            // Incomplete or temporarily offline renderers are valid saved settings. Readiness
            // checks each synth before execution, without blocking another synth's settings.
        }
    }

    public static IReadOnlyList<RenderReadiness> Evaluate(AppSettings settings, PluginPlatform? platform = null, PluginArchitecture? architecture = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Enum.GetValues<SynthKind>().Where(synth => synth != SynthKind.Unknown)
            .Select(synth => Evaluate(settings.Plugins.FirstOrDefault(plugin => plugin.Synth == synth) ??
                new PluginConfiguration { Synth = synth }, settings.DiscoveredPlugins, settings.Template, platform, architecture)).ToList();
    }

    public static RenderReadiness Evaluate(PluginConfiguration configuration, IReadOnlyList<DiscoveredPlugin> discovered,
        PluginPlatform? platform = null, PluginArchitecture? architecture = null) =>
        Evaluate(configuration, discovered, null, platform, architecture);

    private static RenderReadiness Evaluate(PluginConfiguration configuration, IReadOnlyList<DiscoveredPlugin> discovered, PreviewTemplate? template,
        PluginPlatform? platform, PluginArchitecture? architecture)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(discovered);
        RenderReadiness Result(string status, string detail, bool canAttempt = false) =>
            new(configuration.Synth, status, detail + " " + Disclaimer, canAttempt);
        PluginPlatform target = platform ?? PluginSelectionService.CurrentPlatform;
        bool mac = target == PluginPlatform.MacOS;

        if (!Enum.IsDefined(configuration.Synth) || configuration.Synth == SynthKind.Unknown)
            return Result("未识别", "尚不能确认目标音源，不能依据文件名启动渲染。");
        if (configuration.Synth == SynthKind.LogicPatch || configuration.Backend == RenderBackend.LogicPatch)
        {
            if (configuration.Synth != SynthKind.LogicPatch || configuration.Backend is not (RenderBackend.Unconfigured or RenderBackend.LogicPatch))
                return Result("格式未适配", "Logic Patch 仅支持 Logic Patch AU 链后端。");
            if (!mac) return Result("平台不兼容", "Logic .patch 音频生成需要 macOS 13 或更高版本。");
            if (configuration.Backend == RenderBackend.Unconfigured || !File.Exists(configuration.ExecutablePath) || !File.Exists(configuration.PluginPath))
                return Result("环境未准备", "请点击“准备 Logic Patch 环境”，需要 Python 3.10+ 和 Xcode Command Line Tools。");
            return Result("AU 链可尝试", "逐个 .patch 核对所需 AU 及保存状态；不保证等同 Logic 完整通道条。" +
                (configuration.AllowLogicPatchApproximation ? " 已允许省略未解释的通道元数据，结果须试听确认。" : " 未解释的通道设置将拒绝渲染，可明确勾选 AU 链近似。"), true);
        }
        var matching = discovered.Where(plugin => configuration.Backend == RenderBackend.VitalCli && plugin.Format == PluginBinaryFormat.Standalone
            ? SamePath(plugin.FilePath, configuration.ExecutablePath) || SamePath(plugin.BinaryPath, configuration.ExecutablePath)
            : SamePath(plugin.FilePath, configuration.PluginPath) || SamePath(plugin.BinaryPath, configuration.PluginPath));
        // A Windows VST3 bundle can expose several architecture binaries at the same selectable path.
        var selected = matching.FirstOrDefault(plugin => plugin.Synth == configuration.Synth &&
            PluginSelectionService.IsCompatible(plugin, target, architecture)) ?? matching.FirstOrDefault();
        var candidates = discovered.Where(plugin => plugin.Synth == configuration.Synth && plugin.Platform == target).ToList();
        if (selected is not null && selected.Platform != target)
            return Result("平台不兼容", "已关联其他操作系统的音源文件，请扫描并关联当前平台版本。");
        if (PluginSelectionService.IsBundledX86Host(configuration.ExecutablePath) && configuration.Backend != RenderBackend.Unconfigured)
        {
            if (configuration.Backend != RenderBackend.ExternalWorker || configuration.Synth != SynthKind.Sylenth1)
                return Result("宿主不匹配", "所选程序是内置 32 位 Sylenth1 宿主。DawDreamer 的渲染程序须选择 Python，不能选择插件 DLL 或此宿主；32 位 Sylenth1 请重新关联对应版本。");
            if (selected is null || selected.Synth != SynthKind.Sylenth1 || selected.Architecture != PluginArchitecture.X86 || selected.Format != PluginBinaryFormat.Vst2)
                return Result("宿主不匹配", "内置宿主只适用于已扫描确认的 Windows 32 位 Sylenth1 VST2，请关联对应版本。");
        }
        if (configuration.Backend == RenderBackend.Unconfigured)
        {
            bool selectedX86 = selected?.Synth == configuration.Synth && selected.Architecture == PluginArchitecture.X86;
            bool onlyX86 = candidates.Any(plugin => plugin.Architecture == PluginArchitecture.X86) &&
                !candidates.Any(plugin => plugin.Architecture == PluginArchitecture.X64);
            if (!mac && (selectedX86 || onlyX86))
                return Result("需要32位宿主", "已识别到 32 位音源。DawDreamer 桥接不能加载它；Sylenth1 VST2 可在包含独立 x86 宿主的版本中重新扫描并关联，其他插件需配置兼容的外部宿主。不要求必须寻找 64 位音源包。");
            return Result("未选择生成方式", candidates.Count > 0 || !string.IsNullOrWhiteSpace(configuration.PluginPath)
                ? "已发现或关联音源文件，但尚未配置可运行的渲染宿主；扫描识别不等于能够生成音频。"
                : "尚未发现已支持的音源文件，也未配置渲染宿主；请先检查共享插件目录的扫描结果。");
        }
        if (!Enum.IsDefined(configuration.Backend)) return Result("设置无效", "未知的渲染后端，请重新选择。");
        if (configuration.Backend == RenderBackend.VitalCli && configuration.Synth != SynthKind.Vital)
            return Result("格式未适配", "Vital 原生渲染后端只能处理 Vital，不能用来加载其他音源。");
        if (configuration.Backend == RenderBackend.Sushi)
        {
            if (target != PluginPlatform.Windows || (architecture ?? PluginSelectionService.CurrentArchitecture) != PluginArchitecture.X64)
                return Result("平台不兼容", "当前 Sushi 生成方式仅适用于 Windows x64；其他平台请保留原有渲染方式。");
            if (configuration.Synth != SynthKind.Serum1)
                return Result("格式未适配", "当前 Sushi 方式仅适配 Serum 1 x64 VST2 FXP；其他音源请保留原有渲染方式。");
            if (!PluginSelectionService.IsSushiExecutable(configuration.ExecutablePath))
                return Result("宿主不匹配", "Sushi 的渲染程序须选择自备的 sushi.exe，不能选择 Python、音源 DLL 或其他宿主。");
            if (!File.Exists(configuration.ExecutablePath))
                return Result("程序缺失", "找不到自备的 Sushi 程序，请检查已配置的程序路径。");
            if (!PluginSelectionService.IsSushiHostAvailable(configuration.ExecutablePath))
                return Result("宿主不完整", "Sushi 需要 x64 sushi.exe 及同目录的 x64 sndfile.dll，请检查自备渲染组件是否完整。");
            if (selected is null || selected.Synth != configuration.Synth)
                return Result("插件未确认", "所选插件尚未由扫描元数据确认为 Serum 1，请先扫描并关联对应版本。");
            if (selected.Format != PluginBinaryFormat.Vst2 || !Path.GetExtension(configuration.PluginPath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                return Result("格式未适配", "Serum 1 的 Sushi 方式需要 VST2 乐器 DLL，不能使用 VST3 或其他格式。");
            if (!File.Exists(configuration.PluginPath) || !File.Exists(selected.BinaryPath))
                return Result("插件缺失", "已关联的 Serum 1 插件文件不存在，请检查安装目录并重新扫描。");
            if (selected.Architecture != PluginArchitecture.X64 || !PluginSelectionService.IsWindowsX64Binary(selected.BinaryPath) ||
                !PluginSelectionService.IsWindowsX64Binary(configuration.PluginPath))
                return Result("位数不兼容", "当前 Sushi 方式需要 Windows x64 Serum 1 VST2，不能加载 32 位或其他架构的插件。");
        }
        if (configuration.Backend == RenderBackend.DawDreamer)
        {
            if (DawDreamerRuntimeService.IsManagedExecutablePath(configuration.ExecutablePath))
            {
                if (!DawDreamerRuntimeService.TryGetRuntime(configuration.ExecutablePath, out var runtime))
                    return Result("环境未就绪", "本机生成环境缺少准备完成记录或必要文件，请在预览生成页点击“一键准备生成环境”。");
                var cpu = architecture ?? PluginSelectionService.CurrentArchitecture;
                string runtimePlatform = mac ? cpu == PluginArchitecture.Arm64 ? "osx-arm64" : "osx-x64" : "win-x64";
                if (runtime!.Platform != runtimePlatform || !mac && cpu != PluginArchitecture.X64 ||
                    mac && cpu is not (PluginArchitecture.X64 or PluginArchitecture.Arm64))
                    return Result("位数不兼容", "已选择其他平台或架构的本机生成环境，请在本机重新准备。");
                if (configuration.Synth == SynthKind.Serum2 && !runtime.SupportsSerum2)
                    return Result("依赖未齐", "本机生成环境尚未包含 Serum 2 所需组件，请重新运行一键准备生成环境。");
            }
            if (new[] { ".dll", ".vst", ".vst3", ".component" }.Contains(Path.GetExtension(configuration.ExecutablePath), StringComparer.OrdinalIgnoreCase))
                return Result("程序类型错误", "DawDreamer 的渲染程序须选择 Python 可执行文件；音源 DLL 或插件包应放在“已关联插件”，两者不能混用。");
            bool serum2 = configuration.Synth == SynthKind.Serum2;
            if (configuration.Synth is not (SynthKind.Serum1 or SynthKind.Serum2 or SynthKind.Sylenth1))
                return Result("格式未适配", "当前 DawDreamer 桥接适配 Serum 1 / Sylenth1 VST2 FXP 和 Serum 2 VST3 .SerumPreset。Spire 原生格式尚待适配，可另行配置已验证的外部工作进程。");
            string expectedExtension = serum2 ? ".vst3" : mac ? ".vst" : ".dll";
            PluginBinaryFormat expectedFormat = serum2 ? PluginBinaryFormat.Vst3 : PluginBinaryFormat.Vst2;
            if (string.IsNullOrWhiteSpace(configuration.PluginPath)) return Result("插件缺失", serum2 ? "尚未关联 Serum 2 VST3，请先扫描并选择插件。" : mac ? "尚未关联 VST2 .vst 插件包，请先扫描并选择插件。" : "尚未关联 VST2 乐器 DLL，请先扫描并选择插件。");
            if (selected is not null && selected.Format != expectedFormat || !Path.GetExtension(configuration.PluginPath).Equals(expectedExtension, StringComparison.OrdinalIgnoreCase))
                return Result("格式未适配", serum2 ? "Serum 2 原生预制需要 VST3 插件，不能交给旧 VST2 FXP 后端。" : mac ? "当前 FXP 桥接需要 macOS VST2 .vst 插件包。" : "当前 FXP 桥接需要 VST2 乐器 DLL。");
            if (!Directory.Exists(configuration.PluginPath) && !File.Exists(configuration.PluginPath))
                return Result("插件缺失", "已关联的插件文件或包不存在，请检查安装目录并重新扫描。");
            if (selected is null || selected.Synth != configuration.Synth)
                return Result("插件未确认", "所选文件尚未由扫描元数据确认为目标音源；请重新扫描，不能仅凭路径或文件名确认兼容性。");
            if (!mac && selected.Architecture == PluginArchitecture.X86)
                return Result("位数不兼容", "所选插件是 32 位，当前 DawDreamer 桥接仅支持 x64。可配置兼容 32 位插件的独立外部宿主或桥接，不要求更换 64 位音源包。");
            if (!PluginSelectionService.IsCompatible(selected, target, architecture))
                return Result("位数不兼容", mac ? "所选插件与当前 macOS 进程架构不一致；Python、DawDreamer 和插件必须使用同一 arm64 / x64 架构，或插件提供 Universal 二进制。" : "所选插件尚未确认为 Windows x64 架构；不能将其交给当前 x64 DawDreamer 桥接。");
            if (!File.Exists(selected.BinaryPath)) return Result("插件缺失", "已关联的插件包缺少实际二进制文件，请重新扫描。");
        }
        if (string.IsNullOrWhiteSpace(configuration.ExecutablePath) || !File.Exists(configuration.ExecutablePath))
            return Result("程序缺失", configuration.Backend == RenderBackend.DawDreamer
                ? "尚未准备可用的生成环境。请在预览生成页点击“一键准备生成环境”；首次需要联网，完成后会自动填入本机私有环境，无需单独安装 Python。也可继续使用自行选择的兼容 Python。"
                : "未找到配置的渲染程序，请检查程序位置。");
        if (mac && Path.GetExtension(configuration.ExecutablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return Result("平台不兼容", "macOS 不能直接运行 Windows .exe，请配置本平台渲染程序或 Vital.app 内的 Contents/MacOS 可执行文件。");
        if (string.IsNullOrWhiteSpace(configuration.Version)) return Result("版本缺失", "尚未填写或识别实际音源版本，不能建立可靠的渲染记录。");
        if (configuration.TimeoutSeconds is < 5 or > 3600) return Result("设置无效", "进程超时必须为 5 到 3600 秒，请修正渲染设置。");
        if (configuration.Backend == RenderBackend.VitalCli)
        {
            if (selected is not null && !PluginSelectionService.IsCompatible(selected, target, architecture))
                return Result("位数不兼容", "所选 Vital 独立程序与当前进程架构不兼容，请选择相应架构或 Universal 版本。");
            if (template is not null && !template.QuickPreview)
            {
                try { template.Validate(); }
                catch (ArgumentException error) { return Result("模板不匹配", error.Message); }
                if (template.HoldSeconds < 1 || Math.Abs(template.HoldSeconds - Math.Round(template.HoldSeconds)) > 0.000001 ||
                    template.Velocity != 89 || Math.Abs(template.TailSeconds - template.HoldSeconds * 0.3) > 0.000001)
                    return Result("模板不匹配", "Vital 原生渲染要求整数秒按键时长、力度 89、尾音为按键时长的 30%；请采用 Vital 原生参数。");
            }
            return Result("可尝试", template?.QuickPreview == true
                ? "快速预览将从真实原生输出按设定时长截取每音试听，慢起音会在上限内延长采集；Vital 实际按整数秒演奏，力度固定 89。仍须人工确认音色与资源。"
                : "Vital 独立程序和版本已配置；原生 CLI 不报告目标预制加载成功，输出仍须人工确认音色与资源。", true);
        }
        if (configuration.Backend == RenderBackend.Sushi)
            return Result("可尝试", "Serum 1 x64 VST2 与原生 Sushi 宿主已配置，无需 Python；生成前会建立无预制的默认声音参照，结果仍须试听确认音色与外部资源。", true);
        if (configuration.Backend == RenderBackend.DawDreamer)
            return Result("可尝试", configuration.Synth == SynthKind.Serum2
                ? "Serum 2 VST3、匹配架构的 Python 与插件版本已配置。安装 requirements-serum2.txt 后可加载 .SerumPreset，采用当前插件实际状态格式并回读核对；旧 FXP 请先在 Serum 2 中导入并另存。尚需实际预制与外部资源验收。"
                : mac
                ? "macOS VST2 插件包、Python 程序及版本已配置。Python、DawDreamer 和插件必须使用同一 arm64 / x64 架构；依赖安装、FXP 加载和宿主初始化仍须预检及单个预制试渲染，不代表已验证可用。"
                : "x64 VST2 插件、Python 程序及版本已配置。尚未检查 x64 Python 3.11 / 3.12、DawDreamer 0.8.3、NumPy 1.26.4 的依赖安装和宿主初始化是否成功；须先完成独立宿主预检及单个预制试渲染，不代表已验证可用。", true);
        return Result("可尝试", selected?.Architecture == PluginArchitecture.X86
            ? "外部渲染器程序已配置，允许交给兼容 32 位插件的独立宿主或桥接；是否支持此音源和预制格式仍须实际验证。"
            : "外部渲染器程序已配置；目标插件位数、宿主能力和预制格式支持由该外部宿主负责，尚未验证其实际可用性。", true);
    }

    private static bool SamePath(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return ApplicationPaths.SamePath(left, right); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return false; }
    }
}
