using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace AudioConverter
{
    public enum AudioTarget
    {
        Mp3,
        Flac
    }

    public sealed class AudioInfo
    {
        public double DurationSeconds;
        public int Channels;
        public int BitsPerSample;
        public string Codec;
    }

    /// <summary>Runs the bundled FFmpeg tools without invoking a shell.</summary>
    public sealed class ConversionEngine
    {
        private const int MaximumProbeCharacters = 1024 * 1024;
        private const int MaximumErrorCharacters = 16000;
        private readonly string ffmpegPath;
        private readonly string ffprobePath;

        public ConversionEngine(string ffmpegPath, string ffprobePath)
        {
            this.ffmpegPath = RequireTool(ffmpegPath, "FFmpeg");
            this.ffprobePath = RequireTool(ffprobePath, "FFprobe");
        }

        public AudioInfo Probe(string input, CancellationToken token)
        {
            input = RequireInputFile(input);
            token.ThrowIfCancellationRequested();
            List<string> arguments = new List<string>();
            arguments.AddRange(new string[] {
                "-v", "error", "-protocol_whitelist", "file",
                "-select_streams", "a:0", "-show_entries",
                "stream=codec_type,codec_name,sample_fmt,bits_per_sample,bits_per_raw_sample,channels,duration:format=duration,format_name",
                "-of", "json", input
            });

            StringBuilder json = new StringBuilder();
            RunProcess(ffprobePath, arguments, delegate(string line)
            {
                if (json.Length + line.Length + 1 > MaximumProbeCharacters)
                    throw new InvalidDataException("The audio information returned by FFprobe is too large.");
                json.AppendLine(line);
            }, token);
            token.ThrowIfCancellationRequested();

            IDictionary<string, object> document;
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = MaximumProbeCharacters;
                document = serializer.DeserializeObject(json.ToString()) as IDictionary<string, object>;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Could not read the audio information returned by FFprobe.", ex);
            }
            if (document == null)
                throw new InvalidDataException("FFprobe returned no audio information.");

            IDictionary<string, object> format = GetDictionary(document, "format");
            if (IsPlaylistFormat(GetString(format, "format_name")))
                throw new InvalidDataException("Playlists and streaming manifests are not supported. Select an audio file.");

            IDictionary<string, object> stream = FirstDictionary(GetValue(document, "streams"));
            if (stream == null || !String.Equals(GetString(stream, "codec_type"), "audio", StringComparison.Ordinal))
                throw new InvalidDataException("This file does not contain a supported audio stream.");

            AudioInfo result = new AudioInfo();
            result.Codec = GetString(stream, "codec_name");
            result.Channels = GetInt(stream, "channels");
            if (String.IsNullOrEmpty(result.Codec) || result.Channels < 1)
                throw new InvalidDataException("The file has no decodable audio stream.");
            result.DurationSeconds = GetDouble(stream, "duration");
            if (result.DurationSeconds <= 0 || Double.IsNaN(result.DurationSeconds) || Double.IsInfinity(result.DurationSeconds))
                result.DurationSeconds = GetDouble(format, "duration");
            if (Double.IsNaN(result.DurationSeconds) || Double.IsInfinity(result.DurationSeconds) || result.DurationSeconds < 0)
                result.DurationSeconds = 0;

            int rawBits = GetInt(stream, "bits_per_raw_sample");
            int bits = rawBits > 0 ? rawBits : GetInt(stream, "bits_per_sample");
            string sampleFormat = GetString(stream, "sample_fmt");
            bool floatingPoint = sampleFormat.StartsWith("flt", StringComparison.Ordinal)
                || sampleFormat.StartsWith("dbl", StringComparison.Ordinal);
            if (bits == 0)
            {
                if (sampleFormat.StartsWith("s32", StringComparison.Ordinal) || sampleFormat.StartsWith("s64", StringComparison.Ordinal))
                    bits = 24;
                else
                    bits = 16;
            }
            result.BitsPerSample = floatingPoint || bits > 16 ? 24 : 16;
            return result;
        }

        public void Convert(string input, string output, AudioTarget target, AudioInfo info,
            Action<double> progress, CancellationToken token)
        {
            input = RequireInputFile(input);
            if (String.IsNullOrWhiteSpace(output))
                throw new ArgumentException("An output file is required.", "output");
            output = Path.GetFullPath(output);
            if (String.Equals(input, output, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The output file cannot be the original file.");
            if (File.Exists(output) || Directory.Exists(output))
                throw new IOException("The output already exists. Choose a different name.");
            string folder = Path.GetDirectoryName(output);
            if (String.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                throw new DirectoryNotFoundException("The output folder does not exist.");
            if (target != AudioTarget.Mp3 && target != AudioTarget.Flac)
                throw new ArgumentOutOfRangeException("target");
            token.ThrowIfCancellationRequested();
            if (info == null)
                info = Probe(input, token);

            // A staging file in the destination directory makes the final rename atomic.
            string staging = Path.Combine(folder, ".audio-converter-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                List<string> arguments = new List<string>();
                arguments.AddRange(new string[] {
                    "-hide_banner", "-nostdin", "-v", "error", "-xerror", "-n",
                    "-protocol_whitelist", "file", "-i", input,
                    "-map", "0:a:0", "-vn", "-sn", "-dn",
                    "-map_metadata", "0", "-map_metadata", "0:s:a:0",
                    "-map_metadata:s:a:0", "0:s:a:0",
                    "-ar", "44100"
                });
                if (target == AudioTarget.Mp3)
                {
                    arguments.AddRange(new string[] {
                        "-c:a", "libmp3lame", "-b:a", "320k", "-compression_level", "0",
                        "-ac", info.Channels == 1 ? "1" : "2",
                        "-id3v2_version", "3", "-write_xing", "1", "-f", "mp3"
                    });
                }
                else
                {
                    arguments.AddRange(new string[] {
                        "-c:a", "flac", "-compression_level", "8",
                        "-sample_fmt", info.BitsPerSample > 16 ? "s32" : "s16",
                        "-bits_per_raw_sample", info.BitsPerSample > 16 ? "24" : "16",
                        "-f", "flac"
                    });
                }
                arguments.AddRange(new string[] { "-nostats", "-progress", "pipe:1", staging });
                if (progress != null)
                    progress(0);
                double lastProgress = 0;
                RunProcess(ffmpegPath, arguments, delegate(string line)
                {
                    const string prefix = "out_time_us=";
                    if (progress == null || info.DurationSeconds <= 0 || !line.StartsWith(prefix, StringComparison.Ordinal))
                        return;
                    double microseconds;
                    if (!Double.TryParse(line.Substring(prefix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out microseconds)
                        || Double.IsNaN(microseconds) || Double.IsInfinity(microseconds))
                        return;
                    double value = Math.Max(0, Math.Min(0.999, microseconds / 1000000.0 / info.DurationSeconds));
                    if (value > lastProgress)
                    {
                        lastProgress = value;
                        progress(value);
                    }
                }, token);
                token.ThrowIfCancellationRequested();
                FileInfo produced = new FileInfo(staging);
                if (!produced.Exists || produced.Length == 0)
                    throw new InvalidDataException("FFmpeg did not produce a valid output file.");
                // File.Move never replaces an existing destination, including a file created meanwhile.
                File.Move(staging, output);
                if (progress != null)
                    progress(1);
            }
            finally
            {
                try
                {
                    if (File.Exists(staging))
                        File.Delete(staging);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        public static string UniqueOutputPath(string folder, string input, AudioTarget target)
        {
            if (String.IsNullOrWhiteSpace(folder))
                throw new ArgumentException("An output folder is required.", "folder");
            if (String.IsNullOrWhiteSpace(input))
                throw new ArgumentException("An input filename is required.", "input");
            if (target != AudioTarget.Mp3 && target != AudioTarget.Flac)
                throw new ArgumentOutOfRangeException("target");
            folder = Path.GetFullPath(folder);
            string basename = Path.GetFileNameWithoutExtension(input);
            if (String.IsNullOrWhiteSpace(basename))
                basename = "Audio";
            string extension = target == AudioTarget.Mp3 ? ".mp3" : ".flac";
            string candidate = Path.Combine(folder, basename + extension);
            int number = 2;
            while (File.Exists(candidate) || Directory.Exists(candidate))
            {
                candidate = Path.Combine(folder, basename + " (" + number.ToString(CultureInfo.InvariantCulture) + ")" + extension);
                number = checked(number + 1);
            }
            return candidate;
        }

        private static string RequireTool(string path, string name)
        {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentException(name + " executable path is required.", "path");
            path = Path.GetFullPath(path);
            if (!File.Exists(path))
                throw new FileNotFoundException(name + " executable was not found.", path);
            return path;
        }

        private static string RequireInputFile(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentException("An input audio file is required.", "input");
            path = Path.GetFullPath(path);
            if (!File.Exists(path))
                throw new FileNotFoundException("The input audio file does not exist.", path);
            string extension = Path.GetExtension(path).ToLowerInvariant();
            switch (extension)
            {
                case ".m3u": case ".m3u8": case ".pls": case ".ffconcat":
                case ".concat": case ".cue": case ".sdp": case ".mpd":
                case ".ism": case ".asx": case ".wax": case ".wvx":
                case ".xspf": case ".ram":
                    throw new InvalidDataException("Playlists and streaming manifests are not supported. Select an audio file.");
            }
            // Also block common manifests renamed to an audio extension before FFprobe opens them.
            byte[] prefix = new byte[8192];
            int read;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                read = stream.Read(prefix, 0, prefix.Length);
            string header = Encoding.UTF8.GetString(prefix, 0, read).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            if (header.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase)
                || header.StartsWith("ffconcat version", StringComparison.OrdinalIgnoreCase)
                || header.StartsWith("[playlist]", StringComparison.OrdinalIgnoreCase)
                || header.StartsWith("<asx", StringComparison.OrdinalIgnoreCase)
                || header.StartsWith("<MPD", StringComparison.OrdinalIgnoreCase)
                || (header.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
                    && (header.IndexOf("<MPD", StringComparison.OrdinalIgnoreCase) >= 0
                        || header.IndexOf("<playlist", StringComparison.OrdinalIgnoreCase) >= 0
                        || header.IndexOf("<asx", StringComparison.OrdinalIgnoreCase) >= 0)))
                throw new InvalidDataException("Playlists and streaming manifests are not supported. Select an audio file.");
            return path;
        }

        private static bool IsPlaylistFormat(string format)
        {
            foreach (string part in format.Split(','))
            {
                if (part == "hls" || part == "concat" || part == "dash" || part == "sdp" || part == "applehttp")
                    return true;
            }
            return false;
        }

        private static void RunProcess(string executable, IList<string> arguments,
            Action<string> outputLine, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            StringBuilder commandLine = new StringBuilder();
            foreach (string argument in arguments)
            {
                if (commandLine.Length > 0)
                    commandLine.Append(' ');
                commandLine.Append(QuoteArgument(argument));
            }
            StringBuilder errors = new StringBuilder();
            object errorLock = new object();
            object callbackLock = new object();
            Exception callbackException = null;
            using (Process process = new Process())
            {
                process.StartInfo = new ProcessStartInfo {
                    FileName = executable,
                    Arguments = commandLine.ToString(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false)
                };
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null || outputLine == null)
                        return;
                    try
                    {
                        outputLine(e.Data);
                    }
                    catch (Exception ex)
                    {
                        lock (callbackLock)
                        {
                            if (callbackException == null)
                                callbackException = ex;
                        }
                        TryKill(process);
                    }
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null)
                        return;
                    lock (errorLock)
                    {
                        // Keep the useful tail without accumulating FFmpeg's entire diagnostic output.
                        if (e.Data.Length >= MaximumErrorCharacters)
                        {
                            errors.Length = 0;
                            errors.Append(e.Data.Substring(e.Data.Length - MaximumErrorCharacters));
                        }
                        else
                        {
                            int excess = errors.Length + e.Data.Length + Environment.NewLine.Length - MaximumErrorCharacters;
                            if (excess > 0)
                                errors.Remove(0, Math.Min(excess, errors.Length));
                            errors.AppendLine(e.Data);
                        }
                    }
                };
                try
                {
                    if (!process.Start())
                        throw new IOException("Could not start " + Path.GetFileName(executable) + ".");
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    using (token.Register(delegate { TryKill(process); }))
                    {
                        while (!process.WaitForExit(100))
                            token.ThrowIfCancellationRequested();
                        // The parameterless overload also waits for asynchronous pipe readers.
                        process.WaitForExit();
                        token.ThrowIfCancellationRequested();
                        lock (callbackLock)
                        {
                            if (callbackException != null)
                                throw new IOException("Could not process the conversion progress or audio information.", callbackException);
                        }
                        if (process.ExitCode != 0)
                        {
                            string detail;
                            lock (errorLock)
                                detail = errors.ToString().Trim();
                            if (String.IsNullOrEmpty(detail))
                                detail = "The file could not be decoded or converted.";
                            throw new InvalidDataException(Path.GetFileNameWithoutExtension(executable) + " failed: " + detail);
                        }
                    }
                }
                catch
                {
                    TryKill(process);
                    try { process.WaitForExit(); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                    token.ThrowIfCancellationRequested();
                    throw;
                }
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        // The Windows CRT quoting rules: double backslashes before a quote or closing quote.
        private static string QuoteArgument(string argument)
        {
            StringBuilder result = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char character in argument)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (character == '"')
                {
                    result.Append('\\', backslashes * 2 + 1);
                    result.Append('"');
                    backslashes = 0;
                    continue;
                }
                result.Append('\\', backslashes);
                backslashes = 0;
                result.Append(character);
            }
            result.Append('\\', backslashes * 2);
            result.Append('"');
            return result.ToString();
        }

        private static object GetValue(IDictionary<string, object> dictionary, string key)
        {
            object value;
            return dictionary != null && dictionary.TryGetValue(key, out value) ? value : null;
        }

        private static string GetString(IDictionary<string, object> dictionary, string key)
        {
            object value = GetValue(dictionary, key);
            return value == null ? String.Empty : System.Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static int GetInt(IDictionary<string, object> dictionary, string key)
        {
            int value;
            return Int32.TryParse(GetString(dictionary, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        private static double GetDouble(IDictionary<string, object> dictionary, string key)
        {
            double value;
            return Double.TryParse(GetString(dictionary, key), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        private static IDictionary<string, object> GetDictionary(IDictionary<string, object> dictionary, string key)
        {
            return GetValue(dictionary, key) as IDictionary<string, object>;
        }

        private static IDictionary<string, object> FirstDictionary(object value)
        {
            IEnumerable sequence = value as IEnumerable;
            if (sequence == null)
                return null;
            foreach (object entry in sequence)
                return entry as IDictionary<string, object>;
            return null;
        }
    }
}
