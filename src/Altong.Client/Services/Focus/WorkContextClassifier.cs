namespace Altong.Client.Services.Focus;

/// <summary>AutoDND가 활성 창을 보는 세 가지 관점.</summary>
public enum WorkCategory
{
    /// <summary>개발·문서·디자인처럼 명백한 작업. 집중 진입 진행도를 쌓는다.</summary>
    Work,

    /// <summary>탐색기·메신저·브라우저·모르는 앱. 진행도를 쌓지도, 깎지도 않는다.</summary>
    Neutral,

    /// <summary>게임·영상 서비스처럼 명백한 비작업. 일정 시간 머물면 진입을 취소한다.</summary>
    Leisure,
}

/// <summary>
/// 프로세스 이름만으로 활성 창을 <see cref="WorkCategory"/>로 나누는 1단계 규칙 분류기.
/// 브라우저·PDF 뷰어처럼 내용에 따라 달라지는 앱은 아직 판별하지 않고 Neutral로 둔다(Step 2에서 KLUE로 판별 예정).
/// 창 제목은 보지 않는다.
/// </summary>
public static class WorkContextClassifier
{
    private static readonly Dictionary<string, WorkCategory> Rules = Build(
        (WorkCategory.Work,
        [
            // 개발
            "Code", "Code - Insiders", "devenv", "Antigravity", "Antigravity IDE", "Cursor", "Windsurf",
            "idea64", "pycharm64", "rider64", "webstorm64", "clion64", "goland64", "datagrip64", "studio64",
            "WindowsTerminal", "powershell", "pwsh", "cmd", "sublime_text", "notepad++",
            "GitHubDesktop", "Postman", "Ssms", "MATLAB", "rstudio",
            // 문서·기획
            "WINWORD", "EXCEL", "POWERPNT", "ONENOTE", "Hwp", "HwpViewer", "Notion", "Obsidian",
            // 디자인·전문 도구
            "Figma", "Photoshop", "Illustrator", "InDesign", "AfterFX", "Adobe Premiere Pro",
            "acad", "AutoCAD", "blender",
        ]),
        (WorkCategory.Neutral,
        [
            // 시스템
            "explorer", "SystemSettings", "ApplicationFrameHost", "Taskmgr",
            // 메신저·회의 (업무 연락 겸용)
            "KakaoTalk", "Discord", "slack", "ms-teams", "Teams", "Zoom",
            // 작업 중 틀어 두는 음악
            "Spotify",
            // 내용에 따라 다른 앱 (Step 2에서 KLUE 판별 대상)
            "chrome", "msedge", "whale", "firefox", "brave", "opera",
            "Acrobat", "AcroRd32", "PDFXEdit",
            // 알통 자체
            "Altong.Client",
        ]),
        (WorkCategory.Leisure,
        [
            "League of Legends", "LeagueClient", "LeagueClientUx", "VALORANT", "VALORANT-Win64-Shipping",
            "RiotClientServices", "Overwatch", "Steam", "steamwebhelper", "Battle.net", "EpicGamesLauncher",
            "MapleStory", "Netflix",
        ]));

    /// <summary>
    /// 프로세스 이름(예: <c>Code.exe</c>, <c>chrome</c>)을 분류한다. 대소문자와 <c>.exe</c> 유무는 무시하고,
    /// 이름이 비었거나 규칙에 없는 앱은 Neutral이다.
    /// </summary>
    public static WorkCategory Classify(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return WorkCategory.Neutral;
        return Rules.TryGetValue(Normalize(processName), out var category) ? category : WorkCategory.Neutral;
    }

    private static string Normalize(string processName)
    {
        string name = processName.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static Dictionary<string, WorkCategory> Build(params (WorkCategory Category, string[] Names)[] groups)
    {
        var rules = new Dictionary<string, WorkCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var (category, names) in groups)
            foreach (string name in names)
                rules.Add(name, category);
        return rules;
    }
}
