namespace LitematicaViewer.Previewer.Diagnostics;

// 半透明图元压在某个底色上的混合结果，与 GL 那套 SRC_ALPHA / ONE_MINUS_SRC_ALPHA 是同一个算式。
//
// 画面校验为什么需要它：半透明图元（轴线、展台光环）是真的改了像素的，而按颜色反推的那套分类
// 只认识背景色与六个面色，多出来的这些会被算成「认不出的颜色」。认不出的像素本来有 1% 的预算，
// 够盖住几条线——但那意味着那条断言什么时候开始失效取决于画了多少东西，而不是取决于画得对不对。
// 所以每一个半透明图元都要把自己的混合色加进候选表。
//
// 抽成一处而不是各写一份：着色器里的字面量和这里的常量必须一致，而两边一旦分叉，
// 症状是「候选色认不出真实像素」——那看起来像渲染错了，实际是校验的表错了。
internal static class PixelBlend
{
    internal static (float R, float G, float B) Over(
        (float R, float G, float B) color,
        (float R, float G, float B) background,
        float alpha)
    {
        return (
            alpha * color.R + (1f - alpha) * background.R,
            alpha * color.G + (1f - alpha) * background.G,
            alpha * color.B + (1f - alpha) * background.B);
    }
}
