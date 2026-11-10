using System.Globalization;
using N_m3u8DL_RE.Config;
using N_m3u8DL_RE.DownloadManager;
using N_m3u8DL_RE.Entity;
using N_m3u8DL_RE.Parser;
using N_m3u8DL_RE.Parser.Config;
using N_m3u8DL_RE.Tests.TestSupport;
using N_m3u8DL_RE.Util;
using static N_m3u8DL_RE.Tests.TestSupport.DownloadTestHelper;

namespace N_m3u8DL_RE.Tests.DownloadManager;

[Collection("Download console")]
public class VodTsDiscontinuityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FilteringAdKeepsContinuousPacketsAndClosesActualTimestampGaps(bool insertedAd)
    {
        if (!HasTool("ffmpeg") || !HasTool("ffprobe")) return;
        var root = Directory.CreateTempSubdirectory("vod-ts-ad-").FullName;
        try
        {
            await Run("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=96x64:rate=25",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", "6",
                "-c:v", "libx264", "-g", "50", "-bf", "3", "-sc_threshold", "0", "-c:a", "aac",
                "-f", "hls", "-hls_time", "2", "-hls_segment_filename", Path.Combine(root, "seg%d.ts"),
                Path.Combine(root, "clean.m3u8"));
            // 一种清单在连续正文中插入独立广告，另一种将源时间轴中的两秒标为广告。
            var manifest = "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\nseg0.ts\n" +
                "#EXT-X-DISCONTINUITY\n#EXTINF:2,\nadjump.ts\n#EXT-X-DISCONTINUITY\n" +
                (insertedAd ? "#EXTINF:2,\nseg1.ts\n" : "") + "#EXTINF:2,\nseg2.ts\n#EXT-X-ENDLIST\n";
            await using var server = new MediaFixtureServer(root, (path, _) => path == "source.m3u8" ? manifest : null);
            using var extractor = new StreamExtractor(new ParserConfig());
            await extractor.LoadSourceFromUrlAsync(server.Url + "source.m3u8");
            var streams = await extractor.ExtractStreamsAsync();
            await extractor.FetchPlayListAsync(streams);
            VodStreamPlanner.CaptureHlsTimeline(streams);
            var options = CreateOptions(root);
            options.AdKeywords = ["adjump"];
            FilterUtil.CleanAd(streams, options.AdKeywords);
            VodStreamPlanner.AlignHlsDiscontinuities(streams);
            var manager = new SimpleDownloadManager(new DownloaderConfig
                { MyOptions = options, DirPrefix = Path.Combine(root, "tmp") }, streams, extractor);

            Assert.True(await manager.StartDownloadAsync());
            Assert.Equal(0, server.RequestCount("adjump.ts"));
            var output = Assert.Single(Directory.GetFiles(options.SaveDir!, "*.mp4"));
            if (insertedAd)
            {
                var reference = Path.Combine(root, "reference.mp4");
                var files = Enumerable.Range(0, 3).Select(i => Path.Combine(root, $"seg{i}.ts"));
                await Run("ffmpeg", "-v", "error", "-i", "concat:" + string.Join('|', files),
                    "-map", "0:v", "-map", "0:a", "-c", "copy", reference);
                // 同一正文无损拼接后，两条轨道上的每个包都应保留原来的 PTS 和 DTS。
                foreach (var track in new[] { "v", "a" })
                    Assert.Equal(await PacketTimes(reference, track), await PacketTimes(output, track));
            }
            else
            {
                Assert.Equal(0, server.RequestCount("seg1.ts"));
                var video = await PacketTimes(output, "v");
                Assert.Equal(100, video.Length);
                // 允许 B 帧重排与 AAC 帧边界留下少量首尾偏移，两轨均应闭合两秒缺口。
                foreach (var packets in new[] { video, await PacketTimes(output, "a") })
                {
                    var end = packets.Max(row => double.Parse(row.Split(',')[0], CultureInfo.InvariantCulture));
                    Assert.InRange(end, 3.9, 4.15);
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<string[]> PacketTimes(string file, string track) =>
        (await Run("ffprobe", "-v", "error", "-select_streams", track, "-show_packets",
            "-show_entries", "packet=pts_time,dts_time", "-of", "csv=p=0", file))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public async Task TimeRangeAcrossDiscontinuityKeepsReferencesBetweenSelectedSegments()
    {
        if (!HasTool("ffmpeg") || !HasTool("ffprobe")) return;
        var root = Directory.CreateTempSubdirectory("vod-ts-range-").FullName;
        try
        {
            var source = Path.Combine(root, "source.ts");
            await Run("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=96x64:rate=25",
                "-t", "8", "-c:v", "libx264", "-g", "75", "-bf", "0", "-sc_threshold", "0", source);
            await Run("ffmpeg", "-v", "error", "-i", source, "-c", "copy", "-f", "segment",
                "-segment_time", "2", "-break_non_keyframes", "1", Path.Combine(root, "seg%d.ts"));
            await File.WriteAllTextAsync(Path.Combine(root, "source.m3u8"),
                "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\nseg0.ts\n#EXTINF:2,\nseg1.ts\n" +
                "#EXT-X-DISCONTINUITY\n#EXTINF:2,\nseg2.ts\n#EXTINF:2,\nseg3.ts\n#EXT-X-ENDLIST\n");
            await using var server = new MediaFixtureServer(root, (_, _) => null);
            using var extractor = new StreamExtractor(new ParserConfig());
            await extractor.LoadSourceFromUrlAsync(server.Url + "source.m3u8");
            var streams = await extractor.ExtractStreamsAsync();
            await extractor.FetchPlayListAsync(streams);
            VodStreamPlanner.CaptureHlsTimeline(streams);
            var options = CreateOptions(root);
            options.CustomRange = new CustomRange { InputStr = "00:00:02-00:00:05.999", StartSec = 2, EndSec = 5.999 };
            FilterUtil.ApplyCustomRange(streams, options.CustomRange);
            VodStreamPlanner.AlignHlsDiscontinuities(streams);
            var manager = new SimpleDownloadManager(new DownloaderConfig
                { MyOptions = options, DirPrefix = Path.Combine(root, "tmp") }, streams, extractor);

            Assert.True(await manager.StartDownloadAsync());

            // 仅下载第 2～6 秒的两个分片。第 4～6 秒没有独立 IDR，必须继续使用前片的参考画面。
            Assert.Equal(0, server.RequestCount("seg0.ts"));
            Assert.Equal(0, server.RequestCount("seg3.ts"));
            var output = Assert.Single(Directory.GetFiles(options.SaveDir!, "*.mp4"));
            // 起点第 2～3 秒缺少被排除的参考帧，从第 3 秒的首个 IDR 开始应完整保留 75 帧。
            Assert.Equal((await FrameHashes(source)).Skip(75).Take(75), await FrameHashes(output));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ContinuousTsMergeRepairsTimestampResetWithoutLosingFrames()
    {
        if (!HasTool("ffmpeg") || !HasTool("ffprobe")) return;
        var root = Directory.CreateTempSubdirectory("vod-ts-clock-reset-").FullName;
        try
        {
            for (var i = 0; i < 2; i++)
                await Run("ffmpeg", "-v", "error", "-f", "lavfi", "-i",
                    $"color=c={(i == 0 ? "red" : "blue")}:s=96x64:r=25", "-t", "2", "-c:v", "libx264",
                    "-g", "50", "-bf", "0", "-f", "mpegts", Path.Combine(root, $"part{i}.ts"));
            // 两段都从相同的 TS 时钟开始，验证连续读取仍能处理真正的时间戳重置。
            await File.WriteAllTextAsync(Path.Combine(root, "source.m3u8"),
                "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\npart0.ts\n" +
                "#EXT-X-DISCONTINUITY\n#EXTINF:2,\npart1.ts\n#EXT-X-ENDLIST\n");
            await using var server = new MediaFixtureServer(root, (_, _) => null);
            using var extractor = new StreamExtractor(new ParserConfig());
            await extractor.LoadSourceFromUrlAsync(server.Url + "source.m3u8");
            var streams = await extractor.ExtractStreamsAsync();
            await extractor.FetchPlayListAsync(streams);
            VodStreamPlanner.CaptureHlsTimeline(streams);
            VodStreamPlanner.AlignHlsDiscontinuities(streams);
            var options = CreateOptions(root);
            var manager = new SimpleDownloadManager(new DownloaderConfig
                { MyOptions = options, DirPrefix = Path.Combine(root, "tmp") }, streams, extractor);

            Assert.True(await manager.StartDownloadAsync());

            var output = Assert.Single(Directory.GetFiles(options.SaveDir!, "*.mp4"));
            await AssertVideo(output, 4, 100);
            Assert.Equal((await FrameHashes(Path.Combine(root, "part0.ts")))
                .Concat(await FrameHashes(Path.Combine(root, "part1.ts"))), await FrameHashes(output));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("libx264")]
    [InlineData("libx265")]
    public async Task PartStartingBetweenKeyframesKeepsAllDecodedFrames(string encoder)
    {
        if (!HasTool("ffmpeg") ||
            !(await Run("ffmpeg", "-hide_banner", "-encoders")).Contains(encoder, StringComparison.Ordinal))
            return;
        var root = Directory.CreateTempSubdirectory("vod-ts-discontinuity-").FullName;
        try
        {
            var source = Path.Combine(root, "source.ts");
            var arguments = new List<string> { "-v", "error", "-y", "-f", "lavfi", "-i",
                "testsrc2=size=96x64:rate=25", "-t", "6", "-c:v", encoder, "-g", "50", "-bf", "0" };
            if (encoder == "libx265")
                arguments.AddRange(["-x265-params", "keyint=50:min-keyint=50:scenecut=0:pools=1:log-level=error"]);
            else
                arguments.AddRange(["-sc_threshold", "0"]);
            arguments.Add(source);
            await Run("ffmpeg", arguments.ToArray());
            // 在第 3 秒切分，位于第 2 秒和第 4 秒的 IDR 之间。
            // 第二个 part 的首秒画面依赖前一个 part 的参考图像。
            await Run("ffmpeg", "-v", "error", "-y", "-i", source, "-c", "copy", "-f", "segment",
                "-segment_time", "3", "-break_non_keyframes", "1", Path.Combine(root, "part%d.ts"));
            Assert.Equal(2, Directory.GetFiles(root, "part*.ts").Length);
            await File.WriteAllTextAsync(Path.Combine(root, "source.m3u8"),
                "#EXTM3U\n#EXT-X-TARGETDURATION:3\n#EXTINF:3,\npart0.ts\n" +
                "#EXT-X-DISCONTINUITY\n#EXTINF:3,\npart1.ts\n#EXT-X-ENDLIST\n");
            await using var server = new MediaFixtureServer(root, (_, _) => null);
            using var extractor = new StreamExtractor(new ParserConfig());
            await extractor.LoadSourceFromUrlAsync(server.Url + "source.m3u8");
            var streams = await extractor.ExtractStreamsAsync();
            await extractor.FetchPlayListAsync(streams);
            Assert.Equal(2, Assert.Single(streams).Playlist!.MediaParts.Count);
            VodStreamPlanner.CaptureHlsTimeline(streams);
            VodStreamPlanner.AlignHlsDiscontinuities(streams);
            var options = CreateOptions(root);
            var manager = new SimpleDownloadManager(new DownloaderConfig
                { MyOptions = options, DirPrefix = Path.Combine(root, "tmp") }, streams, extractor);

            Assert.True(await manager.StartDownloadAsync().WaitAsync(TimeSpan.FromSeconds(30)));

            var expected = await FrameHashes(source);
            Assert.Equal(150, expected.Length);
            // 像素哈希同时检查丢帧和参考图像损坏，
            // 仅检查转封装退出码无法发现这两类问题。
            Assert.Equal(expected, await FrameHashes(Assert.Single(Directory.GetFiles(options.SaveDir!, "*.mp4"))));
        }
        finally { Directory.Delete(root, true); }
    }
}
