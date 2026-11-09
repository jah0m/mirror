using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace Mirror;

/// <summary>
/// Interface text in Simplified Chinese, English and Japanese.
/// XAML reads the strings as dynamic resources named "S.&lt;key&gt;", so a language change shows at once.
/// </summary>
public static class Loc
{
    public const string Auto = "auto";
    public static readonly string[] Languages = ["zh", "en", "ja"];

    /// <summary>Each language's own name, shown in the language list.</summary>
    public static readonly IReadOnlyDictionary<string, string> NativeNames = new Dictionary<string, string>
    {
        ["zh"] = "简体中文",
        ["en"] = "English",
        ["ja"] = "日本語",
    };

    private static readonly Dictionary<string, (string Zh, string En, string Ja)> Table = new()
    {
        // Top bar and bottom controls
        ["Mirror"] = ("镜像", "Mirror", "ミラー"),
        ["Tip.Mirror"] = ("镜像（M）", "Mirror (M)", "ミラー（M）"),
        ["Background"] = ("背景", "Background", "背景"),
        ["Tip.Background"] = ("背景（B 开关）", "Background (B to toggle)", "背景（B で切り替え）"),
        ["Tip.BackgroundLocked"] = ("录像中不能更换背景", "The background can't change while recording", "録画中は背景を変更できません"),
        ["Tip.Grid"] = ("九宫格（G）", "Grid (G)", "グリッド（G）"),
        ["Tip.Timer"] = ("定时（T）", "Timer (T)", "タイマー（T）"),
        ["Tip.SwitchCamera"] = ("切换摄像头（C）", "Switch camera (C)", "カメラを切り替え（C）"),
        ["Tip.Fullscreen"] = ("全屏（F11）", "Full screen (F11)", "全画面（F11）"),
        ["Tip.Settings"] = ("设置（S）", "Settings (S)", "設定（S）"),
        ["Tip.Gallery"] = ("打开相册", "Open gallery", "アルバムを開く"),
        ["Tip.Shutter"] = ("拍摄（空格）", "Capture (Space)", "撮影（スペース）"),
        ["Photo"] = ("照片", "Photo", "写真"),
        ["Video"] = ("录像", "Video", "動画"),
        ["Seconds"] = ("{0}秒", "{0}s", "{0}秒"),

        // Camera status
        ["OpeningCamera"] = ("正在打开摄像头…", "Opening the camera…", "カメラを起動しています…"),
        ["Retry"] = ("重试", "Retry", "再試行"),
        ["OpenPrivacy"] = ("打开隐私设置", "Open privacy settings", "プライバシー設定を開く"),
        ["NoCamera"] = ("没有找到摄像头。接好摄像头后点重试。",
            "No camera found. Connect a camera, then select Retry.",
            "カメラが見つかりません。カメラを接続してから「再試行」を押してください。"),
        ["NoPermission"] = ("Windows 没有允许桌面应用使用摄像头。\n请在隐私设置中打开“让桌面应用访问你的相机”，然后点重试。",
            "Windows isn't letting desktop apps use the camera.\nIn privacy settings, turn on \"Let desktop apps access your camera\", then select Retry.",
            "Windows でデスクトップ アプリのカメラ使用が許可されていません。\nプライバシー設定で「デスクトップ アプリにカメラへのアクセスを許可する」をオンにしてから、「再試行」を押してください。"),
        ["CameraBusy"] = ("打不开摄像头，它可能正被其他应用占用。\n{0}",
            "Couldn't open the camera. Another app may be using it.\n{0}",
            "カメラを開けません。他のアプリが使用している可能性があります。\n{0}"),
        ["CameraLost"] = ("摄像头断开了。\n{0}", "The camera was disconnected.\n{0}", "カメラの接続が切れました。\n{0}"),
        ["CameraDisconnected"] = ("摄像头已断开", "The camera is disconnected", "カメラが切断されています"),
        ["PreviewStartFailed"] = ("无法启动画面（{0}）", "Couldn't start the preview ({0})", "プレビューを開始できません（{0}）"),

        // Settings panel
        ["Camera"] = ("摄像头", "Camera", "カメラ"),
        ["Resolution"] = ("分辨率", "Resolution", "解像度"),
        ["Saving"] = ("保存", "Saving", "保存"),
        ["SaveMirrored"] = ("照片和视频也按镜像保存", "Save photos and videos mirrored", "写真と動画もミラーで保存"),
        ["RecordAudio"] = ("录像时录制声音", "Record sound with videos", "録画時に音声も録音"),
        ["Brightness"] = ("亮度", "Brightness", "明るさ"),
        ["Contrast"] = ("对比度", "Contrast", "コントラスト"),
        ["ResetPicture"] = ("恢复默认画面", "Reset picture", "画質をリセット"),
        ["OpenFolder"] = ("打开保存位置", "Open save folder", "保存先を開く"),
        ["Language"] = ("语言", "Language", "言語"),
        ["LanguageAuto"] = ("跟随系统", "Same as Windows", "Windows に合わせる"),

        // Photos and recording
        ["PhotoSaveFailed"] = ("照片保存失败：{0}", "Couldn't save the photo: {0}", "写真を保存できませんでした：{0}"),
        ["NoMic"] = ("麦克风不可用，这次录像不带声音", "Microphone unavailable, so this video has no sound", "マイクを使用できないため、この動画は音声なしで録画します"),
        ["RecordStartFailed"] = ("无法开始录像：{0}", "Couldn't start recording: {0}", "録画を開始できません：{0}"),
        ["AudioMergeFailed"] = ("声音合成失败，视频已保存，但没有声音", "Couldn't add the sound; the video was saved without it", "音声を結合できませんでした。動画は音声なしで保存されています"),
        ["RecordStopFailed"] = ("结束录像时出错：{0}", "Problem finishing the recording: {0}", "録画の終了時にエラーが発生しました：{0}"),
        ["EncoderCreateFailed"] = ("无法创建视频编码器：{0}", "Couldn't create the video encoder: {0}", "動画エンコーダーを作成できません：{0}"),
        ["EncodeFailed"] = ("视频编码失败：{0}", "Video encoding failed: {0}", "動画のエンコードに失敗しました：{0}"),
        ["NoFrames"] = ("没有录到画面", "No video was captured", "映像が記録されませんでした"),

        // Backgrounds
        ["BlurStrength"] = ("模糊强度", "Blur strength", "ぼかしの強さ"),
        ["None"] = ("无", "None", "なし"),
        ["Blur"] = ("模糊", "Blur", "ぼかし"),
        ["Upload"] = ("上传图片/视频", "Add picture or video", "画像・動画を追加"),
        ["DeleteBackground"] = ("删除这张背景", "Delete this background", "この背景を削除"),
        ["DeleteFailed"] = ("删除失败：{0}", "Couldn't delete: {0}", "削除できませんでした：{0}"),
        ["ChooseBackground"] = ("选择背景图片或视频", "Choose a background picture or video", "背景の画像または動画を選択"),
        ["ImageFilter"] = ("图片和视频|{0}|所有文件|*.*", "Pictures and videos|{0}|All files|*.*", "画像と動画|{0}|すべてのファイル|*.*"),
        ["ImageUnusable"] = ("无法使用这个文件：{0}", "Can't use this file: {0}", "このファイルは使用できません：{0}"),
        ["LoadingBackground"] = ("正在加载背景效果…", "Loading the background effect…", "背景効果を読み込んでいます…"),
        ["NoGpu"] = ("没有可用的显卡加速，背景效果会占用较多 CPU", "No graphics acceleration available, so background effects will use more CPU", "GPU アクセラレーションを使えないため、背景効果の CPU 負荷が高くなります"),
        ["BackgroundFailed"] = ("背景效果无法启动：{0}", "Couldn't start the background effect: {0}", "背景効果を開始できません：{0}"),
        ["BackgroundMissing"] = ("这张背景图片已不存在", "This background picture no longer exists", "この背景画像は見つかりません"),
        ["MyPicture"] = ("我的图片 {0}", "My picture {0}", "マイ画像 {0}"),
        ["MyAnimation"] = ("我的动态 {0}", "My animation {0}", "マイ動画 {0}"),
        ["bg.aurora"] = ("流光", "Aurora", "オーロラ"),
        ["bg.office"] = ("书房", "Study", "書斎"),
        ["bg.japan"] = ("和室", "Japanese room", "和室"),
        ["bg.cafe"] = ("咖啡馆", "Café", "カフェ"),
        ["bg.city"] = ("城市黄昏", "City at dusk", "夕暮れの街"),
        ["bg.nature"] = ("湖畔", "Lakeside", "湖畔"),
        ["bg.studio"] = ("影棚", "Studio", "スタジオ"),
    };

