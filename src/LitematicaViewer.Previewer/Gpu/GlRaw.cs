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

    // 超采样 FBO 用的 target/attachment/格式组。GlConsts 只收合成后端用得上的，
    // 这一整组它都没有，数值来自 GLES 3.0 规范。
    internal const int GL_RENDERBUFFER = 0x8D41;
    internal const int GL_COLOR_ATTACHMENT0 = 0x8CE0;
    internal const int GL_DEPTH_ATTACHMENT = 0x8D00;
    internal const int GL_DEPTH_COMPONENT24 = 0x81A6;
    internal const int GL_READ_FRAMEBUFFER = 0x8CA8;
    internal const int GL_DRAW_FRAMEBUFFER = 0x8CA9;
    internal const int GL_FRAMEBUFFER_COMPLETE = 0x8CD5;

    private static Uniform1fDelegate? _uniform1f;

    // ---- 离屏 FBO（超采样）。GLES 3.0 core 的整组入口，ANGLE 上必有；
    // 入口缺失时 SupersampleTarget.Create 返回 null，调用方退回直画。 ----

    // 入口只在 EnsureSize/Resolve/Dispose 里用，频率远低于每帧一次 GetProcAddress 的代价可忽略，
    // 但和 Uniform1f 同一处理：缓存委托，省掉重复查表。指针跨上下文安全。
    private static GenDeleteDelegate? _genFramebuffers;
    private static BindDelegate? _bindFramebuffer;
    private static FramebufferTextureDelegate? _framebufferTexture2D;
    private static GenDeleteDelegate? _genRenderbuffers;
    private static BindDelegate? _bindRenderbuffer;
    private static RenderbufferStorageDelegate? _renderbufferStorage;
    private static FramebufferRenderbufferDelegate? _framebufferRenderbuffer;
    private static GenDeleteDelegate? _deleteFramebuffers;
    private static GenDeleteDelegate? _deleteRenderbuffers;
    private static CheckFramebufferStatusDelegate? _checkFramebufferStatus;
    private static BlitFramebufferDelegate? _blitFramebuffer;

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
    internal static bool BlendFuncSeparate(GlInterface gl, int sourceRgb, int destinationRgb, int sourceAlpha,
        int destinationAlpha)
    {
        var entry = gl.GetProcAddress("glBlendFuncSeparate");
        if (entry == IntPtr.Zero) return false;

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
        var entry = gl.GetProcAddress("glLineWidth");
        if (entry == IntPtr.Zero) return false;

        Marshal.GetDelegateForFunctionPointer<LineWidthDelegate>(entry)(width);
        return true;
    }

    // GLenum glGetError(void)
    //
    // GL 的错误是状态而不是返回值：出了错先攒着，直到有人读走。所以「读一次」等于
    // 给之前一整段调用兜了一次底，代价是它给不出出错的位置。
    internal static int GetError(GlInterface gl)
    {
        var entry = gl.GetProcAddress("glGetError");
        if (entry == IntPtr.Zero) return NoError;

        return Marshal.GetDelegateForFunctionPointer<GetErrorDelegate>(entry)();
    }

    // void glReadPixels(GLint x, GLint y, GLsizei w, GLsizei h, GLenum format, GLenum type, void* pixels)
    //
    // 整张 framebuffer 读回来是强制 GPU 同步的，只在 DEBUG 校验里用，且总次数有预算。
    // 宽高用物理像素，与视口一致。
    internal static byte[]? ReadPixels(GlInterface gl, int x, int y, int width, int height)
    {
        var entry = gl.GetProcAddress("glReadPixels");
        if (entry == IntPtr.Zero) return null;

        var readPixels = Marshal.GetDelegateForFunctionPointer<ReadPixelsDelegate>(entry);
        var byteCount = width * height * 4;
        var buffer = Marshal.AllocHGlobal(byteCount);
        try
        {
            readPixels(x, y, width, height, GlConsts.GL_RGBA, GlConsts.GL_UNSIGNED_BYTE, buffer);

            var pixels = new byte[byteCount];
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
            var entry = gl.GetProcAddress("glUniform1f");
            _uniform1f = Marshal.GetDelegateForFunctionPointer<Uniform1fDelegate>(entry);
        }

        _uniform1f(location, value);
    }

    // const GLubyte* glGetString(GLenum name)
    //
    // 查扩展名用的。返回值指向 GL 内部的字符串，只读不写、不保存指针，
    // 立刻 marshal 成托管串——GL 什么时候改写它不受我们管。
    internal static string? GetString(GlInterface gl, int name)
    {
        var entry = gl.GetProcAddress("glGetString");
        if (entry == IntPtr.Zero) return null;

        var result = Marshal.GetDelegateForFunctionPointer<GetStringDelegate>(entry)(name);
        return result == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(result);
    }

    // void glGetFloatv(GLenum pname, GLfloat* data)
    //
    // 查标量上限（这里是 MAX_TEXTURE_MAX_ANISOTROPY）。单值查询，data 指向一个 float。
    internal static float? GetFloat(GlInterface gl, int pname)
    {
        var entry = gl.GetProcAddress("glGetFloatv");
        if (entry == IntPtr.Zero) return null;

        var buffer = Marshal.AllocHGlobal(sizeof(float));
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
        var entry = gl.GetProcAddress("glTexParameterf");
        if (entry == IntPtr.Zero) return false;

        Marshal.GetDelegateForFunctionPointer<TexParameterfDelegate>(entry)(target, pname, param);
        return true;
    }

    // void glGetTexParameteriv(GLenum target, GLenum pname, GLint* params)
    //
    // 「设置有没有落到真正生效的那个 pname 上」只能靠回读证。过滤相关的 pname 值
    // 全是猜不得的（GlConsts 的常量值与本机驱动的实际行为是两码事），所以设完必须能查。
    internal static int? GetTexParameteriv(GlInterface gl, int target, int pname)
    {
        var entry = gl.GetProcAddress("glGetTexParameteriv");
        if (entry == IntPtr.Zero) return null;

        var buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.GetDelegateForFunctionPointer<GetTexParameterivDelegate>(entry)(target, pname, buffer);
            return Marshal.ReadInt32(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // void glGetIntegerv(GLenum pname, GLint* data)
    //
    // 查上下文状态用。绑定量、当前纹理单元、当前 sampler 这类「谁在生效」的问题
    // 只能靠它回答——filter 设了却不影响采样时，第一嫌疑就是 unit 上挂了 sampler。
    internal static int? GetInteger(GlInterface gl, int pname)
    {
        var entry = gl.GetProcAddress("glGetIntegerv");
        if (entry == IntPtr.Zero) return null;

        var buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.GetDelegateForFunctionPointer<GetIntegervDelegate>(entry)(pname, buffer);
            return Marshal.ReadInt32(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // void glBindSampler(GLuint unit, GLuint sampler)：sampler 为 0 表示解绑，
    // 让该单元回到「用纹理自己的 filter」的语义。
    internal static bool BindSampler(GlInterface gl, int unit, int sampler)
    {
        var entry = gl.GetProcAddress("glBindSampler");
        if (entry == IntPtr.Zero) return false;

        Marshal.GetDelegateForFunctionPointer<BindSamplerDelegate>(entry)(unit, sampler);
        return true;
    }

    private static T Cache<T>(GlInterface gl, string name, ref T? cache) where T : Delegate
    {
        if (cache is null)
        {
            var entry = gl.GetProcAddress(name);
            cache = entry == IntPtr.Zero
                ? throw new InvalidOperationException($"[PREVIEWER][gl.raw] 入口缺失 {name}")
                : Marshal.GetDelegateForFunctionPointer<T>(entry);
        }

        return cache;
    }

    // 这组入口没有「可缺省」的余量：要 FBO 就得全有。Create 里逐个探测太啰嗦，
    // 直接约定——任何一个缺失就抛，SupersampleTarget.Create 捕获后整体降级直画。
    internal static void GenFramebuffers(GlInterface gl, int count, IntPtr ids)
    {
        Cache(gl, "glGenFramebuffers", ref _genFramebuffers)(count, ids);
    }

    internal static void BindFramebuffer(GlInterface gl, int target, int handle)
    {
        Cache(gl, "glBindFramebuffer", ref _bindFramebuffer)(target, handle);
    }

    internal static void FramebufferTexture2D(GlInterface gl, int target, int attachment, int textureTarget,
        int texture, int level)
    {
        Cache(gl, "glFramebufferTexture2D", ref _framebufferTexture2D)(target, attachment, textureTarget, texture,
            level);
    }

    internal static void GenRenderbuffers(GlInterface gl, int count, IntPtr ids)
    {
        Cache(gl, "glGenRenderbuffers", ref _genRenderbuffers)(count, ids);
    }

    internal static void BindRenderbuffer(GlInterface gl, int target, int handle)
    {
        Cache(gl, "glBindRenderbuffer", ref _bindRenderbuffer)(target, handle);
    }

    internal static void RenderbufferStorage(GlInterface gl, int target, int internalFormat, int width, int height)
    {
        Cache(gl, "glRenderbufferStorage", ref _renderbufferStorage)(target, internalFormat, width, height);
    }

    internal static void FramebufferRenderbuffer(GlInterface gl, int target, int attachment, int renderbufferTarget,
        int renderbuffer)
    {
        Cache(gl, "glFramebufferRenderbuffer", ref _framebufferRenderbuffer)(target, attachment, renderbufferTarget,
            renderbuffer);
    }

    internal static void DeleteFramebuffers(GlInterface gl, int count, IntPtr ids)
    {
        Cache(gl, "glDeleteFramebuffers", ref _deleteFramebuffers)(count, ids);
    }

    internal static void DeleteRenderbuffers(GlInterface gl, int count, IntPtr ids)
    {
        Cache(gl, "glDeleteRenderbuffers", ref _deleteRenderbuffers)(count, ids);
    }

    internal static int CheckFramebufferStatus(GlInterface gl, int target)
    {
        return Cache(gl, "glCheckFramebufferStatus", ref _checkFramebufferStatus)(target);
    }

    internal static void BlitFramebuffer(GlInterface gl,
        int srcX0, int srcY0, int srcX1, int srcY1,
        int dstX0, int dstY0, int dstX1, int dstY1,
        int mask, int filter)
    {
        Cache(gl, "glBlitFramebuffer", ref _blitFramebuffer)(
            srcX0, srcY0, srcX1, srcY1, dstX0, dstY0, dstX1, dstY1, mask, filter);
    }

    // GL 的 C 原型在 Windows 上就是 stdcall（x64 上只有一种调用约定，这一栏写什么都一样，
    // 但签名本身必须与原型逐项对上）。这与项目里已有那条 glReadPixels 的写法保持一致。
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void BlendFuncSeparateDelegate(int sourceRgb, int destinationRgb, int sourceAlpha,
        int destinationAlpha);

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

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetTexParameterivDelegate(int target, int pname, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetIntegervDelegate(int pname, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void BindSamplerDelegate(int unit, int sampler);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GenDeleteDelegate(int count, IntPtr ids);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void BindDelegate(int target, int handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void FramebufferTextureDelegate(int target, int attachment, int textureTarget, int texture,
        int level);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void RenderbufferStorageDelegate(int target, int internalFormat, int width, int height);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void FramebufferRenderbufferDelegate(int target, int attachment, int renderbufferTarget,
        int renderbuffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CheckFramebufferStatusDelegate(int target);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void BlitFramebufferDelegate(
        int srcX0, int srcY0, int srcX1, int srcY1,
        int dstX0, int dstY0, int dstX1, int dstY1,
        int mask, int filter);
}
