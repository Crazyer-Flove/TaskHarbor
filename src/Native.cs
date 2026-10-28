// ============================================================
//  Win32 互操作
//   · 窗口形状：Region 裁剪（面板圆角 / 球的圆形）
//   · 整体半透明：SetLayeredWindowAttributes(LWA_ALPHA)
//   · 悬浮球逐像素透明：UpdateLayeredWindow（圆边真抗锯齿）
//   · 窗口拖动 / 置顶 / 运行时设置窗口图标
//
//  ⚠ 经验一：亚克力毛玻璃会吃掉 Region（本机实测，2026-09-18）
//  ------------------------------------------------------------------
//  用 SetWindowCompositionAttribute 开亚克力毛玻璃
//  （ACCENT_ENABLE_ACRYLICBLURBEHIND / ACCENT_ENABLE_BLURBEHIND）时，
//  DWM 会把毛玻璃铺满【整个窗口矩形】，**完全无视窗口的 Region 裁剪**。
//  实测：圆形 Region + 亚克力 → 屏幕上是个带直角的灰方块，四角恒为
//  (227,227,227)，tint 的 Alpha 设多少都一样；加 WS_EX_LAYERED 也压不住。
//  → 本项目不使用毛玻璃接口。
//
//  ⚠ 经验二：Region 永远有锯齿，想要圆边平滑只能逐像素透明
//  ------------------------------------------------------------------
//  SetWindowRgn 的区域是 **1 位掩码**，只有「在里 / 在外」两种状态，
//  不存在半透明像素，所以圆形边缘一定是一格一格的台阶。
//  实测悬浮球边缘：紫色 (60,52,145) → 白色 (254,254,255) 在一个像素内突变，
//  中间没有任何过渡色。
//  → 悬浮球的稳态改用 UpdateLayeredWindow 自己画（见 PaintBall），
//    边缘才真正抗锯齿。面板仍然用 Region（WebView2 是子窗口，
//    没法参与父窗口的逐像素合成，只能用 Region 裁圆角）。
//
//  ⚠ 经验三：WS_EX_LAYERED 的两种模式不能混用
//  ------------------------------------------------------------------
//  调过 SetLayeredWindowAttributes 之后，UpdateLayeredWindow 会直接失败，
//  反之亦然。要切换必须把 WS_EX_LAYERED 样式位先摘掉再装上（见 SetLayeredMode）。
//  另外：一旦带上 WS_EX_LAYERED，就必须调过 SetLayeredWindowAttributes，
//  否则窗口完全不显示。
//
//  ⚠ 经验四：SLWA 一旦调过，ULW 就【永久】失效，而且旧位图会留下残影
//  ------------------------------------------------------------------
//  这不是「切回来就好了」那么轻：ULW 用过之后中途调一次 SLWA，
//  ULW 从此每次都返回 0；而 SLWA 只改整体 alpha，**不会清内容** —— 原来那张
//  位图会一直粘在窗口表面上。窗口一放大，DWM 把那块表面一起拉伸，
//  于是面板底色里就透出一个直径 = 球直径的淡色圆（实测圆心正好落在两个
//  窗口尺寸的正中，就是拉伸的铁证），挪窗口、RedrawWindow 都擦不掉。
//  另一面：SLWA 模式下窗口按「自己那块底」显示，ULW 一失效，球就丢掉了
//  圆形的位图，变成一个方块。
//  → 铁律：_pixelBall 为真时，一次都不能调 SetAlpha / SetLayeredWindowAttributes。
//    alpha 只能烘进位图（PaintBall 的 alpha 参数）。退出逐像素模式时，
//    必须先 SetLayeredMode(hwnd, true) 把模式重新装好，再 ClearLayered ——
//    否则那次 ULW 照样失败，球永远擦不掉。
//
//  ⚠ 经验五：音频播放（winmm waveOut）是阻塞 API，必须在后台线程调
//  ------------------------------------------------------------------
//  waveOutWrite 之后要等缓冲放完，waveOutReset / Close 也会等。
//  在 UI 线程上直接调 → 窗口僵住最长 1 秒（音效越长卡越久）。
//  → 本文件只声明，Sound.cs 统一丢 ThreadPool 跑。
//  ⚠ WAVEHDR 必须用 AllocHGlobal + StructureToPtr 放非托管内存：
//    PrepareHeader / Write 会回写 dwFlags，托管对象被 GC 挪动就坏了。
// ============================================================
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TaskHarbor
{
    internal static class Native
    {
        // ---------------- 区域（把窗口裁成圆形 / 圆角矩形） ----------------
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int nLeft, int nTop, int nRight,
                                                       int nBottom, int nWidthEllipse,
                                                       int nHeightEllipse);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateEllipticRgn(int nLeft, int nTop, int nRight, int nBottom);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        /// <summary>
        /// 裁剪窗口形状。radius 达到半边长时自动改用真椭圆 API（得到正圆）。
        /// </summary>
        public static void ApplyRegion(Form form, int w, int h, int radius, ref Region old)
        {
            if (w < 2 || h < 2) return;
            if (radius < 1) radius = 1;

            IntPtr rgn;
            if (radius * 2 >= w && radius * 2 >= h)
            {
                rgn = CreateEllipticRgn(0, 0, w, h);            // 正圆 / 正椭圆
            }
            else
            {
                int r2 = Math.Min(radius, Math.Min(w, h) / 2);
                rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, r2, r2);
            }
            if (rgn == IntPtr.Zero) return;

            Region nr;
            try
            {
                nr = Region.FromHrgn(rgn);
            }
            finally
            {
                DeleteObject(rgn);
            }
            if (nr == null) return;

            Region prev = old;
            form.Region = nr;
            old = nr;
            if (prev != null)
            {
                try { prev.Dispose(); } catch { }
            }
        }

        /// <summary>去掉形状裁剪（逐像素透明模式下必须去掉，否则又把抗锯齿边切回硬边）。</summary>
        public static void ClearRegion(Form form, ref Region old)
        {
            try { form.Region = null; } catch { }
            if (old != null)
            {
                try { old.Dispose(); } catch { }
                old = null;
            }
        }

        // ---------------- 分层窗口：整体半透明 ----------------
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey,
                                                             byte bAlpha, uint dwFlags);

        private const uint LWA_ALPHA = 0x00000002;

        /// <summary>
        /// 设置整个窗口的不透明度（255 = 完全不透明）。
        /// 需要窗口带 WS_EX_LAYERED 样式，见 MakeToolWindow()。
        ///
        /// ⚠ 逐像素模式（ULW）下【绝对不能】调这个：调过一次，UpdateLayeredWindow
        ///   就永久返回 0，球会变成一个方块，旧位图还会留下拉伸出来的淡色圆残影。
        ///   那种模式的不透明度请用 PaintBall 的 alpha 参数烘进位图。
        /// </summary>
        public static bool SetAlpha(IntPtr hwnd, int alpha)
        {
            if (alpha < 30) alpha = 30;
            if (alpha > 255) alpha = 255;
            try
            {
                return SetLayeredWindowAttributes(hwnd, 0, (byte)alpha, LWA_ALPHA);
            }
            catch
            {
                return false;
            }
        }

        // ---------------- 扩展样式：工具窗口 + 分层 ----------------
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                                int X, int Y, int cx, int cy, uint uFlags);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;

        /// <summary>
        /// 标记为工具窗口（不出现在 Alt+Tab 和任务栏），并打开分层窗口支持。
        /// ⚠ 一旦带上 WS_EX_LAYERED，后续必须调过 SetLayeredWindowAttributes 或
        ///   UpdateLayeredWindow 之一，否则窗口会完全不显示。
        /// </summary>
        public static void MakeToolWindow(IntPtr hwnd)
        {
            try
            {
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_LAYERED);
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER | SWP_FRAMECHANGED);
            }
            catch { }
        }

        /// <summary>
        /// 在「整体 Alpha 模式」和「逐像素透明模式」之间切换。
        /// 两种模式不能混用（先调过 SetLayeredWindowAttributes 会让
        /// UpdateLayeredWindow 永久失败），所以先把 WS_EX_LAYERED 样式位摘掉再装上。
        /// perPixel = true  → 之后用 UpdateLayeredWindow（调用方负责画）
        /// perPixel = false → 之后用 SetLayeredWindowAttributes（调用方负责设 Alpha）
        /// </summary>
        public static void SetLayeredMode(IntPtr hwnd, bool perPixel)
        {
            try
            {
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                int off = ex & ~WS_EX_LAYERED;
                SetWindowLong(hwnd, GWL_EXSTYLE, off);      // 摘掉：清掉上一次的分层模式
                SetWindowLong(hwnd, GWL_EXSTYLE, off | WS_EX_LAYERED);
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER | SWP_FRAMECHANGED);
                if (!perPixel) SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
            }
            catch { }
        }

        public static void SetTopMost(IntPtr hwnd, bool on)
        {
            try
            {
                SetWindowPos(hwnd, on ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch { }
        }

        // ---------------- 逐像素透明绘制 ----------------
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BMIH
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BMI { public BMIH h; public int colors; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey,
            ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BMI pbmi, uint usage,
                                                     out IntPtr ppvBits, IntPtr hSection, uint offset);

        private const int ULW_ALPHA = 0x00000002;
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;

        private static readonly Color BALL_GLYPH = Color.FromArgb(235, 58, 58, 60);   // 与网页 #3A3A3C 对上
        private static readonly Color DOT_COLOR = Color.FromArgb(107, 60, 60, 67);    // rgba(60,60,67,.42)
        private static readonly Color BADGE_RED = Color.FromArgb(255, 255, 69, 58);   // #FF453A

        /// <summary>
        /// 把悬浮球逐像素透明地画到窗口上（圆边真抗锯齿）。
        /// size      窗口边长（物理像素）
        /// docked    true = 贴边态，画握柄而不是图标
        /// side      left / right / top / bottom，决定握柄朝哪边
        /// badge     角标数字，0 表示不画
        /// alpha     整体不透明度 0-255（把 LWA_ALPHA 的效果烘进像素里）
        /// 返回 false = 没画成功，调用方必须回退到「Region + 网页渲染」的老方案。
        /// </summary>
        public static bool PaintBall(IntPtr hwnd, int size, bool docked, string side, int badge, int alpha)
        {
            if (size < 8) return false;

            IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, dib = IntPtr.Zero, oldBmp = IntPtr.Zero;
            IntPtr bits = IntPtr.Zero;
            bool ok = false;
            try
            {
                BMI bmi = new BMI();
                bmi.h.biSize = Marshal.SizeOf(typeof(BMIH));
                bmi.h.biWidth = size;
                bmi.h.biHeight = -size;          // 负数 = 自上而下，与 GDI+ 一致
                bmi.h.biPlanes = 1;
                bmi.h.biBitCount = 32;
                bmi.h.biCompression = 0;         // BI_RGB，同时也是带 Alpha 的 32bpp

                screenDc = GetDC(IntPtr.Zero);
                memDc = CreateCompatibleDC(screenDc);
                if (memDc == IntPtr.Zero) return false;

                dib = CreateDIBSection(memDc, ref bmi, 0, out bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero) return false;

                oldBmp = SelectObject(memDc, dib);

                // Bitmap 只借用这块内存，不拥有它（dispose 不会 free 掉 DIB）
                using (Bitmap bmp = new Bitmap(size, size, size * 4, PixelFormat.Format32bppPArgb, bits))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);

                    float s = size / 68f;                   // 以逻辑 68px 为基准
                    float cx = size / 2f, cy = size / 2f;
                    // 半径要减掉描边的一半宽度，否则 200% DPI 下那圈淡描边会被窗口边缘裁掉
                    float r = size / 2f - Math.Max(0.5f, s * 0.5f);
                    RectangleF rc = new RectangleF(cx - r, cy - r, r * 2f, r * 2f);

                    // 球体：白色，带一点从左上来的极淡渐变，避免看上去像一块贴纸
                    using (GraphicsPath gp = new GraphicsPath())
                    {
                        gp.AddEllipse(rc);
                        using (PathGradientBrush pgb = new PathGradientBrush(gp))
                        {
                            pgb.CenterColor = Color.White;
                            pgb.SurroundColors = new Color[] { Color.FromArgb(255, 241, 241, 245) };
                            pgb.CenterPoint = new PointF(cx - r * 0.36f, cy - r * 0.52f);
                            g.FillPath(pgb, gp);
                        }
                        // 极淡的描边：白球落在浅色桌面上也能看出轮廓
                        using (Pen pen = new Pen(Color.FromArgb(20, 0, 0, 0), Math.Max(1f, s)))
                        {
                            g.DrawEllipse(pen, rc);
                        }
                    }

                    if (docked && side != null && side.Length > 0)
                    {
                        DrawGrip(g, size, side);
                    }
                    else
                    {
                        DrawGlyph(g, cx, cy, 28f * s);
                        if (badge > 0) DrawBadge(g, size, s, badge);
                    }
                }

                // 把整体不透明度烘进像素（逐像素模式下 LWA_ALPHA 不起作用）
                if (alpha < 255)
                {
                    if (alpha < 0) alpha = 0;
                    BlitAlpha(bits, size * size, alpha);
                }

                SIZE sz = new SIZE();
                sz.cx = size;
                sz.cy = size;
                POINT src = new POINT();
                src.x = 0;
                src.y = 0;
                BLENDFUNCTION bf = new BLENDFUNCTION();
                bf.BlendOp = AC_SRC_OVER;
                bf.BlendFlags = 0;
                bf.SourceConstantAlpha = 255;
                bf.AlphaFormat = AC_SRC_ALPHA;

                ok = UpdateLayeredWindow(hwnd, screenDc, IntPtr.Zero, ref sz, memDc, ref src,
                                         0, ref bf, ULW_ALPHA);
            }
            catch
            {
                ok = false;
            }
            finally
            {
                try { if (oldBmp != IntPtr.Zero) SelectObject(memDc, oldBmp); } catch { }
                try { if (dib != IntPtr.Zero) DeleteObject(dib); } catch { }
                try { if (memDc != IntPtr.Zero) DeleteDC(memDc); } catch { }
                try { if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc); } catch { }
            }
            return ok;
        }

        /// <summary>
        /// 把分层表面擦成完全透明（w/h 必须是窗口当前尺寸）。返回 false = 没擦成。
        /// 退出逐像素模式时必须调一次：UpdateLayeredWindow 画上去的内容会一直
        /// 粘在窗口 (0,0)，而 SetLayeredWindowAttributes 只改整体 alpha、
        /// **不会清内容** —— 不擦的话旧球会在窗口放大后被 DWM 拉伸，
        /// 从网页 8% 的透明里透出来，就是那个「淡色圆」。
        ///
        /// ⚠ 调用前必须先 SetLayeredMode(hwnd, true) 重新装好逐像素模式。
        ///   只要中途调过一次 SetLayeredWindowAttributes，这里的 ULW 就会失败，
        ///   等于什么都没做。
        /// </summary>
        public static bool ClearLayered(IntPtr hwnd, int w, int h)
        {
            if (hwnd == IntPtr.Zero || w < 1 || h < 1) return false;

            IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, dib = IntPtr.Zero, oldBmp = IntPtr.Zero;
            IntPtr bits = IntPtr.Zero;
            bool ok = false;
            try
            {
                BMI bmi = new BMI();
                bmi.h.biSize = Marshal.SizeOf(typeof(BMIH));
                bmi.h.biWidth = w;
                bmi.h.biHeight = -h;             // 负数 = 自上而下
                bmi.h.biPlanes = 1;
                bmi.h.biBitCount = 32;
                bmi.h.biCompression = 0;         // BI_RGB（32bpp 下同时带 Alpha）

                screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero) return false;
                memDc = CreateCompatibleDC(screenDc);
                if (memDc == IntPtr.Zero) return false;

                dib = CreateDIBSection(memDc, ref bmi, 0, out bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero) return false;
                oldBmp = SelectObject(memDc, dib);

                // DIB 建出来就是全 0，即「全透明」—— 不用再刷一遍

                SIZE sz = new SIZE();
                sz.cx = w;
                sz.cy = h;
                POINT src = new POINT();
                BLENDFUNCTION bf = new BLENDFUNCTION();
                bf.BlendOp = AC_SRC_OVER;
                bf.BlendFlags = 0;
                bf.SourceConstantAlpha = 255;
                bf.AlphaFormat = AC_SRC_ALPHA;

                ok = UpdateLayeredWindow(hwnd, screenDc, IntPtr.Zero, ref sz, memDc, ref src,
                                         0, ref bf, ULW_ALPHA);
            }
            catch { ok = false; }
            finally
            {
                try { if (oldBmp != IntPtr.Zero && memDc != IntPtr.Zero) SelectObject(memDc, oldBmp); } catch { }
                try { if (dib != IntPtr.Zero) DeleteObject(dib); } catch { }
                try { if (memDc != IntPtr.Zero) DeleteDC(memDc); } catch { }
                try { if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc); } catch { }
            }
            return ok;
        }

        /// <summary>
        /// 直接拿 DC 把客户区刷成实色 —— 只作最后兜底用。
        ///
        /// 为什么要绕这一下：WM_PAINT 的 DC 会被子窗口（WebView2）裁掉，
        /// 所以父窗口那块表面永远没人画（实测就是纯黑），UpdateLayeredWindow
        /// 留下的球位图能一直粘在上面，挪窗口、RedrawWindow 都擦不掉。
        /// GetDC 拿到的 DC 不受那个裁剪影响，是唯一能把残留硬盖掉的办法。
        /// </summary>
        public static void FillClient(IntPtr hwnd, Color c)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                using (Graphics g = Graphics.FromHwnd(hwnd))
                {
                    g.Clear(c);
                }
            }
            catch { }
        }

        /// <summary>整块位图的 alpha 等比缩放（预乘格式下四个通道一起乘即可）。</summary>
        private static void BlitAlpha(IntPtr bits, int pixels, int alpha)
        {
            int n = pixels * 4;
            byte[] buf = new byte[n];
            Marshal.Copy(bits, buf, 0, n);
            for (int i = 0; i < n; i++)
            {
                buf[i] = (byte)((buf[i] * alpha + 127) / 255);
            }
            Marshal.Copy(buf, 0, bits, n);
        }

        /// <summary>球里的清单图标：直接把网页那个 24×24 的 SVG 用 GDI+ 复刻一遍。</summary>
        private static void DrawGlyph(Graphics g, float cx, float cy, float box)
        {
            float u = box / 24f;                       // SVG viewBox 是 24 单位
            float ox = cx - box / 2f, oy = cy - box / 2f;

            using (Pen p = new Pen(BALL_GLYPH, 1.9f * u))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;

                // 三条横线：M9 6h11 / M9 12h11 / M9 18h11
                g.DrawLine(p, ox + 9f * u, oy + 6f * u, ox + 20f * u, oy + 6f * u);
                g.DrawLine(p, ox + 9f * u, oy + 12f * u, ox + 20f * u, oy + 12f * u);
                g.DrawLine(p, ox + 9f * u, oy + 18f * u, ox + 20f * u, oy + 18f * u);

                // 两个对勾：M4 6l1.2 1.2L7.5 4.8 / M4 12l1.2 1.2L7.5 10.8
                p.Width = 2f * u;
                g.DrawLines(p, new PointF[]
                {
                    new PointF(ox + 4.0f * u, oy + 6.0f * u),
                    new PointF(ox + 5.2f * u, oy + 7.2f * u),
                    new PointF(ox + 7.5f * u, oy + 4.8f * u)
                });
                g.DrawLines(p, new PointF[]
                {
                    new PointF(ox + 4.0f * u, oy + 12.0f * u),
                    new PointF(ox + 5.2f * u, oy + 13.2f * u),
                    new PointF(ox + 7.5f * u, oy + 10.8f * u)
                });

                // 第三行的小圆圈：cx=5 cy=18 r=1.6（描边圆环）
                p.Width = 1.9f * u;
                g.DrawEllipse(p, ox + (5f - 1.6f) * u, oy + (18f - 1.6f) * u, 3.2f * u, 3.2f * u);
            }
        }

        /// <summary>角标：右上角红色胶囊 + 白色数字（与网页 #badge 的位置尺寸一致）。</summary>
        private static void DrawBadge(Graphics g, int size, float s, int badge)
        {
            string txt = badge > 99 ? "99+" : badge.ToString();

            float h = 19f * s;
            float padL = 5f * s;
            float fontPx = 11f * s;

            Font font;
            try { font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel); }
            catch { font = new Font(FontFamily.GenericSansSerif, fontPx, FontStyle.Bold, GraphicsUnit.Pixel); }

            float tw;
            using (font)
            {
                tw = g.MeasureString(txt, font, 400, StringFormat.GenericTypographic).Width;
                float w = Math.Max(19f * s, tw + padL * 2f);
                float x = size - 12f * s - w;
                float y = 12f * s;
                RectangleF rc = new RectangleF(x, y, w, h);

                using (GraphicsPath gp = RoundedRect(rc, h / 2f))
                {
                    // 白色描边（网页里是 1.5px 半透明白边）
                    // 注意：GDI+ 只实现了 PenAlignment.Center，别设 Outset（未实现）
                    using (Pen pen = new Pen(Color.FromArgb(230, 255, 255, 255), 1.5f * s))
                        g.DrawPath(pen, gp);
                    using (SolidBrush br = new SolidBrush(BADGE_RED)) g.FillPath(br, gp);
                }

                using (SolidBrush tb = new SolidBrush(Color.White))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(txt, font, tb, rc, sf);
                }
            }
        }

        /// <summary>贴边态的三个小圆点握柄，朝着露出来的那一截。</summary>
        private static void DrawGrip(Graphics g, int size, string side)
        {
            float s = size / 68f;
            float dot = 3f * s;
            float gap = 2.5f * s;
            float at = 0.21f * size;                  // 与网页 #grip 的 21% 对齐
            float c = size / 2f;

            using (SolidBrush br = new SolidBrush(DOT_COLOR))
            {
                for (int i = -1; i <= 1; i++)
                {
                    float x, y;
                    if (side == "left") { x = at; y = c + i * (dot + gap); }
                    else if (side == "right") { x = size - at; y = c + i * (dot + gap); }
                    else if (side == "top") { x = c + i * (dot + gap); y = at; }
                    else { x = c + i * (dot + gap); y = size - at; }
                    g.FillEllipse(br, x - dot / 2f, y - dot / 2f, dot, dot);
                }
            }
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            GraphicsPath gp = new GraphicsPath();
            float d = radius * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            gp.AddArc(r.X, r.Y, d, d, 180f, 90f);
            gp.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            gp.CloseFigure();
            return gp;
        }

        // ---------------- 窗口拖动 ----------------
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 2;

        /// <summary>
        /// 借系统的标题栏拖动逻辑移动无边框窗口（比手动跟随鼠标稳）。
        /// 注意：这个调用会一直阻塞到鼠标松开为止。
        /// </summary>
        public static void DragWindow(IntPtr hwnd)
        {
            try
            {
                ReleaseCapture();
                SendMessage(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, 0);
            }
            catch { }
        }

        // ---------------- 运行时设置窗口图标 ----------------
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PrivateExtractIconsW")]
        private static extern int PrivateExtractIcons(string szFileName, int nIconIndex,
            int cxIcon, int cyIcon, IntPtr[] phicon, IntPtr[] piconid, uint nIcons, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW")]
        private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType,
                                              int cxDesired, int cyDesired, uint fuLoad);

        private const int WM_SETICON = 0x0080;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;
        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x0010;

        /// <summary>
        /// 从磁盘上的 .ico 按当前 DPI 取 ICON_BIG / ICON_SMALL 塞给窗口。
        /// 为什么要这么做：编译参数 /win32icon 失败时 exe 里根本没有图标资源，
        /// 任务栏和 Alt+Tab 就会显示白纸默认图标。运行时自己设一遍最稳。
        /// </summary>
        public static bool ApplyIcon(IntPtr hwnd, string icoPath)
        {
            if (string.IsNullOrEmpty(icoPath)) return false;
            try { if (!File.Exists(icoPath)) return false; }
            catch { return false; }

            bool any = false;
            try
            {
                int bcx = Math.Max(16, GetSystemMetrics(11));   // SM_CXICON
                int bcy = Math.Max(16, GetSystemMetrics(12));   // SM_CYICON
                int scx = Math.Max(16, GetSystemMetrics(49));   // SM_CXSMICON
                int scy = Math.Max(16, GetSystemMetrics(50));   // SM_CYSMICON

                IntPtr big = ExtractOne(icoPath, bcx, bcy);
                if (big != IntPtr.Zero)
                {
                    SendMessage(hwnd, WM_SETICON, new IntPtr(ICON_BIG), big);
                    any = true;
                }
                IntPtr sm = ExtractOne(icoPath, scx, scy);
                if (sm != IntPtr.Zero)
                {
                    SendMessage(hwnd, WM_SETICON, new IntPtr(ICON_SMALL), sm);
                    any = true;
                }
            }
            catch { }
            return any;
        }

        private static IntPtr ExtractOne(string path, int cx, int cy)
        {
            try
            {
                IntPtr[] h = new IntPtr[1];
                int n = PrivateExtractIcons(path, 0, cx, cy, h, null, 1, 0);
                if (n > 0 && h[0] != IntPtr.Zero) return h[0];
            }
            catch { }
            try { return LoadImage(IntPtr.Zero, path, IMAGE_ICON, cx, cy, LR_LOADFROMFILE); }
            catch { return IntPtr.Zero; }
        }

        // ============================================================
        //  音频播放（winmm · waveOut）
        //  ------------------------------------------------------------------
        //  为什么不用 System.Media.SoundPlayer：它只吃「完整的 wav 流」。
        //  我们的提示音是程序里实时合成的 PCM，没有文件也没有现成的 wav 头，
        //  waveOut 这套 API 可以直接把裸 PCM 喂进去播。
        //
        //  ⚠ 这几个调用是阻塞式的：
        //      waveOutWrite 之后要等缓冲放完，waveOutReset / Close 也会等。
        //      **绝不能在 UI 线程上直接调**，否则窗口会僵住最长 1 秒。
        //      Sound.cs 里统一丢到 ThreadPool 上跑，本文件只负责声明。
        //
        //  ⚠ WAVEHDR 必须靠 Marshal.AllocHGlobal + StructureToPtr 交给非托管内存：
        //      PrepareHeader / Write 会往结构体里回写 dwFlags，
        //      用托管对象的话 GC 一挪动就把这块内存搞坏了。
        // ============================================================
        private const int WAVE_MAPPER = -1;
        private const int WAVE_FORMAT_PCM = 1;
        private const int CALLBACK_NULL = 0;
        private const int WHDR_DONE = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll")]
        private static extern int waveOutGetNumDevs();

        [DllImport("winmm.dll")]
        private static extern int waveOutGetVolume(IntPtr hwo, out uint pdwVolume);

        [DllImport("winmm.dll")]
        private static extern int waveOutOpen(out IntPtr hWaveOut, int uDeviceID,
            ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, int dwFlags);

        [DllImport("winmm.dll")]
        private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveOutReset(IntPtr hwo);

        [DllImport("winmm.dll")]
        private static extern int waveOutClose(IntPtr hwo);

        /// <summary>系统有几个波形输出设备（0 = 没声卡）。给 Sound.cs 判断用。</summary>
        public static int WaveOutDeviceCount()
        {
            try { return waveOutGetNumDevs(); }
            catch { return 0; }
        }

        /// <summary>读系统音量（低 16 位左声道，0xFFFF = 最大）。返回 0 才算读成功。</summary>
        public static int WaveOutGetVolume(IntPtr hwo, out uint vol)
        {
            vol = 0;
            try { return waveOutGetVolume(hwo, out vol); }
            catch { return -1; }
        }

        /// <summary>
        /// 把一段单声道 16 位 PCM 播出去（阻塞，只允许在后台线程上调）。
        /// 返回 0 表示成功；任何一步失败都返回非 0，调用方不用管具体是哪个。
        /// </summary>
        public static int PlayPcm(short[] pcm, int samplesPerSec)
        {
            if (pcm == null || pcm.Length < 2) return -1;

            IntPtr hwo = IntPtr.Zero, hdr = IntPtr.Zero, data = IntPtr.Zero;
            int rc = -1;
            bool opened = false, prepared = false;

            try
            {
                if (waveOutGetNumDevs() <= 0) return -2;

                WAVEFORMATEX wf = new WAVEFORMATEX();
                wf.wFormatTag = WAVE_FORMAT_PCM;
                wf.nChannels = 1;
                wf.nSamplesPerSec = (uint)samplesPerSec;
                wf.wBitsPerSample = 16;
                wf.nBlockAlign = 2;
                wf.nAvgBytesPerSec = (uint)(samplesPerSec * 2);
                wf.cbSize = 0;

                rc = waveOutOpen(out hwo, WAVE_MAPPER, ref wf, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
                if (rc != 0) return rc;
                opened = true;

                int bytes = pcm.Length * 2;
                data = Marshal.AllocHGlobal(bytes);
                Marshal.Copy(pcm, 0, data, pcm.Length);

                WAVEHDR h = new WAVEHDR();
                h.lpData = data;
                h.dwBufferLength = (uint)bytes;

                int sz = Marshal.SizeOf(typeof(WAVEHDR));
                hdr = Marshal.AllocHGlobal(sz);
                Marshal.StructureToPtr(h, hdr, false);

                rc = waveOutPrepareHeader(hwo, hdr, sz);
                if (rc != 0) return rc;
                prepared = true;

                rc = waveOutWrite(hwo, hdr, sz);
                if (rc != 0) return rc;

                // 等它放完。音效最长也就 900ms，等得起。
                // 用「DONE 标志 + 时间上限」两条退出条件：万一某些驱动不给
                // DONE 标志，也不会把这条后台线程永远挂住。
                int limitMs = (int)(bytes / (samplesPerSec * 2.0) * 1000.0) + 260;
                int waited = 0;
                while (waited < limitMs)
                {
                    WAVEHDR cur = (WAVEHDR)Marshal.PtrToStructure(hdr, typeof(WAVEHDR));
                    if ((cur.dwFlags & WHDR_DONE) != 0) break;
                    Thread.Sleep(12);
                    waited += 12;
                }
                rc = 0;
            }
            catch { rc = -3; }
            finally
            {
                try { if (opened) waveOutReset(hwo); } catch { }
                try { if (prepared) waveOutUnprepareHeader(hwo, hdr, Marshal.SizeOf(typeof(WAVEHDR))); } catch { }
                try { if (hdr != IntPtr.Zero) Marshal.FreeHGlobal(hdr); } catch { }
                try { if (data != IntPtr.Zero) Marshal.FreeHGlobal(data); } catch { }
                try { if (opened) waveOutClose(hwo); } catch { }
            }
            return rc;
        }
    }
}
