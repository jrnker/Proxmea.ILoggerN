using System.IO.Compression;
using Newtonsoft.Json.Linq;
using NLog;
using NLog.Layouts;

namespace Proxmea.ILoggerN
{
    /// <summary>
    /// Keeps the <c>enableArchiveFileCompression</c> file target option alive on NLog v6.
    /// </summary>
    /// <remarks>
    /// NLog v6 removed <c>EnableArchiveFileCompression</c> (and the <c>FileCompressor</c> hook) from its file
    /// target. Configurations carrying the option still roll over, but the rolled files are left as plain text -
    /// and since the option is normally paired with an <c>archiveFileName</c> ending in <c>.zip</c>, the result is
    /// plain text wearing a <c>.zip</c> name. NLog silently ignores the unknown property, so nothing complains.
    ///
    /// This restores the behaviour without any change on the consuming side. The option is read out of the merged
    /// NLog configuration and removed from it (so NLog never sees an unknown property, which would throw for
    /// consumers running with <c>throwConfigExceptions: true</c>), and a low frequency background sweep compresses
    /// finished archive files in place. Names and timestamps are preserved, so NLog's own <c>maxArchiveFiles</c> and
    /// <c>maxArchiveDays</c> retention still recognises and prunes the files it created.
    /// </remarks>
    internal static class ArchiveFileCompression
    {
        private const string OptionName = "enableArchiveFileCompression";
        private const string WorkFileSuffix = ".compressing";
        private const string LoggerName = "Proxmea.ILoggerN.ArchiveFileCompression";

        /// <summary>How often finished archive files are looked for. A sweep is a directory listing, so this is cheap.</summary>
        private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

        /// <summary>Delay of the first sweep, to stay off the application startup path.</summary>
        private static readonly TimeSpan FirstSweepDelay = TimeSpan.FromSeconds(5);

        /// <summary>An archive file must be untouched for this long before it is considered finished.</summary>
        private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(15);

        /// <summary>Leftover work files from a killed process are cleaned up once they are this old.</summary>
        private static readonly TimeSpan WorkFileMaxAge = TimeSpan.FromHours(1);

        /// <summary>Linux and friends have case sensitive paths, Windows does not.</summary>
        private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        private static readonly object Sync = new();
        private static readonly HashSet<string> Warned = new(StringComparer.OrdinalIgnoreCase);

        private static Timer? _timer;
        private static Watch[] _watches = Array.Empty<Watch>();
        private static int _sweeping;

        private enum Format { Zip, GZip }

        /// <summary>A file target that asked for compression. Layouts are rendered per sweep, not cached, so
        /// paths built from layout renderers (<c>${basedir}</c>, <c>${processname}</c>, ...) stay correct.</summary>
        private sealed record Watch(Layout ArchiveFileName, Layout? FileName);

        /// <summary>
        /// Takes over the <c>enableArchiveFileCompression</c> option from the merged NLog configuration and starts
        /// the background sweep. Safe to call more than once; the previous sweep is replaced.
        /// </summary>
        /// <param name="mergedNLog">The merged <c>NLog</c> section. Handled properties are removed from it.</param>
        internal static void Initialize(JObject mergedNLog)
        {
            var watches = new List<Watch>();
            try
            {
                CollectAndStrip(mergedNLog, watches);
            }
            catch (Exception ex)
            {
                // A malformed config is NLog's problem to report, not a reason to fail startup here.
                NLog.Common.InternalLogger.Warn(ex, "Proxmea.ILoggerN: could not read {0} from the NLog config", OptionName);
                return;
            }

            lock (Sync)
            {
                _timer?.Dispose();
                _timer = null;
                _watches = watches.ToArray();

                if (_watches.Length == 0)
                    return;

                _timer = new Timer(_ => Sweep(), null, FirstSweepDelay, SweepInterval);
            }
        }

        /// <summary>Stops the background sweep. Any compression in flight is left to finish.</summary>
        internal static void Stop()
        {
            lock (Sync)
            {
                _timer?.Dispose();
                _timer = null;
                _watches = Array.Empty<Watch>();
            }
        }

        #region Configuration

