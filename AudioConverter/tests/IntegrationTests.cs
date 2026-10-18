using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using AudioConverter;

internal static class IntegrationTests
{
    private static string ffmpeg;
    private static string ffprobe;
    private static string work;
    private static ConversionEngine engine;
    private static int passed;
    private static int failed;
    private static readonly CancellationToken None = CancellationToken.None;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            string project = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
            ffmpeg = Path.Combine(project, "release", "AudioConverter", "tools", "ffmpeg.exe");
            ffprobe = Path.Combine(project, "release", "AudioConverter", "tools", "ffprobe.exe");
            Assert(File.Exists(ffmpeg) && File.Exists(ffprobe), "FFmpeg and ffprobe must be packaged before testing.");
            work = Path.Combine(project, "tests", "out", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(work);
            string output = Path.Combine(work, "결과 & output (테스트)");
            Directory.CreateDirectory(output);
            engine = new ConversionEngine(ffmpeg, ffprobe);

            string original16 = Path.Combine(work, "원본 sample & 괄호 (한글).wav");
            string original24 = Path.Combine(work, "24 bit 96k 원본.wav");
            string original441 = Path.Combine(work, "lossless 44100.wav");
            string title = "테스트 \"Title\" & 123";
            Generate(original16, 48000, 2, "pcm_s16le", title);
            Generate(original24, 96000, 2, "pcm_s24le", "24 bit source");
            Generate(original441, 44100, 2, "pcm_s16le", "lossless source");
            Dictionary<string, string> originals = new Dictionary<string, string>();
            originals[original16] = Hash(original16);
            originals[original24] = Hash(original24);
            originals[original441] = Hash(original441);

            Run("Probe reports source duration, channels and codec", delegate
            {
                AudioInfo info = engine.Probe(original16, None);
                Assert(info.DurationSeconds > 1.8 && info.DurationSeconds < 2.2, "Unexpected duration: " + info.DurationSeconds);
                Assert(info.Channels == 2, "Expected stereo source.");
                Assert(info.BitsPerSample == 16, "Expected 16-bit source.");
                Assert(info.Codec == "pcm_s16le", "Unexpected input codec: " + info.Codec);
            });

            string mp3 = Path.Combine(output, "한글 & MP3 (320).mp3");
            Run("MP3 output is 320 kbps, 44.1 kHz and stereo", delegate
            {
                double lastProgress = -1;
                int updates = 0;
                engine.Convert(original16, mp3, AudioTarget.Mp3, engine.Probe(original16, None), delegate(double value)
                {
                    Assert(!Double.IsNaN(value) && value >= 0 && value <= 1, "Invalid progress value: " + value);
                    Assert(value >= lastProgress, "Progress moved backwards.");
                    lastProgress = value;
                    updates++;
                }, None);
                Dictionary<string, object> audio = AudioStream(mp3);
                Equal("mp3", Text(audio, "codec_name"), "MP3 codec");
                Equal("44100", Text(audio, "sample_rate"), "MP3 sample rate");
                Equal("320000", Text(audio, "bit_rate"), "MP3 bitrate");
                Equal("2", Text(audio, "channels"), "MP3 channels");
                Assert(File.Exists(mp3) && new FileInfo(mp3).Length > 1000, "Missing or empty MP3.");
                Assert(updates > 0 && lastProgress == 1, "Progress did not report completed output.");
            });

            Run("Unicode filenames and quoted title metadata survive conversion", delegate
            {
                Assert(File.Exists(mp3), "Unicode output path was not created.");
                Dictionary<string, object> metadata = ProbeJson(mp3);
                Dictionary<string, object> format = (Dictionary<string, object>)metadata["format"];
                Dictionary<string, object> tags = (Dictionary<string, object>)format["tags"];
                Equal(title, Text(tags, "title"), "MP3 title");
            });

            Run("Ogg stream title metadata is retained in MP3 and FLAC", delegate
            {
                string ogg = Path.Combine(work, "Ogg stream tags.ogg");
                ProcessOk(ffmpeg, "-hide_banner", "-v", "error", "-nostdin", "-i", original16, "-c:a", "libvorbis", "-metadata", "title=" + title, "-y", ogg);
                originals[ogg] = Hash(ogg);
                foreach (AudioTarget target in new AudioTarget[] { AudioTarget.Mp3, AudioTarget.Flac })
                {
                    string path = Path.Combine(output, target == AudioTarget.Mp3 ? "from-ogg.mp3" : "from-ogg.flac");
                    engine.Convert(ogg, path, target, engine.Probe(ogg, None), null, None);
                    Dictionary<string, object> document = ProbeJson(path);
                    Dictionary<string, object> format = (Dictionary<string, object>)document["format"];
                    Dictionary<string, object> tags = (Dictionary<string, object>)format["tags"];
                    Equal(title, Text(tags, "title"), target + " title from Ogg stream");
                }
            });

            string flac16 = Path.Combine(output, "16bit.flac");
            Run("FLAC resamples to 44.1 kHz and preserves 16-bit depth", delegate
            {
                engine.Convert(original16, flac16, AudioTarget.Flac, engine.Probe(original16, None), null, None);
                Dictionary<string, object> audio = AudioStream(flac16);
                Equal("flac", Text(audio, "codec_name"), "FLAC codec");
                Equal("44100", Text(audio, "sample_rate"), "FLAC sample rate");
                Equal("16", Text(audio, "bits_per_raw_sample"), "FLAC bit depth");
                Equal("2", Text(audio, "channels"), "FLAC channels");
            });

            Run("FLAC resamples 96 kHz source and preserves 24-bit depth", delegate
            {
                string path = Path.Combine(output, "24bit.flac");
                engine.Convert(original24, path, AudioTarget.Flac, engine.Probe(original24, None), null, None);
                Dictionary<string, object> audio = AudioStream(path);
                Equal("44100", Text(audio, "sample_rate"), "24-bit FLAC sample rate");
                Equal("24", Text(audio, "bits_per_raw_sample"), "24-bit FLAC bit depth");
            });

            Run("44.1 kHz PCM to FLAC round trip preserves every audio sample", delegate
            {
                string path = Path.Combine(output, "lossless.flac");
                engine.Convert(original441, path, AudioTarget.Flac, engine.Probe(original441, None), null, None);
                string before = Path.Combine(work, "before.pcm");
                string after = Path.Combine(work, "after.pcm");
                DecodePcm(original441, before);
                DecodePcm(path, after);
                Equal(Hash(before), Hash(after), "Decoded audio SHA256");
            });

            Run("UniqueOutputPath avoids existing files and repeated collisions", delegate
            {
                string folder = Path.Combine(work, "collisions");
                Directory.CreateDirectory(folder);
                string first = ConversionEngine.UniqueOutputPath(folder, original16, AudioTarget.Mp3);
                Equal(".mp3", Path.GetExtension(first).ToLowerInvariant(), "MP3 extension");
                Equal(Path.GetFullPath(folder), Path.GetDirectoryName(Path.GetFullPath(first)), "Output directory");
                File.WriteAllText(first, "original sentinel", Encoding.UTF8);
                string second = ConversionEngine.UniqueOutputPath(folder, original16, AudioTarget.Mp3);
                Assert(!String.Equals(first, second, StringComparison.OrdinalIgnoreCase), "First collision was not avoided.");
                File.WriteAllText(second, "second sentinel", Encoding.UTF8);
                string third = ConversionEngine.UniqueOutputPath(folder, original16, AudioTarget.Mp3);
                Assert(!String.Equals(first, third, StringComparison.OrdinalIgnoreCase) && !String.Equals(second, third, StringComparison.OrdinalIgnoreCase), "Repeated collision was not avoided.");
                Equal("original sentinel", File.ReadAllText(first, Encoding.UTF8), "Existing output contents");
            });

            Run("Existing output cannot be overwritten by conversion", delegate
            {
                string path = Path.Combine(output, "existing.mp3");
                File.WriteAllText(path, "keep this output", Encoding.UTF8);
                string oldHash = Hash(path);
                ExpectFailure(delegate { engine.Convert(original16, path, AudioTarget.Mp3, engine.Probe(original16, None), null, None); });
                Equal(oldHash, Hash(path), "Existing output SHA256");
            });

            Run("Source cannot be used as its own output", delegate
            {
                string oldHash = Hash(original16);
                ExpectFailure(delegate { engine.Convert(original16, original16, AudioTarget.Flac, engine.Probe(original16, None), null, None); });
                Equal(oldHash, Hash(original16), "Source SHA256 after rejected self overwrite");
            });

            Run("Corrupt input fails without leaving an output", delegate
            {
                string bad = Path.Combine(work, "손상된 파일.wav");
                File.WriteAllText(bad, "This is not an audio file.", Encoding.UTF8);
                string path = Path.Combine(output, "corrupt.mp3");
                ExpectFailure(delegate
                {
                    AudioInfo info = engine.Probe(bad, None);
                    engine.Convert(bad, path, AudioTarget.Mp3, info, null, None);
                });
                Assert(!File.Exists(path), "Corrupt input left an output file.");
            });

            Run("Pre-cancelled probe and conversion honor cancellation", delegate
            {
                using (CancellationTokenSource source = new CancellationTokenSource())
                {
                    source.Cancel();
                    ExpectCancellation(delegate { engine.Probe(original16, source.Token); });
                    string folder = Path.Combine(work, "cancel-before");
                    Directory.CreateDirectory(folder);
                    string path = Path.Combine(folder, "cancelled.mp3");
                    ExpectCancellation(delegate { engine.Convert(original16, path, AudioTarget.Mp3, engine.Probe(original16, None), null, source.Token); });
                    Assert(Directory.GetFiles(folder).Length == 0, "Pre-cancellation left temporary files.");
                }
            });

            Run("In-flight cancellation stops encoding and removes incomplete files", delegate
            {
                string slow = Path.Combine(work, "long silence.flac");
                ProcessOk(ffmpeg, "-hide_banner", "-v", "error", "-nostdin", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "600", "-c:a", "flac", "-y", slow);
                originals[slow] = Hash(slow);
                AudioInfo info = engine.Probe(slow, None);
                string folder = Path.Combine(work, "cancel-during");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "cancelled.mp3");
                using (CancellationTokenSource source = new CancellationTokenSource())
                {
                    Exception observed = null;
                    Thread worker = new Thread(delegate()
                    {
                        try { engine.Convert(slow, path, AudioTarget.Mp3, info, null, source.Token); }
                        catch (Exception ex) { observed = ex; }
                    });
                    worker.IsBackground = true;
                    worker.Start();
                    Assert(!worker.Join(120), "Long fixture completed before cancellation could be exercised.");
                    source.Cancel();
                    Assert(worker.Join(15000), "Cancellation did not stop conversion within 15 seconds.");
                    Assert(observed is OperationCanceledException, "Expected cancellation exception; received: " + (observed == null ? "success" : observed.GetType().Name + ": " + observed.Message));
                    Assert(Directory.GetFiles(folder).Length == 0, "Cancellation left incomplete or temporary output files.");
                }
            });

