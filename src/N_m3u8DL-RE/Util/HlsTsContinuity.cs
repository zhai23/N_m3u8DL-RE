using System.ComponentModel;
using System.Globalization;

namespace N_m3u8DL_RE.Util;

internal static class HlsTsContinuity
{
    internal static async Task<bool> IsContinuousAsync(string binary, string previousSegment, string nextSegment)
    {
        var edges = await Task.WhenAll(ReadClocksAsync(binary, previousSegment), ReadClocksAsync(binary, nextSegment));
        var previous = edges[0];
        var next = edges[1];
        // framecrc 的头部包含各流的顺序、类型、编码、时间基和媒体配置。
        if (previous == null || next == null || !previous.Headers.SequenceEqual(next.Headers) ||
            previous.Tracks.Count != next.Tracks.Count)
            return false;

        const long wrap = 1L << 33;
        foreach (var (index, track) in previous.Tracks)
        {
            if (!next.Tracks.TryGetValue(index, out var following))
                return false;
            // AAC 采样时间换算到 90 kHz 时相邻包可能差几个 tick。也要接受 MPEG-TS 时钟回绕。
            var delta = (following.FirstDts - track.EndDts) & (wrap - 1);
            if (Math.Min(delta, wrap - delta) > 3)
                return false;
        }
        return true;
    }

    private sealed class SegmentClocks
    {
        public List<string> Headers { get; } = [];
        public Dictionary<int, (long FirstDts, long EndDts)> Tracks { get; } = [];
        public bool Invalid { get; private set; }

        public void ReadLine(string line)
        {
            if (line.StartsWith('#'))
            {
                // Annex B 参数集的起始码长度可变，应比较解复用后的编码和轨道配置。
                if (!line.StartsWith("#extradata ", StringComparison.Ordinal))
                    Headers.Add(line);
                return;
            }
            // framecrc 每行的前四列为流号、DTS、PTS 和包时长。B 帧的 PTS 可以乱序。
            var fields = line.Split(',', 5);
            if (fields.Length != 5 ||
                !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ||
                !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dts) ||
                !long.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration) ||
                dts == long.MinValue || duration <= 0)
            {
                Invalid = true;
                return;
            }
            var first = Tracks.TryGetValue(index, out var existing) ? existing.FirstDts : dts;
            Tracks[index] = (first, dts + duration);
        }
    }

    private static async Task<SegmentClocks?> ReadClocksAsync(string binary, string file)
    {
        var clocks = new SegmentClocks();
        try
        {
            var result = await ProcessUtil.RunAsync(binary,
                ["-nostdin", "-v", "error", "-copyts", "-f", "mpegts", "-i", file, "-map", "0:v?", "-map", "0:a?",
                    "-c", "copy", "-copyinkf", "-avoid_negative_ts", "disabled", "-f", "framecrc", "pipe:1"],
                default, captureLimit: 1024, outputLine: clocks.ReadLine);
            if (result.ExitCode != 0 || result.Error.Length > 0 || clocks.Invalid || clocks.Tracks.Count == 0)
                return null;
            // 只有全部音视频流都提供包和 TS 原生时间基，才能判定其时钟连续。
            var timeBases = clocks.Headers.Where(h => h.StartsWith("#tb ", StringComparison.Ordinal)).ToList();
            if (timeBases.Count != clocks.Tracks.Count ||
                !clocks.Tracks.Keys.All(i => timeBases.Contains($"#tb {i}: 1/90000")))
                return null;
            return clocks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return null;
        }
    }
}
