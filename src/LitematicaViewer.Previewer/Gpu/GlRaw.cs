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

    // 半透明 pass 的深度语义。LEQUAL 与 LESS 只差「相等时也过」：共面的宿主面和
    // 水壁深度相同，LESS 会让胜负落到插值噪声上逐像素抖（z-fight），LEQUAL 让
    // 后画的水稳定地叠上去——vanilla 的半透明 pass 用的也是 LEQUAL。
    internal const int GL_LEQUAL = 0x0203;

    // 半透明绘制需要的两件事：开混合、把因子设成 srcAlpha / oneMinusSrcAlpha。
    //
    // 抽成一个入口是因为用它的人不止一个（轴线、展台光环），而「谁先建谁顺手设一下」是隐式依赖：
    // 顺序一变，后来者拿到的就是默认的 (ONE, ZERO)，画出来只是「颜色偏实」——
    // 不报错、不崩，看着像颜色选错了。两次调用同样的值是幂等的，谁调都不亏。
    internal static bool EnableAlphaBlend(GlInterface gl)
    {
        gl.Enable(GL_BLEND);
        return BlendFuncSeparate(gl, GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA, GL_ZERO, GL_ONE);
    }

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

    // void glUniform1f(GLint location, GLfloat v0)
    //
    // 两 pass 的开关（uOpaquePass）每帧要设两次，入口指针缓存一份：GetProcAddress
    // 每帧查表没必要。指针与上下文无关（同进程同驱动），缓存跨上下文安全。
    internal static void Uniform1f(GlInterface gl, int location, float value)
    {
        if (_uniform1f is null)
        {
            IntPtr entry = gl.GetProcAddress("glUniform1f");
            _uniform1f = Marshal.GetDelegateForFunctionPointer<Uniform1fDelegate>(entry);
        }

        _uniform1f(location, value);
    }

    // GL 的 C 原型在 Windows 上就是 stdcall（x64 上只有一种调用约定，这一栏写什么都一样，
    // 但签名本身必须与原型逐项对上）。这与项目里已有那条 glReadPixels 的写法保持一致。
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void BlendFuncSeparateDelegate(int sourceRgb, int destinationRgb, int sourceAlpha, int destinationAlpha);

    private static Uniform1fDelegate? _uniform1f;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Uniform1fDelegate(int location, float value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void LineWidthDelegate(float width);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetErrorDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ReadPixelsDelegate(int x, int y, int width, int height, int format, int type, IntPtr pixels);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr GetStringDelegate(int name);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetFloatvDelegate(int pname, IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void TexParameterfDelegate(int target, int pname, float param);

    // const GLubyte* glGetString(GLenum name)
    //
    // 查扩展名用的。返回值指向 GL 内部的字符串，只读不写、不保存指针，
    // 立刻 marshal 成托管串——GL 什么时候改写它不受我们管。
    internal static string? GetString(GlInterface gl, int name)
    {
        IntPtr entry = gl.GetProcAddress("glGetString");
        if (entry == IntPtr.Zero)
        {
            return null;
        }

        IntPtr result = Marshal.GetDelegateForFunctionPointer<GetStringDelegate>(entry)(name);
        return result == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(result);
    }

    // void glGetFloatv(GLenum pname, GLfloat* data)
    //
    // 查标量上限（这里是 MAX_TEXTURE_MAX_ANISOTROPY）。单值查询，data 指向一个 float。
    internal static float? GetFloat(GlInterface gl, int pname)
    {
        IntPtr entry = gl.GetProcAddress("glGetFloatv");
        if (entry == IntPtr.Zero)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal(sizeof(float));
        try
        {
            Marshal.GetDelegateForFunctionPointer<GetFloatvDelegate>(entry)(pname, buffer);
            return Marshal.PtrToStructure<float>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // void glTexParameterf(GLenum target, GLenum pname, GLfloat param)
    //
    // 各向异性过滤的档位是 float 参数，GlInterface 自带的 TexParameteri 签名对不上
    // （发 int 过去在 x64 上参数寄存器宽度一致侥幸能用，但不赌——正确原型就一行）。
    internal static bool TexParameterf(GlInterface gl, int target, int pname, float param)
    {
        IntPtr entry = gl.GetProcAddress("glTexParameterf");
        if (entry == IntPtr.Zero)
        {
            return false;
        }

        Marshal.GetDelegateForFunctionPointer<TexParameterfDelegate>(entry)(target, pname, param);
        return true;
    }
}
