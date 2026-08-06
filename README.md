# Proxmea.ILoggerN

## Overview

Proxmea.ILoggerN provides a shared, configurable logging setup for .NET 8 applications using NLog, with a focus on minimal code and settings inside your project to get up running. 
It enables consistent, centralized logging across your application, supporting both console, file and Elastic outputs by default. 
It also adds missing functionality to be able to log individual properties. 
The package is designed for easy integration with ASP.NET Core projects and leverages dependency injection for logger access.

It's utilizing NLog as the logging provider, mainly because of its high performance.

## What It Does

- Sets up NLog as the logging provider for your .NET application. It comes with default well ironed-out layouts. 
- ILogger is missing `.WithProperty`, so we've implemented that here. Easy peasy to use.
- Merges default logging configuration with your environment-specific `appsettings.[environment].json`. This effectivly makes your NLog section very small.
- Allows you to retrieve and use loggers anywhere in your app, including static contexts.
- Supports logging to both console, file and Elastic by default, with configuration overrides per environment.
- Provides helpers for adding custom properties to log entries (e.g., application version).
- Keeps rolled log files compressed, which NLog itself no longer does. See [Archive compression](#archive-compression).
- All NLog settings are overridable in your own appsettings.json

## Usage

1. **Add Controllers and Logging:**
   In your `Program.cs`, add controllers and configure logging:
```
var builder = WebApplication.CreateBuilder(args); 
SharedLogging.ConfigureNLog(builder); 
var app = builder.Build();
```


2. **Initialize Logger in Static Contexts:**
```
ServicesHelper.Configure(app.Services); 
var logger = LoggerHelper.GetLogger<Program>(); 
logger.LogInformation("Starting");
```


3. **Log with Custom Properties:**
```
logger 
  .WithProperty("Version", System.Reflection.Assembly.GetExecutingAssembly().GetName().Version) 
  .LogInformation("Application started with properties.");
```


4. **Controller Logging (Dependency Injection):**
   In your controller, inject `ILogger<T>` and use as needed:
```
public class HelloWorldController : Controller 
{ 
	private readonly ILogger<HelloWorldController> _logger; 

	public HelloWorldController(ILogger<HelloWorldController> logger) 
	{ 
		_logger = logger; 
	}
	
	[HttpGet] 
	public IActionResult GetHello() { 
		_logger.LogInformation("GetHello called."); 
		return Ok("HelloBack"); 
	} 
}
```


5. **Configuration:**
   - Default NLog settings are in `Proxmea.ILoggerN.Default.AppSettings.json`.
	
	 This serves as boilerplate config for NLog.
   - Override or extend logging in your own `appsettings.[environment].json`.

     This is where you'd put your application specific minimal NLog config.

## Archive compression

The default file target rolls over daily and keeps the rolled files compressed:

```
"file": {
  "type": "File",
  "fileName": "${basedir}/logs/app.log",
  "archiveFileName": "${basedir}/logs/archive/app.{#}.log.zip",
  "enableArchiveFileCompression": true
}
```

NLog v6 removed `enableArchiveFileCompression` from its file target, and because unknown properties are ignored, 
configurations using it kept rolling over but stopped compressing - typically leaving plain text files named `.zip`. 
Proxmea.ILoggerN handles the option itself, so the setting means what it says again. Nothing changes on your side; 
keep configuring it exactly as before.

Worth knowing:

- Compression happens shortly after a file is rolled, not during rollover, so logging is never held up by it.
- Archives keep the name and timestamps NLog gave them, which leaves `maxArchiveFiles` and `maxArchiveDays` retention working.
- `archiveFileName` decides the format: `.zip` produces a zip, `.gz` a gzip. Any other extension is left uncompressed, 
  since compressing would mean renaming the file out of reach of NLog's retention. A warning is logged if that happens.
- Several applications may share one archive directory; they will not compress each other's files twice or corrupt them.
- Archives that earlier versions left uncompressed are picked up and compressed on the first sweep after the upgrade.
- Set `"enableArchiveFileCompression": false` in your own appsettings to turn it off.

## Sample output from `HelloWorld`
```
2025-06-11 18:41:54.2293 HelloWorld  INFO HelloWorld.Program Starting
2025-06-11 18:41:54.2526 HelloWorld  INFO HelloWorld.Program Environment: Production
2025-06-11 18:41:54.2553 HelloWorld  INFO HelloWorld.Program Application started with properties.  Version=1.0.73.25162
2025-06-11 18:41:58.5665 HelloWorld  INFO HelloWorld.Controllers.HelloWorldController GetHello called.  ConnectionId=0HND90RH1Q3UT, RequestId=0HND90RH1Q3UT:00000001, RequestPath=/HelloWorld, ActionId=d60d08d9-a508-4200-94ee-84a09af2dfd1, ActionName=HelloWorld.Controllers.HelloWorldController.GetHello (HelloWorld)
```

# Changelog
| Version | Description |
|---------|-------------|
| v1.2.0.26218 (2026-08-06) | Fixed `enableArchiveFileCompression` silently doing nothing since the move to NLog v6, which left archives as plain text named `.zip`. The option is now handled by the package itself. |
| v1.1.5.25259 (2025-09-16)| Added default (optional) logging of unhandled exceptions. Changed to work with non-web apps. Updated NuGets. |
| v1.0.62.25174 (2025-06-23) | Changed Elastic layout to be async to prevent blocking the main thread on Elastic failure. Updated default console layout to colour output. |
| v1.0.49.251620 (2025-06-11) | Initial Release |

# 📜License 
This project is available under a **Mozilla Public License 2.0 (MPL-2.0)**:  
 
  - ✅ **You are free to use this project** (or snippets from it) in any application, including commercial ones.
  - ✅ **You must provide attribution** by linking back to this repository.
  - ✅ **If you modify and distribute this code**, you **must open-source** the modified files under the same license.
  - 🔄 **Forks and improvements are encouraged** – If you enhance this project, consider submitting a **pull request** so everyone benefits.  
  - 📖 Full license text: [MPL-2.0](https://choosealicense.com/licenses/mpl-2.0/)
	 
## 📢 Author
Developed and maintained by **jrnker**@Proxmea. For inquiries, issues, or contributions, check out the repository or open a pull request.  
