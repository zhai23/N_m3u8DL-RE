using N_m3u8DL_RE.Entity;
using N_m3u8DL_RE.Common.Log;
using N_m3u8DL_RE.Common.Resource;
using System.Globalization;

namespace N_m3u8DL_RE.Util;

internal sealed record RepairedAacTrack(int AudioIndex, string Path, double StartSeconds);

internal static class AacPacketClock
{
    // ADTS AAC-LC, one raw_data_block per access unit.
    private const int SamplesPerFrame = 1024;
    private static readonly int[] SampleRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000,
        22050, 16000, 12000, 11025, 8000, 7350];
    private static readonly string[] Layouts = ["", "mono", "stereo", "3.0", "4.0", "5.0", "5.1"];

    // The caller owns tempDir and deletes it after the final mux (also on cancellation).
    internal static async Task<List<RepairedAacTrack>> PrepareAsync(string binary, IReadOnlyList<string> sourceArguments,
        IReadOnlyList<Mediainfo> infos, string tempDir, bool loopbackInput, CancellationToken token)
    {
        var indices = infos.Where(info => info.Type == "Audio").Select((info, i) => (info, i))
            .Where(item => item.info.BaseInfo?.StartsWith("aac (LC)", StringComparison.OrdinalIgnoreCase) == true)
            .Select(item => item.i).ToArray();
        if (indices.Length == 0)
            return [];
        Directory.CreateDirectory(tempDir);
        // Keep every original A/V stream active, just as in the final mux. FFmpeg otherwise
        // chooses the first active track as its input origin, losing a legitimate start offset.
        List<string> args = [.. sourceArguments, "-loglevel", "error", "-map", "0:v?", "-map", "0:a?",
            "-c", "copy", "-f", "null", "-"];
        foreach (var i in indices)
        {
            args.AddRange(["-map", $"0:a:{i}", "-c:a", "copy", "-bsf:a", "aac_adtstoasc", "-f", "framecrc", System.IO.Path.Combine(tempDir, $"{i}.crc"),
                "-map", $"0:a:{i}", "-c:a", "copy", "-f", "adts", System.IO.Path.Combine(tempDir, $"{i}.aac")]);
        }
        var extraction = await ProcessUtil.RunAsync(binary, args, token, loopbackInput: loopbackInput).ConfigureAwait(false);
        if (extraction.ExitCode != 0)
        {
            Logger.Warn(ResString.aacClockProbeFailed);
            return [];
        }
        List<RepairedAacTrack> repaired = [];
        foreach (var i in indices)
        {
            var plan = Plan(System.IO.Path.Combine(tempDir, $"{i}.crc"), token);
            if (plan == null || plan.Insertions.Count == 0)
                continue;
            var raw = System.IO.Path.Combine(tempDir, $"{i}.aac");
            var result = System.IO.Path.Combine(tempDir, $"{i}-filled.aac");
            if (await FillAsync(binary, raw, result, plan, token).ConfigureAwait(false))
                repaired.Add(new(i, result, plan.StartSeconds));
            else
                Logger.Warn(ResString.aacClockProbeFailed);
        }
        return repaired;
    }

    internal sealed record FillPlan(double StartSeconds, int SampleRate, long FrameCount, Dictionary<long, int> Insertions);

    internal static FillPlan? Plan(string timestamps, CancellationToken token)
    {
        double timeBase = 0;
        int rate = 0;
        long? start = null;
        long frames = 0, inserted = 0;
        Dictionary<long, int> gaps = [];
        foreach (var line in File.ReadLines(timestamps))
        {
            token.ThrowIfCancellationRequested();
            if (line.StartsWith("#tb 0:", StringComparison.Ordinal))
            {
                var parts = line[6..].Trim().Split('/');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var n) || !int.TryParse(parts[1], out var d) || n <= 0 || d <= 0)
                    return null;
                timeBase = (double)n / d;
            }
            else if (line.StartsWith("#sample_rate 0:", StringComparison.Ordinal))
                int.TryParse(line[15..], out rate);
            else if (line.StartsWith("0,", StringComparison.Ordinal))
            {
                var fields = line.Split(',', 6);
                if (timeBase == 0 || !SampleRates.Contains(rate) || fields.Length < 5 ||
                    !long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pts))
                    return null;
                start ??= pts;
                var expected = ((double)pts - start.Value) * timeBase * rate / SamplesPerFrame;
                var deficit = expected - frames - inserted;
                var missing = Math.Max(0, Math.Floor(deficit + 0.5));
                // Negative drift would require deleting original packets. Large jumps are
                // timestamp discontinuities, not safe evidence of lost audio (FFmpeg's default
                // MPEG-TS discontinuity threshold is 10 seconds).
                if (deficit < -0.5 - timeBase * rate / SamplesPerFrame || missing * SamplesPerFrame >= 10.0 * rate)
                    return null;
                if (missing > 0)
                {
                    gaps.Add(frames, (int)missing);
                    inserted += (int)missing;
                }
                frames++;
            }
        }
        return start == null ? null : new(start.Value * timeBase, rate, frames, gaps);
    }

    private readonly record struct Config(int Rate, int Channels);

    // Maximum ADTS frame length is 13 bits. CRC and multiple raw blocks are not rewritten.
    private static int ReadFrame(Stream stream, byte[] buffer, out Config config)
    {
        config = default;
        if (stream.Position == stream.Length)
            return 0;
        if (stream.Length - stream.Position < 7)
            return -1;
        stream.ReadExactly(buffer, 0, 7);
        var rateIndex = (buffer[2] >> 2) & 15;
        var channels = ((buffer[2] & 1) << 2) | (buffer[3] >> 6);
        var size = ((buffer[3] & 3) << 11) | (buffer[4] << 3) | (buffer[5] >> 5);
        if (buffer[0] != 0xff || (buffer[1] & 0xf7) != 0xf1 || (buffer[2] >> 6) != 1 || rateIndex >= SampleRates.Length ||
            channels <= 0 || channels >= Layouts.Length || (buffer[6] & 3) != 0 || size <= 7 || size - 7 > stream.Length - stream.Position)
            return -1;
        stream.ReadExactly(buffer, 7, size - 7);
        config = new(SampleRates[rateIndex], channels);
        return size;
    }

    private static async Task<bool> FillAsync(string binary, string raw, string output, FillPlan plan, CancellationToken token)
    {
        using var input = File.OpenRead(raw);
        var frame = new byte[8192];
        if (ReadFrame(input, frame, out var config) <= 0 || config.Rate != plan.SampleRate)
            return false;
        // Encode silence once for this exact rate/channel configuration; no original AAC
        // access unit goes through an audio encoder. Use a steady-state, not a priming frame.
        var template = output + ".silence.aac";
        var generated = await ProcessUtil.RunAsync(binary, ["-y", "-loglevel", "error", "-f", "lavfi", "-i",
            $"anullsrc=r={config.Rate}:cl={Layouts[config.Channels]}", "-frames:a", "3", "-c:a", "aac", "-profile:a", "aac_low",
            "-f", "adts", template], token).ConfigureAwait(false);
        if (generated.ExitCode != 0)
            return false;
        var silence = new byte[8192];
        using var encoded = File.OpenRead(template);
        if (ReadFrame(encoded, silence, out _) <= 0)
            return false;
        var silenceLength = ReadFrame(encoded, silence, out var silentConfig);
        if (silenceLength <= 0 || silentConfig != config)
            return false;
        input.Position = 0;
        using var filled = File.Create(output);
        for (long index = 0; index < plan.FrameCount; index++)
        {
            token.ThrowIfCancellationRequested();
            var size = ReadFrame(input, frame, out var current);
            if (size <= 0 || current != config)
                return false;
            if (plan.Insertions.TryGetValue(index, out var count))
                for (int j = 0; j < count; j++)
                    filled.Write(silence, 0, silenceLength);
            filled.Write(frame, 0, size);
        }
        return input.Position == input.Length;
    }
}
