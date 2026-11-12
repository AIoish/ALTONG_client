using System.Drawing;
using System.Drawing.Imaging;
using Altong.Client.Services.Notifications;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

/// <summary>
/// 카카오톡 팝업 OCR 결과를 방 이름·발신자·본문으로 나누는 규칙과 OCR 전 이미지 손질을 검증한다.
/// 좌표는 논리 px(96 DPI)이고, 실제 팝업에서 방 이름 줄은 약 10, 발신자 줄은 약 36, 본문 줄은 약 56에 있다.
/// </summary>
[TestClass]
public sealed class KakaoOcrLayoutTests
{
    [TestMethod]
    public void GroupChat_SplitsRoomSenderAndBodyByRow()
    {
        var text = LiveKakaoWindowOperator.MapOcrLines([("알통 팀방", 10), ("팀원A", 36), ("내일 회의 3시", 56)]);

        Assert.AreEqual(new KakaoNotificationText("팀원A", "알통 팀방", "내일 회의 3시"), text);
    }

    [TestMethod]
    public void OneToOneChat_WrappedBodyStaysInBody()
    {
        // 1:1도 맨 위 이름과 발신자 줄이 따로 있다. 본문이 두 줄로 접혀도 발신자가 밀리지 않아야 한다.
        var text = LiveKakaoWindowOperator.MapOcrLines([("팀원A", 10), ("팀원A", 36), ("첫 줄", 56), ("둘째 줄", 72)]);

        Assert.AreEqual(new KakaoNotificationText("팀원A", "팀원A", "첫 줄\n둘째 줄"), text);
    }

    [TestMethod]
    public void MissingSenderRow_KeepsBodyAndFallsBackToRoomName()
    {
        // 기존 기준(58)에서는 위쪽 좌표 57인 본문이 발신자로 들어가고 본문이 '(이모티콘 또는 미디어)'가 됐다.
        var text = LiveKakaoWindowOperator.MapOcrLines([("알통 팀방", 10), ("심플이 최고", 57)]);

        Assert.AreEqual(new KakaoNotificationText("알통 팀방", "알통 팀방", "심플이 최고"), text);
    }

    [TestMethod]
    public void SenderWithoutBody_IsUnreadBody()
    {
        var text = LiveKakaoWindowOperator.MapOcrLines([("알통 팀방", 10), ("팀원A", 36)]);

        Assert.AreEqual(new KakaoNotificationText("팀원A", "알통 팀방", KakaoNotificationText.UnreadBody), text);
    }

    [TestMethod]
    public void OnlyTopLine_IsMessageWithoutReadableBody()
    {
        var text = LiveKakaoWindowOperator.MapOcrLines([("팀원A", 10)]);

        Assert.AreEqual(new KakaoNotificationText("팀원A", "팀원A", KakaoNotificationText.MissingBody), text);
    }

    [TestMethod]
    public void LinesAreOrderedByPosition_AndBlankLinesIgnored()
    {
        var text = LiveKakaoWindowOperator.MapOcrLines([("본문", 56), (" ", 40), ("팀원A", 36), ("알통 팀방", 10)]);

        Assert.AreEqual(new KakaoNotificationText("팀원A", "알통 팀방", "본문"), text);
        Assert.IsNull(LiveKakaoWindowOperator.MapOcrLines([]));
    }

    [TestMethod]
    public void FindTextRows_FindsRoomSenderAndBodyRows_IgnoringBorderAndToolbarIcons()
    {
        using var card = NewUpscaledCard();
        Ink(card, 0, 0, 1550, 2);      // 위 테두리
        Ink(card, 0, 0, 2, 120);       // 왼쪽 테두리
        Ink(card, 1330, 40, 50, 60);   // 벨 아이콘
        Ink(card, 1460, 45, 45, 55);   // 닫기 아이콘
        Ink(card, 80, 60, 210, 45);    // 방 이름
        Ink(card, 322, 180, 400, 62);  // 발신자
        Ink(card, 320, 283, 330, 52);  // 본문

        var rows = LiveKakaoWindowOperator.FindTextRows(card, UpscaledPxPerLogical, out Color background);

        Assert.AreEqual(Color.White.ToArgb(), background.ToArgb());
        CollectionAssert.AreEqual(
            new[] { new Rectangle(80, 60, 210, 45), new Rectangle(322, 180, 400, 62), new Rectangle(320, 283, 330, 52) },
            rows);
    }

