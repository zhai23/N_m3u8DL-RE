using System.Globalization;
using N_m3u8DL_RE.Config;
using N_m3u8DL_RE.DownloadManager;
using N_m3u8DL_RE.Parser;
using N_m3u8DL_RE.Parser.Config;
using N_m3u8DL_RE.Tests.TestSupport;
using N_m3u8DL_RE.Util;
using static N_m3u8DL_RE.Tests.TestSupport.DownloadTestHelper;

namespace N_m3u8DL_RE.Tests.DownloadManager;

[Collection("Download console")]
public class VodAacPlaybackClockTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task FlashAndBeepStayTogetherOnContinuousPlayback(bool missingSamples, bool removeAds)
    {
        if (!HasTool("ffmpeg")) return;
        var root = Directory.CreateTempSubdirectory("vod-aac-clock-").FullName;
        try
        {
            var playlist = Path.Combine(root, "source.m3u8");
            // Flash and beep together every two seconds. Missing AAC frames fall in silence,
            // away from both the events and the ad boundaries.
            string[] audioFilter = missingSamples
                ? ["-af", "asetnsamples=n=1024:p=0,aselect='not(between(mod(t,2),1,1.12))'"]
                : [];
            await Run("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "color=black:s=96x64:r=25",
                "-f", "lavfi", "-i", @"aevalsrc=if(between(mod(t\,2)\,0.52\,0.68)\,0.65*sin(2*PI*880*t)\,0):s=48000",
                "-t", "12", "-vf", "drawbox=color=white:t=fill:enable='between(mod(t,2),0.519,0.679)'",
                ..audioFilter, "-c:v", "libx264", "-g", "25", "-bf", "0", "-sc_threshold", "0", "-c:a", "aac",
                "-f", "hls", "-hls_time", "1", "-hls_list_size", "0", "-hls_segment_filename",
                Path.Combine(root, "seg-%02d.ts"), playlist]);
            var sourceFlashes = await ReadMarkers(playlist, "-vf", "negate,blackdetect=d=0.08", "lavfi.black_start");
            Assert.Equal(6, sourceFlashes.Length);
            if (missingSamples)
            {
                var sourceBeeps = await ReadMarkers(playlist, "-af",
                    "asetpts=N/SR/TB,silencedetect=n=-35dB:d=0.1", "lavfi.silence_end");
                Assert.Equal(6, sourceBeeps.Length);
                Assert.True(sourceFlashes[^1] - sourceBeeps[^1] > 0.5, "Fixture must exhibit cumulative drift.");
            }

            var segments = Directory.GetFiles(root, "seg-*.ts").Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(12, segments.Length);
            int[] adIndices = [3, 4, 7, 8];
            var manifest = await File.ReadAllLinesAsync(playlist);
            Directory.CreateDirectory(Path.Combine(root, "ads"));
            foreach (var index in adIndices)
            {
                var name = Path.GetFileName(segments[index]);
                File.Move(segments[index], Path.Combine(root, "ads", name));
                manifest[Array.IndexOf(manifest, name)] = "ads/" + name;
            }
            await File.WriteAllLinesAsync(playlist, manifest);
            await using var server = new MediaFixtureServer(root, (_, _) => null);
            using var extractor = new StreamExtractor(new ParserConfig());
            await extractor.LoadSourceFromUrlAsync(server.Url + "source.m3u8");
            var streams = await extractor.ExtractStreamsAsync();
            await extractor.FetchPlayListAsync(streams);
            var options = CreateOptions(root);
            options.AdKeywords = removeAds ? ["/ads/"] : null;
            VodStreamPlanner.CaptureHlsTimeline(streams);
            FilterUtil.CleanAd(streams, options.AdKeywords);
            VodStreamPlanner.AlignHlsDiscontinuities(streams);
            var manager = new SimpleDownloadManager(new DownloaderConfig
                { MyOptions = options, DirPrefix = Path.Combine(root, "tmp") }, streams, extractor);
            Assert.True(await manager.StartDownloadAsync());
            foreach (var index in adIndices)
                Assert.Equal(!removeAds, server.RequestCount("ads/" + Path.GetFileName(segments[index])) > 0);

            var output = Path.Combine(options.SaveDir!, "result.mp4");
            var flashes = await ReadMarkers(output, "-vf", "negate,blackdetect=d=0.08", "lavfi.black_start");
            // Reset timestamps to the count of real decoded samples, as a player feeding PCM
            // continuously would. Container PTS alone also count periods with no sound samples.
            var beeps = await ReadMarkers(output, "-af",
                "asetpts=N/SR/TB,silencedetect=n=-35dB:d=0.1", "lavfi.silence_end");
            double[] expected = removeAds
                ? [sourceFlashes[0], sourceFlashes[1], sourceFlashes[3] - 2, sourceFlashes[5] - 4]
                : sourceFlashes;
            Assert.Equal(expected.Length, flashes.Length);
            Assert.Equal(expected.Length, beeps.Length);
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.InRange(Math.Abs(flashes[i] - expected[i]), 0, 0.12);
                Assert.InRange(Math.Abs(beeps[i] - flashes[i]), 0, 0.075);
            }

            var kept = segments.Where((_, index) => !adIndices.Contains(index));
            var reference = removeAds ? "concat:" + string.Join('|', kept) : playlist;
            var originals = await AacPackets(reference);
            var merged = await AacPackets(output);
            Assert.NotEmpty(originals);
            if (missingSamples)
            {
                var next = 0;
                var inserted = new List<int>();
                for (var index = 0; index < merged.Length; index++)
                {
                    if (next < originals.Length && originals[next] == merged[index])
                        next++;
                    else
                        inserted.Add(index);
                }
                Assert.Equal(originals.Length, next);
                // Six 120ms holes at 48 kHz, plus up to one frame of cut rounding per splice.
                Assert.InRange(inserted.Count, 1, 6 * 6 + 2);
                Assert.Single(inserted.Select(index => merged[index]).Distinct());
                await AssertSilentPacket(root, output, inserted[0]);
            }
            else
                Assert.Equal(originals, merged);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<double[]> ReadMarkers(string input, string option, string filter, string key)
    {
        var metadata = option == "-vf" ? "metadata" : "ametadata";
        var result = await Run("ffmpeg", "-v", "error", "-i", input, "-map", option == "-vf" ? "0:v:0" : "0:a:0",
            option, $"{filter},{metadata}=mode=print:key={key}:file=-", "-f", "null", "-");
        return result.Split('\n').Where(line => line.StartsWith(key + "=", StringComparison.Ordinal))
            .Select(line => double.Parse(line[(key.Length + 1)..], CultureInfo.InvariantCulture)).ToArray();
    }

    private static async Task<string[]> AacPackets(string input)
    {
        var hashes = await Run("ffmpeg", "-v", "error", "-i", input, "-map", "0:a:0", "-c:a", "copy",
            "-bsf:a", "aac_adtstoasc", "-f", "framehash", "-hash", "sha256", "-");
        return hashes.Split('\n').Where(line => line.Contains(',') && !line.StartsWith('#'))
            .Select(line => line.Split(',')[5].Trim()).ToArray();
    }

    private static async Task AssertSilentPacket(string root, string output, int index)
    {
        var packet = Path.Combine(root, "padding.aac");
        await Run("ffmpeg", "-v", "error", "-i", output, "-map", "0:a:0", "-c:a", "copy", "-bsf:a",
            $"noise=drop='not(eq(n,{index}))'", "-f", "adts", packet);
        var frame = await File.ReadAllBytesAsync(packet);
        Assert.NotEmpty(frame);
        await File.WriteAllBytesAsync(packet, Enumerable.Repeat(frame, 8).SelectMany(bytes => bytes).ToArray());
        var pcm = Path.Combine(root, "padding.pcm");
        await Run("ffmpeg", "-v", "error", "-f", "aac", "-i", packet, "-c:a", "pcm_s16le", "-f", "s16le", pcm);
        var samples = await File.ReadAllBytesAsync(pcm);
        Assert.Equal(8 * 1024 * sizeof(short), samples.Length);
        Assert.True(samples.All(value => value == 0), "An inserted AAC packet must decode to silence.");
    }
}
