using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using NLog;
using NLog.Web;
using System.Diagnostics;

namespace Proxmea.ILoggerN
{
    public static class SharedLogging
    {
        /// <summary>
        /// Configures NLog for the application by merging default and application-specific NLog configurations.
        /// </summary>
        /// <param name="builder">The <see cref="IHostApplicationBuilder"/> used to access configuration and logging services.</param>
        /// <param name="captureUnhandledExceptions">Control if the logger also handles UnhandledExceptions. Default True</param>
        public static void ConfigureNLog(IHostApplicationBuilder builder, bool captureUnhandledExceptions = true)
        {
            // Determine environment
            var env = builder.Configuration["AppSettings:Environment"]
                      ?? builder.Environment?.EnvironmentName
                      ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                      ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                      ?? "Development";

            GlobalDiagnosticsContext.Set("env", env.ToLower());

            #region Merge NLog config
            // Load shared NLog config
            var sharedNLogPath = Path.Combine(AppContext.BaseDirectory, "Proxmea.ILoggerN.Default.AppSettings.json");
            JObject sharedNLog = new JObject();
            if (File.Exists(sharedNLogPath))
            {
                var sharedConfigRoot = new ConfigurationBuilder()
                    .AddJsonFile(sharedNLogPath, optional: false, reloadOnChange: false)
                    .Build()
                    .GetSection("NLog");
                sharedNLog = sharedConfigRoot.Exists()
                    ? (JObject)ConfigSectionToJToken(sharedConfigRoot)
                    : new JObject();
            }

            // Load app NLog config (from already loaded builder.Configuration, e.g. appsettings.json)
            var appNLogSection = builder.Configuration.GetSection("NLog");
            JObject appNLog = appNLogSection.Exists()
                ? (JObject)ConfigSectionToJToken(appNLogSection)
                : new JObject();

            // Deep merge: app config wins
            var mergedNLog = sharedNLog != null
                ? (JObject)sharedNLog.DeepClone()
                : new JObject();

            if (appNLog != null)
                mergedNLog.Merge(appNLog, new JsonMergeSettings
                {
                    MergeArrayHandling = MergeArrayHandling.Replace,
                    MergeNullValueHandling = MergeNullValueHandling.Merge
                });

            // NLog v6 dropped file target compression, so we take the option over here. This also strips it from
            // the merged config, keeping NLog from tripping over an unknown property.
            ArchiveFileCompression.Initialize(mergedNLog);

            // Now, create a temp file for the merged config
            var mergedConfigPath = Path.Combine(Path.GetTempPath(), $"Proxmea.ILoggerN.Merged.{Guid.NewGuid()}.json");
            var mergedRoot = new JObject { ["NLog"] = mergedNLog };
            File.WriteAllText(mergedConfigPath, mergedRoot.ToString());
            #endregion

            // Add the merged config as a configuration source
            builder.Configuration.AddJsonFile(mergedConfigPath, optional: false, reloadOnChange: false);

            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                try { File.Delete(mergedConfigPath); } catch { }
            };

            // Handle unhandled exceptions
            if (captureUnhandledExceptions)
            {
                AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionTrapper;
                TaskScheduler.UnobservedTaskException += UnhandledTaskExceptionTrapper;
            }

            builder.Logging.ClearProviders();
            builder.UseNLog();

            // Shutdown nicely upon exit
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                ArchiveFileCompression.Stop();
                LogManager.Shutdown();
            };
        }
        private static void UnhandledTaskExceptionTrapper(object? sender, UnobservedTaskExceptionEventArgs e) =>
            UnhandledExceptionTrapper(sender, new UnhandledExceptionEventArgs(e.Exception, false));
        private static void UnhandledExceptionTrapper(object? sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            var categoryType = TryGetExceptionCategoryType(ex) ?? typeof(SharedLogging);
            var logger = LoggerHelper.GetLogger(categoryType);
            if (ex != null)
                logger?.LogError(ex, "Unhandled exception");
            else
                logger?.LogError("Unhandled exception: {ExceptionObject}", e.ExceptionObject);
        }
        // Prefer the actual throw site; fall back by scanning the stack for a user frame.
        private static Type? TryGetExceptionCategoryType(Exception? ex)
        {
            var target = ex?.TargetSite?.DeclaringType;
            if (target != null) return target;

            if (ex == null) return null;

            var st = new StackTrace(ex, fNeedFileInfo: false);
            for (int i = 0; i < st.FrameCount; i++)
            {
                var method = st.GetFrame(i)?.GetMethod();
                var dt = method?.DeclaringType;
                if (dt == null) continue;

                var ns = dt.Namespace ?? string.Empty;
                // Heuristic: skip framework/helpers/async state machines
                if (!ns.StartsWith("System", StringComparison.Ordinal) &&
                    !ns.StartsWith("Microsoft", StringComparison.Ordinal) &&
                    !dt.Name.Contains("ThrowHelper", StringComparison.Ordinal) &&
                    !dt.Name.Contains("MoveNext", StringComparison.Ordinal))
                {
                    return dt;
                }
            }

            return null;
        }
        private static JToken ConfigSectionToJToken(IConfigurationSection section)
        {
            var children = section.GetChildren().ToList();
            if (children.Count > 0)
            {
                if (children.All(c => int.TryParse(c.Key, out _)))
                {
                    var array = new JArray();
                    foreach (var child in children.OrderBy(c => int.Parse(c.Key)))
                        array.Add(ConfigSectionToJToken(child));
                    return array;
                }
                else
                {
                    var obj = new JObject();
                    foreach (var child in children)
                        obj[child.Key] = ConfigSectionToJToken(child);
                    return obj;
                }
            }
            // Otherwise, treat as value
            if (section.Value == null)
                return JValue.CreateNull();

            // Only parse as bool/int/double for known value types, not for keys like "type", "name", etc.
            var key = section.Path.Split(':').Last().ToLowerInvariant();
            if (key != "type" && key != "name" && key != "layout" && key != "assembly")
            {
                if (bool.TryParse(section.Value, out var boolVal))
                    return new JValue(boolVal);
                if (int.TryParse(section.Value, out var intVal))
                    return new JValue(intVal);
                if (double.TryParse(section.Value, out var doubleVal))
                    return new JValue(doubleVal);
            }

            return new JValue(section.Value);
        }
    }
}
