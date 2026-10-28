// ============================================================
//  音效：程序里实时合成提示音，不依赖任何音频文件
//
//  为什么不用音频文件
//  ------------------------------------------------------------------
//  · 交付形态是「一个 exe」，多一个 wav 就要多一条 /resource + 释放路径；
//  · 更要紧的是：音频文件属于二进制，我没法在这台机器上编译验证，
//    万一嵌错了、释放失败，表现就是「点了没声音」，极难排查。
//  · 合成出来的波形是纯代码，静态检查能覆盖，出错面小得多。
//  于是最终方案：Sound.Play("soft") 现场算出 PCM，交给 winmm 播。
//
//  ⚠ 两个关键约束
//  ------------------------------------------------------------------
//  1. C# 5 语法（编译器是系统自带 csc.exe，只有 /langversion:5）：
//     不能用字符串插值、?. 、nameof、表达式体成员。
//  2. 必须走 waveOut 播内存里的 PCM，不能用 System.Media.SoundPlayer ——
//     它只认完整 wav 文件流，而我们要自己拼一个 44 字节的 RIFF 头。
//     waveOutOpen/PrepareHeader/Write 这套 API 可以直接吃裸 PCM。
//
//  ⚠ 播放必须是异步的
//  ------------------------------------------------------------------
//  waveOut 这套 API 是阻塞式的（写进缓冲后要等它放完，
//  waveOutReset / Close 也会等）。UI 线程上直接调会卡住窗口，
//  所以这里统一把播放丢给线程池，只把「算波形」留在调用线程
//  （算波形是纯算术，毫秒级，不阻塞）。
//  ⚠ 真出问题时第一时间怀疑的应该是这里 —— 日志在 build\ 下看不到，
//    只能靠 DumpWav() 把波形导出来自己听，别去猜。
// ============================================================
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TaskHarbor
{
    internal static class Sound
    {
        // 采样参数：22050Hz 单声道 16 位，够用又省算力
        private const int SR = 22050;

        // 系统音量只问一次（同步调用，很便宜），后面播放全走后台线程
        private static int _vol = -1;

        // 上一次播放的音色（给设置页「试听」用的回显，也方便诊断）
        private static string _last = "";

        public static string LastPlayed { get { return _last; } }

        // ---------------- 音色表（顺序即下拉框顺序） ----------------
        //   id            显示名          说明
        public static readonly string[] IDS = { "none", "ding", "soft", "double", "rise", "drop", "wood", "glass" };
        public static readonly string[] NAMES =
        {
            "关闭（不发声）",
            "清脆铃声",     // 高频正弦 + 上方五度泛音，像小铃铛
            "柔和三音",     // 880 → 660 → 880，温和
            "双响",         // 同音敲两下
            "上行提示",     // 784 → 1046 → 1318，好消息
            "下滑提示",     // 1318 → 1046 → 784，该动手了
            "木鱼声",       // 低音快衰减
            "玻璃风铃"      // 高频错落
        };
        public static readonly string[] TIPS =
        {
            "",
            "一声清亮的金属铃声，短促、辨识度高",
            "三个音依次落下，温和不吵人（默认）",
            "两声同音，像敲门，存在感更强",
            "两个音往上走，适合「做完了」这种好消息",
            "两个音往下走，适合提醒「该动手了」",
            "短促的木质敲击声，闷而不刺耳",
            "细碎的高频串音，像风铃"
        };

        public static int IndexOf(string id)
        {
            if (id != null)
            {
                for (int i = 0; i < IDS.Length; i++)
                    if (IDS[i] == id) return i;
            }
            return 2;    // 认不出来就退回默认的「柔和三音」
        }

        // ============================================================
        //  波形合成
        //  约定：每个音是一段正弦波，带一个「不能太陡」的包络，
        //  否则起音和收尾会爆出「咔」的一声。
        //  太陡的包络（<200 个采样）在 22050Hz 下会听见明显杂音。
        // ============================================================
        private static void Tone(short[] buf, double at, double dur, double f, double amp)
        {
            int s0 = (int)(at * SR);
            int n = (int)(dur * SR);
            if (s0 < 0) s0 = 0;
            if (n < 1) return;
            if (s0 + n > buf.Length) n = buf.Length - s0;
            if (n < 1) return;

            // 包络总长度按「20% 起音 / 余下衰减」分配，但起音至少 260 个采样
            int atk = (int)(n * 0.2);
            if (atk < 260) atk = 260;
            if (atk > n / 2) atk = n / 2;
            if (atk < 1) atk = 1;

            double w = 2.0 * Math.PI * f / SR;
            for (int i = 0; i < n; i++)
            {
                double env;
                if (i < atk) env = (double)i / atk;
                else
                {
                    double t = (double)(i - atk) / (n - atk);
                    // 指数衰减，尾巴收得干净
                    env = Math.Exp(-3.6 * t);
                    // 最后 140 个采样再补一段线性淡出，防止截断爆音
                    if (n - i < 140) env *= (double)(n - i) / 140.0;
                }
                double v = Math.Sin(w * i) * env * amp;
                int s = (int)(v * 32000.0);
                if (s > 32767) s = 32767;
                if (s < -32768) s = -32768;
                // 叠加而不是覆盖：多个音重叠时才不会互相削掉
                int mix = buf[s0 + i] + s;
                if (mix > 32767) mix = 32767;
                if (mix < -32768) mix = -32768;
                buf[s0 + i] = (short)mix;
            }
        }

        /// <summary>把一个音色 id 合成成 PCM 采样数组。认不出来就当「柔和三音」。</summary>
        private static short[] Compose(string id)
        {
            int ms = 380;
            if (id == "ding") ms = 700;
            else if (id == "soft") ms = 760;
            else if (id == "double") ms = 520;
            else if (id == "tri") ms = 620;
            else if (id == "rise") ms = 560;
            else if (id == "drop") ms = 560;
            else if (id == "wood") ms = 340;
            else if (id == "glass") ms = 900;

            short[] b = new short[ms * SR / 1000 + 8];

            if (id == "ding")
            {
                Tone(b, 0.000, 0.50, 1180, 0.42);
                Tone(b, 0.008, 0.42, 1770, 0.20);   // 上方五度，金属感
                Tone(b, 0.016, 0.34, 2360, 0.10);
            }
            else if (id == "soft")
            {
                Tone(b, 0.000, 0.40, 880.0, 0.34);
                Tone(b, 0.100, 0.40, 660.0, 0.34);
                Tone(b, 0.200, 0.50, 880.0, 0.34);
            }
            else if (id == "double")
            {
                Tone(b, 0.000, 0.26, 1250, 0.42);
                Tone(b, 0.150, 0.32, 1250, 0.34);
            }
            else if (id == "rise")
            {
                Tone(b, 0.000, 0.18, 784, 0.36);
                Tone(b, 0.085, 0.18, 1046, 0.36);
                Tone(b, 0.170, 0.30, 1318, 0.36);
            }
            else if (id == "drop")
            {
                Tone(b, 0.000, 0.18, 1318, 0.36);
                Tone(b, 0.085, 0.18, 1046, 0.36);
                Tone(b, 0.170, 0.30, 784, 0.36);
            }
            else if (id == "wood")
            {
                // 木鱼：低音为主，配两个短的泛音，衰减快
                Tone(b, 0.000, 0.16, 480, 0.46);
                Tone(b, 0.000, 0.09, 720, 0.26);
                Tone(b, 0.000, 0.05, 1080, 0.14);
            }
            else if (id == "glass")
            {
                // 风铃：一串高频短音错开落下。
                // ⚠ 幅度要比别的音色给得大：五个音是错开的，几乎不叠加，
                //   每个都是独立起音，所以整体响度天然偏低。
                //   实测 0.20 那版峰值只有 7548，比其它音色弱一截，
                //   tools/sound_test.py 会直接判 FAIL，这里提到 0.40+
                Tone(b, 0.000, 0.26, 2093, 0.46);
                Tone(b, 0.070, 0.24, 2637, 0.43);
                Tone(b, 0.140, 0.22, 2349, 0.41);
                Tone(b, 0.205, 0.20, 3136, 0.38);
                Tone(b, 0.265, 0.34, 2793, 0.36);
            }
            else
            {
                // 兜底 = soft
                Tone(b, 0.000, 0.40, 880.0, 0.34);
                Tone(b, 0.100, 0.40, 660.0, 0.34);
                Tone(b, 0.200, 0.50, 880.0, 0.34);
            }

            return b;
        }

        // ============================================================
        //  播放（winmm waveOut，直接吃内存里的 PCM）
        // ============================================================
        //  播放必须异步 —— waveOut 这套 API 写进缓冲后要等它放完，
        //  在 UI 线程上直接调会卡住窗口。这里把耗时的部分丢给线程池。
        // ============================================================
        /// <summary>系统音量（0-100）。没有声卡或查不到时返回 -1。</summary>
        public static int SystemVolume()
        {
            if (_vol >= 0) return _vol;
            _vol = -1;
            try
            {
                if (Native.WaveOutDeviceCount() <= 0) return _vol;
                uint v;
                if (Native.WaveOutGetVolume(IntPtr.Zero, out v) == 0)
                {
                    // 低 16 位是左声道，0xFFFF = 最大
                    _vol = (int)Math.Round(((v & 0xFFFF) / 65535.0) * 100.0);
                }
            }
            catch { }
            return _vol;
        }

        public static bool IsMuted()
        {
            int v = SystemVolume();
            return v == 0;      // -1（查不到）不算静音，宁可多响一次也别漏
        }

        /// <summary>
        /// 异步播一个音色。绝不阻塞调用线程 —— 内部丢给线程池。
        /// winmm 的 waveOut 系列是阻塞 API，UI 线程上直接调会把窗口卡住。
        /// </summary>
        public static void Play(string id)
        {
            if (id == null || id.Length == 0 || id == "none") return;
            _last = id;
            try
            {
                // 先把 PCM 算好（毫秒级），再把耗时的「等它放完」丢后台
                short[] pcm = Compose(id);
                PlayPcmAsync(pcm);
            }
            catch { }
        }

        /// <summary>设置页的「试听」——和 Play 是同一件事，名字分开只为读起来清楚。</summary>
        public static void Preview(string id)
        {
            Play(id);
        }

        /// <summary>把 PCM 丢给线程池播放（真正的阻塞在 Native.PlayPcm 里）。</summary>
        private static void PlayPcmAsync(short[] pcm)
        {
            ThreadPool.QueueUserWorkItem(delegate(object st)
            {
                try { Native.PlayPcm((short[])st, SR); }
                catch { }
            }, pcm);
        }

        // ------------------------------------------------------------
        //  为了方便自己排查：把某个音色导成完整 wav（带 RIFF 头）
        //  放在磁盘上，用播放器直接听。只在需要时调，正常运行不碰。
        //  运维用：Sound.DumpWav("ding", @"C:\Temp\ding.wav")
        // ------------------------------------------------------------
        public static bool DumpWav(string id, string path)
        {
            try
            {
                short[] pcm = Compose(id);
                int dataLen = pcm.Length * 2;
                byte[] outb = new byte[44 + dataLen];

                // RIFF 头（44 字节，标准 PCM）
                Encoding.ASCII.GetBytes("RIFF").CopyTo(outb, 0);
                BitConverter.GetBytes(36 + dataLen).CopyTo(outb, 4);
                Encoding.ASCII.GetBytes("WAVE").CopyTo(outb, 8);
                Encoding.ASCII.GetBytes("fmt ").CopyTo(outb, 12);
                BitConverter.GetBytes(16).CopyTo(outb, 16);          // PCM 子块大小
                BitConverter.GetBytes((short)1).CopyTo(outb, 20);    // WAVE_FORMAT_PCM
                BitConverter.GetBytes((short)1).CopyTo(outb, 22);    // 单声道
                BitConverter.GetBytes(SR).CopyTo(outb, 24);
                BitConverter.GetBytes(SR * 2).CopyTo(outb, 28);
                BitConverter.GetBytes((short)2).CopyTo(outb, 32);    // block align
                BitConverter.GetBytes((short)16).CopyTo(outb, 34);   // 位深
                Encoding.ASCII.GetBytes("data").CopyTo(outb, 36);
                BitConverter.GetBytes(dataLen).CopyTo(outb, 40);

                Buffer.BlockCopy(pcm, 0, outb, 44, dataLen);
                System.IO.File.WriteAllBytes(path, outb);
                return true;
            }
            catch { return false; }
        }

        /// <summary>所有音色的 id 列表（设置页下拉框用）。</summary>
        public static List<string> IdList()
        {
            List<string> l = new List<string>();
            for (int i = 0; i < IDS.Length; i++) l.Add(IDS[i]);
            return l;
        }
    }
}