    [TestMethod]
    public void FindTextRows_SenderRowReachingBodyBoundary_DoesNotPullBodyIntoSenderRow()
    {
        // 실제 사례(11/06): 발신자 "22 컴공 박민준"의 글자 아래쪽이 본문 경계(논리 48 = 240px)까지 내려와 있었다.
        // 예전에는 본문 영역을 경계부터 훑다가 이 자투리를 본문 줄의 시작으로 잡아 본문 "ㅋㅋㅋ"의 위쪽을 47.6으로
        // 계산했고, 본문이 발신자 칸에 붙어 "22 컴공 박민준 ㅋㅋㅋ" / "(이모티콘 또는 미디어)"가 됐다.
        using var card = NewUpscaledCard();
        Ink(card, 322, 180, 400, 62);  // 발신자: y 180~241
        Ink(card, 327, 294, 273, 30);  // 본문 "ㅋㅋㅋ"

        var rows = LiveKakaoWindowOperator.FindTextRows(card, UpscaledPxPerLogical, out _);
        var text = LiveKakaoWindowOperator.MapOcrLines(
            rows.Select((row, i) => (i == 0 ? "22 컴공 박민준" : "ㅋㅋㅋ", row.Top / UpscaledPxPerLogical)).ToList());

        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual(new KakaoNotificationText("22 컴공 박민준", "22 컴공 박민준", "ㅋㅋㅋ"), text);
    }

    [TestMethod]
    [DataRow(34, 8, 7, DisplayName = "물음표: 몸통과 점")]
    [DataRow(7, 16, 15, DisplayName = "쌍반점: 낮은 조각 둘")]
    [DataRow(27, 2, 23, DisplayName = "넵: 받침 위에서 끊김")]
    public void FindTextRows_JoinsPiecesOfOneLine(int upperHeight, int gap, int lowerHeight)
    {
        using var card = NewUpscaledCard();
        Ink(card, 322, 180, 400, 62);
        Ink(card, 320, 288, 24, upperHeight);
        Ink(card, 322, 288 + upperHeight + gap, 20, lowerHeight);

        var rows = LiveKakaoWindowOperator.FindTextRows(card, UpscaledPxPerLogical, out _);

        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual(new Rectangle(320, 288, 24, upperHeight + gap + lowerHeight), rows[1]);
    }

    [TestMethod]
    public void FindTextRows_KeepsSeparateLinesApart()
    {
        using var card = NewUpscaledCard(height: 500);
        Ink(card, 322, 180, 400, 62);  // 발신자
        Ink(card, 320, 283, 600, 65);  // 본문 첫 줄 (아래로 내려간 글자 포함)
        Ink(card, 330, 363, 30, 12);   // 둘째 줄 맨 위의 낮은 조각 (틈 3 논리 px)
        Ink(card, 320, 440, 40, 9);    // 그 아래 줄의 말줄임표

        var rows = LiveKakaoWindowOperator.FindTextRows(card, UpscaledPxPerLogical, out _);

        CollectionAssert.AreEqual(new[] { 180, 283, 363, 440 }, rows.Select(r => r.Top).ToArray());
    }

    [TestMethod]
    public void FindTextRows_UsesCardBackground_WhenDarkThemeWasInverted()
    {
        // 다크 모드 팝업은 PrepareForOcr가 밝기를 뒤집어 배경이 밝은 회색이 된다.
        var inverted = Color.FromArgb(212, 212, 212);
        using var card = NewUpscaledCard(inverted);
        Ink(card, 322, 180, 400, 62, Color.FromArgb(20, 20, 20));
        Ink(card, 320, 283, 330, 52, Color.FromArgb(110, 110, 110));

        var rows = LiveKakaoWindowOperator.FindTextRows(card, UpscaledPxPerLogical, out Color background);

        Assert.AreEqual(inverted.ToArgb(), background.ToArgb());
        CollectionAssert.AreEqual(new[] { 180, 283 }, rows.Select(r => r.Top).ToArray());
    }

