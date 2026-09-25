using System.Runtime.InteropServices;
using Avalonia.OpenGL;

namespace LitematicaViewer.Previewer.Gpu;

// GlInterface 是 Avalonia 从 GL 里挑出来的一份子集：它把自己后端合成用得上的那几十个入口
// 包成了强类型方法（实测 115 个），其余的一律不给。这个类放的就是「我们要、但它没包」的那几个。
//
// 唯一的手段是 GetProcAddress：上下文已经建好，函数地址问得到，只是拿回来的是裸指针，
// 得自己配一个签名对得上的委托。签名的唯一依据是 GL 的 C 原型——写错不会编译失败，
// 会在运行期把栈搅乱，所以每一条都把原型抄在方法上面。
//
// 这些入口都只在 Initialize 或 DEBUG 校验里调，不在每帧路径上：
// 每调一次都要 GetProcAddress 一次（这里不缓存，缓存就是一份要跟着上下文失效的状态）。
internal static class GlRaw
{
    // GL 的错误码：0 是 GL_NO_ERROR，一次调用最多读走一个。
    // GlConsts 里有 GL_NO_ERROR，这里只是给「读回来比一比」一个名字。
    internal const int NoError = 0;

    // GlConsts 收了 GL_DEPTH_TEST、GL_CULL_FACE，却没这两组：它按「后端自己用得上」来收，
    // 而混合与线宽都不在后端的用途里。数值来自 GL 规范，写错的表现是「什么都没变」。
    internal const int GL_BLEND = 0x0BE2;
    internal const int GL_SRC_ALPHA = 0x0302;
    internal const int GL_ONE_MINUS_SRC_ALPHA = 0x0303;
    internal const int GL_ZERO = 0x0000;
    internal const int GL_ONE = 0x0001;

    // void glBlendFuncSeparate(GLenum srcRGB, GLenum dstRGB, GLenum srcAlpha, GLenum dstAlpha)
    //
    // 用分离的那个而不是 glBlendFunc：颜色要按 src 的 alpha 混，而 alpha 通道必须原样留下。
    // 用单参数的版本时 alpha 通道也会被混成 0.6*a + (1-a)*dst，于是画过轴线的地方
    // 帧缓冲的 alpha 不再是 1——那个帧缓冲接下来要交给 Avalonia 合成，透明度漏出去的表现
    // 是「窗口上那几条线看着比周围淡」，而不是报错。
    //
    // 返回「这个上下文里到底有没有这个入口」：没有的时候这件事必须让调用方知道，
    // 因为它什么都不做，而「混合没开」在画面上只是「颜色不太对」。
    internal static bool BlendFuncSeparate(GlInterface gl, int sourceRgb, int destinationRgb, int sourceAlpha, int destinationAlpha)
    {
        IntPtr entry = gl.GetProcAddress("glBlendFuncSeparate");
        if (entry == IntPtr.Zero)
        {
            return false;
        }

        Marshal.GetDelegateForFunctionPointer<BlendFuncSeparateDelegate>(entry)(
            sourceRgb, destinationRgb, sourceAlpha, destinationAlpha);
        return true;
    }

    // void glLineWidth(GLfloat width)
    //
    // GLES 3 的 core 只保证宽度 1.0，更粗的是可选的：不支持的实现会发出 GL_INVALID_VALUE
    // 并把宽度留在原处。所以调用方得自己判断有没有生效，这里只负责发出去。
    internal static bool LineWidth(GlInterface gl, float width)
    {
        IntPtr entry = gl.GetProcAddress("glLineWidth");
        if (entry == IntPtr.Zero)
        {
            return false;
        }

        Marshal.GetDelegateForFunctionPointer<LineWidthDelegate>(entry)(width);
        return true;
    }

    // GLenum glGetError(void)
    //
    // GL 的错误是状态而不是返回值：出了错先攒着，直到有人读走。所以「读一次」等于
    // 给之前一整段调用兜了一次底，代价是它给不出出错的位置。
    internal static int GetError(GlInterface gl)
    {
        IntPtr entry = gl.GetProcAddress("glGetError");
        if (entry == IntPtr.Zero)
        {
            return NoError;
        }

        return Marshal.GetDelegateForFunctionPointer<GetErrorDelegate>(entry)();
    }

    // void glReadPixels(GLint x, GLint y, GLsizei w, GLsizei h, GLenum format, GLenum type, void* pixels)
    //
    // 整张 framebuffer 读回来是强制 GPU 同步的，只在 DEBUG 校验里用，且总次数有预算。
    // 宽高用物理像素，与视口一致。
    internal static byte[]? ReadPixels(GlInterface gl, int x, int y, int width, int height)
    {
        IntPtr entry = gl.GetProcAddress("glReadPixels");
        if (entry == IntPtr.Zero)
        {
            return null;
        }

        ReadPixelsDelegate readPixels = Marshal.GetDelegateForFunctionPointer<ReadPixelsDelegate>(entry);
        int byteCount = width * height * 4;
        IntPtr buffer = Marshal.AllocHGlobal(byteCount);
        try
        {
            readPixels(x, y, width, height, GlConsts.GL_RGBA, GlConsts.GL_UNSIGNED_BYTE, buffer);

            byte[] pixels = new byte[byteCount];
            Marshal.Copy(buffer, pixels, 0, byteCount);
            return pixels;
        }
        finally
        {
            // GL 已经把数据拷进我们这块内存了，中转缓冲可以立刻放掉。
            Marshal.FreeHGlobal(buffer);
        }
    }

    // GL 的 C 原型在 Windows 上就是 stdcall（x64 上只有一种调用约定，这一栏写什么都一样，
    // 但签名本身必须与原型逐项对上）。这与项目里已有那条 glReadPixels 的写法保持一致。
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void BlendFuncSeparateDelegate(int sourceRgb, int destinationRgb, int sourceAlpha, int destinationAlpha);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void LineWidthDelegate(float width);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetErrorDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ReadPixelsDelegate(int x, int y, int width, int height, int format, int type, IntPtr pixels);
}
