namespace Altong.Client.Services;

/// <summary>세로 시간축에 블록을 겹치지 않게 놓는다. 활동 일지 하루 흐름과 캘린더 그날 시간표가 함께 쓴다.</summary>
public static class TimelineLayout
{
    /// <summary>
    /// 블록마다 원래 시각 위치(Top)와 높이를 받아 실제로 놓을 위치를 돌려준다.
    /// 먼저 겹치는 블록을 아래로 밀고, 그래서 바닥을 넘으면 끝에서부터 위로 끌어올린다.
    /// 짧은 집중이 저녁에 몰려 있어도 블록과 글자가 서로 겹치지 않는다.
    /// </summary>
    public static double[] Stack(IReadOnlyList<(double Top, double Height)> blocks, double canvasHeight, double gap = 3)
    {
        var tops = new double[blocks.Count];
        double previousBottom = double.NegativeInfinity;
        for (int i = 0; i < blocks.Count; i++)
        {
            tops[i] = Math.Max(Math.Max(0, blocks[i].Top), previousBottom + gap);
            previousBottom = tops[i] + blocks[i].Height;
        }
        double limit = canvasHeight;
        for (int i = blocks.Count - 1; i >= 0; i--)
        {
            tops[i] = Math.Max(0, Math.Min(tops[i], limit - blocks[i].Height));
            limit = tops[i] - gap;
        }
        return tops;
    }
}