        private static void CollectAndStrip(JToken token, List<Watch> watches)
        {
            switch (token)
            {
                case JObject obj:
                    // Only file targets are ours to handle. Other targets keeping the same option name (the legacy
                    // NLog.Targets.ConcurrentFile still implements it natively) are left untouched.
                    if (IsFileTarget(obj))
                    {
                        var option = obj.Property(OptionName, StringComparison.OrdinalIgnoreCase);
                        if (option != null)
                        {
                            obj.Remove(option.Name);
                            if (IsTrue(option.Value))
                                AddWatch(obj, watches);
                        }
                    }

                    foreach (var property in obj.Properties().ToList())
                        CollectAndStrip(property.Value, watches);
                    break;

                case JArray array:
                    foreach (var item in array.ToList())
                        CollectAndStrip(item, watches);
                    break;
            }
        }

        private static void AddWatch(JObject target, List<Watch> watches)
        {
            var fileName = GetString(target, "fileName");
            // Without an archiveFileName, NLog v6 derives the archive name from the log file name itself.
            var archiveFileName = GetString(target, "archiveFileName") ?? fileName;
            if (string.IsNullOrWhiteSpace(archiveFileName))
                return;

            watches.Add(new Watch(
                Layout.FromString(archiveFileName),
                string.IsNullOrWhiteSpace(fileName) ? null : Layout.FromString(fileName)));
        }

        private static bool IsFileTarget(JObject obj) =>
            string.Equals(GetString(obj, "type"), "File", StringComparison.OrdinalIgnoreCase);

        private static string? GetString(JObject obj, string name) =>
            obj.Property(name, StringComparison.OrdinalIgnoreCase)?.Value is JValue { Value: not null } value
                ? value.Value<string>()
                : null;

        private static bool IsTrue(JToken? token) => token switch
        {
            JValue { Value: bool flag } => flag,
            JValue { Value: string text } => bool.TryParse(text, out var parsed) && parsed,
            _ => false
        };

        #endregion

        #region Sweeping

        private static void Sweep()
        {
            // A sweep that runs long (a big archive on a slow disk) must not stack up behind itself.
            if (Interlocked.CompareExchange(ref _sweeping, 1, 0) != 0)
                return;

            try
            {
                var watches = _watches;

                // The log file currently being written is never an archive, whatever it is called.
                var liveFiles = new HashSet<string>(PathComparer);
                foreach (var watch in watches)
                {
                    var live = Render(watch.FileName);
                    if (live != null)
                        liveFiles.Add(live);
                }

                var swept = new HashSet<string>(PathComparer);
                foreach (var watch in watches)
                {
                    var archiveFileName = Render(watch.ArchiveFileName);
                    if (archiveFileName == null)
                        continue;

                    var directory = Path.GetDirectoryName(archiveFileName);
                    var extension = Path.GetExtension(archiveFileName);
                    if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                        continue;

                    var format = FormatFor(extension);
                    if (format == null)
                    {
                        // Compressing would mean renaming, and a renamed archive is one NLog's own retention no
                        // longer recognises - which would quietly turn maxArchiveFiles into unbounded growth.
                        WarnOnce(archiveFileName,
                            "Proxmea.ILoggerN: {0} is enabled but the archive file name '{1}' does not end in .zip or .gz, so archives are left uncompressed. Set archiveFileName to one of those extensions to enable compression.",
                            OptionName, archiveFileName);
                        continue;
                    }

                    // Several targets commonly share one archive directory. Sweep it once.
                    if (!swept.Add(directory + "|" + extension))
                        continue;

                    SweepDirectory(directory, extension, format.Value, liveFiles);
                }
            }
            catch (Exception ex)
            {
                NLog.Common.InternalLogger.Warn(ex, "Proxmea.ILoggerN: archive compression sweep failed");
            }
            finally
            {
                Interlocked.Exchange(ref _sweeping, 0);
            }
        }

        private static void SweepDirectory(string directory, string extension, Format format, HashSet<string> liveFiles)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*" + extension))
            {
                if (liveFiles.Contains(Path.GetFullPath(file)))
                    continue;

                TryCompress(file, format);
            }

            RemoveStaleWorkFiles(directory);
        }

        private static void TryCompress(string path, Format format)
        {
            string? workFile = null;
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length == 0)
                    return;