    /// <summary>The language in use: "zh", "en" or "ja".</summary>
    public static string Current { get; private set; } = "en";

    public static event Action? Changed;

    public static string T(string key)
    {
        if (!Table.TryGetValue(key, out var text))
            return key;
        return Current switch { "zh" => text.Zh, "ja" => text.Ja, _ => text.En };
    }

    public static string T(string key, object? arg) => string.Format(T(key), arg);

    /// <summary>Picks the language from a setting ("auto" follows Windows) and refreshes all text.</summary>
    public static void Apply(string? setting)
    {
        Current = setting is "zh" or "en" or "ja" ? setting : FromWindows();

        var resources = Application.Current.Resources;
        foreach (var key in Table.Keys)
            resources["S." + key] = T(key);

        foreach (Window window in Application.Current.Windows)
            ApplyFont(window);
        Changed?.Invoke();
    }

    private static string FromWindows()
    {
        string name = CultureInfo.CurrentUICulture.Name;
        if (name.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            return "ja";
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return "zh";
        return "en";
    }

    /// <summary>
    /// Chinese and Japanese draw shared characters differently, so each language gets its own CJK font.
    /// </summary>
    public static void ApplyFont(FrameworkElement element)
    {
        (string tag, string font) = Current switch
        {
            "zh" => ("zh-CN", "Segoe UI Variable Text, Microsoft YaHei UI"),
            "ja" => ("ja-JP", "Segoe UI Variable Text, Yu Gothic UI"),
            _ => ("en-US", "Segoe UI Variable Text, Microsoft YaHei UI, Yu Gothic UI"),
        };
        element.Language = XmlLanguage.GetLanguage(tag);
        if (element is System.Windows.Controls.Control control)
            control.FontFamily = new FontFamily(font);
    }
}
