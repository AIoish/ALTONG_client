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
    public void SenderWithoutBody_IsMedia()
    {
        var text = LiveKakaoWindowOperator.MapOcrLines([("알통 팀방", 10), ("팀원A", 36)]);

        Assert.AreEqual(new KakaoNotificationText("팀원A", "알통 팀방", LiveKakaoWindowOperator.MediaBody), text);
    }

    [TestMethod]
    public void OnlyTopLine_IsMessageWithoutReadableBody()
    {
        var text = LiveKakaoWindowOperator.MapOcrLines([("팀원A", 10)]);

        Assert.AreEqual(new KakaoNotificationText("팀원A", "팀원A", LiveKakaoWindowOperator.MissingBody), text);
    }

    [TestMethod]
    public void LinesAreOrderedByPosition_AndBlankLinesIgnored()
    {
        var text = LiveKakaoWindowOperator.MapOcrLines([("본문", 56), (" ", 40), ("팀원A", 36), ("알통 팀방", 10)]);

        Assert.AreEqual(new KakaoNotificationText("팀원A", "알통 팀방", "본문"), text);
        Assert.IsNull(LiveKakaoWindowOperator.MapOcrLines([]));
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

    private static Bitmap NewCard()
    {
        var card = new Bitmap(620, 140, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(card);
        g.Clear(Color.White);
        g.FillRectangle(Brushes.Black, 130, 75, 80, 20);
        return card;
    }
}