    [TestMethod]
    public void UnreadBody_IsOnlyThePlaceholdersTheAppWrites()
    {
        Assert.IsTrue(KakaoNotificationText.IsUnreadBody(KakaoNotificationText.UnreadBody));
        Assert.IsTrue(KakaoNotificationText.IsUnreadBody(KakaoNotificationText.LegacyMediaBody), "문구를 바꾸기 전에 저장된 알림도 구분한다.");
        Assert.IsTrue(KakaoNotificationText.IsUnreadBody(KakaoNotificationText.MissingBody));
        Assert.IsTrue(KakaoNotificationText.IsUnreadBody(KakaoNotificationText.ArrivedBody));
        Assert.IsTrue(KakaoNotificationText.IsUnreadBody(KakaoNotificationText.EmptyBody));
        Assert.IsFalse(KakaoNotificationText.IsUnreadBody("ㅋㅋㅋ"));
        Assert.IsFalse(KakaoNotificationText.IsUnreadBody(""), "본문이 원래 없는 알림(제목만 있는 토스트)은 해당하지 않는다.");
    }

    [TestMethod]
    public void PrepareForOcr_ErasesAvatarButKeepsTitleAndTextColumn()
    {
        const float scale = 2f;
        using var card = new Bitmap(620, 140, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(card))
        {
            g.Clear(Color.White);
            g.FillRectangle(Brushes.Black, 30, 20, 50, 15);   // 방 이름 줄 (맨 위 왼쪽)
            g.FillRectangle(Brushes.SteelBlue, 30, 55, 75, 80); // 프로필 사진
            g.FillRectangle(Brushes.Black, 130, 75, 80, 20);  // 발신자 줄 (글자 열)
        }

        LiveKakaoWindowOperator.PrepareForOcr(card, scale);

        Assert.AreEqual(Color.White.ToArgb(), card.GetPixel(60, 90).ToArgb(), "프로필 사진은 배경색으로 덮는다.");
        Assert.AreEqual(Color.Black.ToArgb(), card.GetPixel(50, 27).ToArgb(), "방 이름 줄은 남긴다.");
        Assert.AreEqual(Color.Black.ToArgb(), card.GetPixel(150, 85).ToArgb(), "글자 열은 남긴다.");
    }

    [TestMethod]
    public void ContentHash_ChangesWithTextButIgnoresToolbarIcons()
    {
        using var first = NewCard();
        using var same = NewCard();
        using var newText = NewCard();
        using var hoveredBell = NewCard();
        newText.SetPixel(200, 110, Color.Black);
        hoveredBell.SetPixel(560, 20, Color.Gray);

        ulong hash = LiveKakaoWindowOperator.ComputeContentHash(first);

        Assert.AreNotEqual(0UL, hash);
        Assert.AreEqual(hash, LiveKakaoWindowOperator.ComputeContentHash(same));
        Assert.AreNotEqual(hash, LiveKakaoWindowOperator.ComputeContentHash(newText));
        Assert.AreEqual(hash, LiveKakaoWindowOperator.ComputeContentHash(hoveredBell), "벨·닫기 아이콘 영역은 지문에서 뺀다.");
    }

    [TestMethod]
    public void KakaoIntegration_EnvironmentOverridesBuildDefault()
    {
        Assert.IsTrue(KakaoIntegration.Resolve("1"));
        Assert.IsFalse(KakaoIntegration.Resolve(" 0 "));
#if DEBUG
        Assert.IsTrue(KakaoIntegration.Resolve(null), "개발(Debug) 빌드는 기본으로 켠다.");
#else
        Assert.IsFalse(KakaoIntegration.Resolve(null), "Release 빌드는 기본으로 끈다.");
#endif
    }

    // 200% 팝업(620x161)을 OCR용으로 2.5배 키운 크기에서는 논리 px 하나가 5px이다.
    private const double UpscaledPxPerLogical = 5;

    private static Bitmap NewUpscaledCard(Color? background = null, int height = 402)
    {
        var card = new Bitmap(1550, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(card);
        g.Clear(background ?? Color.White);
        return card;
    }

    private static void Ink(Bitmap card, int left, int top, int width, int height, Color? color = null)
    {
        using var g = Graphics.FromImage(card);
        using var brush = new SolidBrush(color ?? Color.Black);
        g.FillRectangle(brush, left, top, width, height);
    }

    private static Bitmap NewCard()
    {
        var card = new Bitmap(620, 140, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(card);
        g.Clear(Color.White);
        g.FillRectangle(Brushes.Black, 130, 75, 80, 20);
        return card;
    }
}