                // Still being written to, or only just handed over by NLog.
                if (DateTime.UtcNow - file.LastWriteTimeUtc < QuietPeriod)
                    return;

                if (IsCompressed(path) || IsLocked(path))
                    return;

                var lastWriteUtc = file.LastWriteTimeUtc;
                var creationUtc = file.CreationTimeUtc;
                var originalLength = file.Length;

                workFile = $"{path}.{Environment.ProcessId}{WorkFileSuffix}";
                CompressTo(path, workFile, format, lastWriteUtc);

                // Applications sharing an archive directory sweep it independently, and NLog may have appended to
                // the file after all. Only replace what was actually read.
                file.Refresh();
                if (!file.Exists || file.Length != originalLength || file.LastWriteTimeUtc != lastWriteUtc)
                    return;

                var compressedLength = new FileInfo(workFile).Length;

                File.SetLastWriteTimeUtc(workFile, lastWriteUtc);
                try { File.SetCreationTimeUtc(workFile, creationUtc); } catch { /* not supported everywhere */ }

                File.Move(workFile, path, overwrite: true);
                workFile = null;

                LogManager.GetLogger(LoggerName)
                    .Info("Compressed log archive {0} ({1} KB -> {2} KB)",
                        Path.GetFileName(path), originalLength / 1024, compressedLength / 1024);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another process got there first, or the file is still held. Next sweep picks it up.
                NLog.Common.InternalLogger.Debug(ex, "Proxmea.ILoggerN: deferring compression of {0}", path);
            }
            catch (Exception ex)
            {
                NLog.Common.InternalLogger.Warn(ex, "Proxmea.ILoggerN: could not compress {0}", path);
            }
            finally
            {
                if (workFile != null)
                    TryDelete(workFile);
            }
        }

        private static void CompressTo(string source, string destination, Format format, DateTime lastWriteUtc)
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);

            if (format == Format.GZip)
            {
                using var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
                input.CopyTo(gzip);
            }
            else
            {
                using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                var entry = zip.CreateEntry(EntryNameFor(source), CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(lastWriteUtc, TimeSpan.Zero);
                using var entryStream = entry.Open();
                input.CopyTo(entryStream);
            }
        }

        /// <summary>The archive file without its compression extension, which is what NLog v4 used to put in the zip.</summary>
        private static string EntryNameFor(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrEmpty(name) ? Path.GetFileName(path) : name;
        }

        private static void RemoveStaleWorkFiles(string directory)
        {
            foreach (var workFile in Directory.EnumerateFiles(directory, "*" + WorkFileSuffix))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(workFile) > WorkFileMaxAge)
                        File.Delete(workFile);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use by another process after all.
                }
            }
        }

        #endregion

        #region Helpers

        private static Format? FormatFor(string extension) => extension.ToLowerInvariant() switch
        {
            ".zip" => Format.Zip,
            ".gz" or ".gzip" => Format.GZip,
            _ => null
        };

        /// <summary>True if the file already carries a zip or gzip signature, so sweeps do not re-compress.</summary>
        private static bool IsCompressed(string path)
        {
            Span<byte> header = stackalloc byte[2];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Read(header) < 2)
                return false;

            return (header[0] == 'P' && header[1] == 'K')      // zip
                || (header[0] == 0x1F && header[1] == 0x8B);   // gzip
        }

        /// <summary>
        /// Best effort check for a file NLog still holds open. Reliable on Windows; on Unix, where locking is
        /// advisory, <see cref="QuietPeriod"/> is what keeps us off files still in use.
        /// </summary>
        private static bool IsLocked(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static string? Render(Layout? layout)
        {
            if (layout == null)
                return null;

            var rendered = layout.Render(LogEventInfo.CreateNullEvent());
            if (string.IsNullOrWhiteSpace(rendered))
                return null;

            try
            {
                return Path.GetFullPath(rendered);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }

        private static void WarnOnce(string key, string message, params object[] args)
        {
            lock (Warned)
            {
                if (!Warned.Add(key))
                    return;
            }

            LogManager.GetLogger(LoggerName).Warn(message, args);
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { /* cleaned up by RemoveStaleWorkFiles */ }
        }

        #endregion
    }
}