            Run("All original source files remain byte-for-byte unchanged", delegate
            {
                foreach (KeyValuePair<string, string> original in originals)
                    Equal(original.Value, Hash(original.Key), "Original SHA256: " + Path.GetFileName(original.Key));
            });

            Console.WriteLine();
            Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed.");
            Console.WriteLine("Artifacts: " + work);
            return failed == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SETUP ERROR: " + ex);
            return 2;
        }
    }

    private static void Run(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + "\n  " + ex); }
    }

    private static void Generate(string path, int rate, int channels, string codec, string title)
    {
        ProcessOk(ffmpeg, "-hide_banner", "-v", "error", "-nostdin", "-f", "lavfi", "-i", "sine=frequency=997:sample_rate=" + rate + ":duration=2", "-ac", channels.ToString(CultureInfo.InvariantCulture), "-c:a", codec, "-metadata", "title=" + title, "-y", path);
    }

    private static void DecodePcm(string path, string output)
    {
        ProcessOk(ffmpeg, "-hide_banner", "-v", "error", "-nostdin", "-i", path, "-map", "0:a:0", "-c:a", "pcm_s16le", "-f", "s16le", "-y", output);
    }

    private static Dictionary<string, object> ProbeJson(string path)
    {
        return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(ProcessOk(ffprobe, "-v", "error", "-show_streams", "-show_format", "-of", "json", path));
    }

    private static Dictionary<string, object> AudioStream(string path)
    {
        IEnumerable streams = (IEnumerable)ProbeJson(path)["streams"];
        foreach (object item in streams)
        {
            Dictionary<string, object> stream = (Dictionary<string, object>)item;
            if (Text(stream, "codec_type") == "audio") return stream;
        }
        throw new Exception("No audio stream in " + path);
    }

    private static string Text(Dictionary<string, object> value, string key)
    {
        object result;
        return value.TryGetValue(key, out result) ? Convert.ToString(result, CultureInfo.InvariantCulture) : "";
    }

    private static void ExpectFailure(Action action)
    {
        bool failedAsExpected = false;
        try { action(); }
        catch (Exception ex) { if (ex is OperationCanceledException) throw; failedAsExpected = true; }
        Assert(failedAsExpected, "Expected operation to fail.");
    }

    private static void ExpectCancellation(Action action)
    {
        bool cancelled = false;
        try { action(); }
        catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled, "Operation did not honor cancellation.");
    }

    private static string Hash(string path)
    {
        using (SHA256 hash = SHA256.Create())
        using (FileStream stream = File.OpenRead(path))
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
    }

    private static void Equal(string expected, string actual, string description)
    {
        Assert(expected == actual, description + ": expected [" + expected + "], received [" + actual + "]");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static string ProcessOk(string executable, params string[] args)
    {
        StringBuilder arguments = new StringBuilder();
        foreach (string arg in args)
        {
            if (arguments.Length > 0) arguments.Append(' ');
            arguments.Append(Quote(arg));
        }
        ProcessStartInfo start = new ProcessStartInfo(executable, arguments.ToString());
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        using (Process process = Process.Start(start))
        {
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new Exception(Path.GetFileName(executable) + " exited " + process.ExitCode + ": " + stderr);
            return stdout;
        }
    }

    private static string Quote(string value)
    {
        StringBuilder result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '\"') { result.Append('\\', slashes * 2 + 1); result.Append(c); slashes = 0; continue; }
            result.Append('\\', slashes);
            result.Append(c);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        result.Append('\"');
        return result.ToString();
    }
}
