/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using log4net;
using log4net.Config;

namespace OpenSim.Server.Base.Hosting;

public sealed class Log4NetBootstrapper : ILog4NetBootstrapper
{
    public string LogPath { get; set; } = ".";

    /// <summary>
    /// Picks the configured path over the server default, and resolves a bare filename
    /// against the directory the assemblies live in when the working directory does not
    /// hold it.
    /// </summary>
    /// <remarks>
    /// Each server passes a bare "OpenSim.Server.&lt;role&gt;.dll.config" as its default, and a
    /// bare name is resolved against the process working directory - which is the application
    /// directory only by accident. Our containers set a working directory, so it has always
    /// worked there; a systemd unit without WorkingDirectory= resolves the name somewhere the
    /// file is not, log4net configures nothing, and no log file is written. Measured on
    /// playsim/moneyserver:1.0.49.9: the name resolves from the image's /money/bin and does
    /// not resolve from /.
    ///
    /// An absolute path is handed back untouched, including when it does not exist, so that
    /// Configure can report what the operator actually asked for rather than quietly
    /// substituting something else. The same fallback applies to an explicitly configured
    /// --logconfig, because a relative path there is no easier to resolve by hand.
    /// </remarks>
    public string ResolveConfigPath(string configuredPath, string defaultPath)
    {
        string candidate = string.IsNullOrWhiteSpace(configuredPath) ? defaultPath : configuredPath;

        if (string.IsNullOrWhiteSpace(candidate))
            return candidate;

        if (Path.IsPathRooted(candidate) || File.Exists(candidate))
            return candidate;

        string besideAssemblies = Path.Combine(AppContext.BaseDirectory, candidate);

        return File.Exists(besideAssemblies) ? besideAssemblies : candidate;
    }

    public string Configure(string configuredPath, string defaultPath)
    {
        if (string.IsNullOrWhiteSpace(LogPath) is false)
            log4net.GlobalContext.Properties["LogPath"] = LogPath;

        string configPath = ResolveConfigPath(configuredPath, defaultPath);

        // The caller also hands this path to AddLog4Net, so a path that resolves nowhere is
        // worth saying out loud here rather than leaving to whatever that provider does with
        // it. XmlConfigurator on a missing file is a no-op.
        if (string.IsNullOrWhiteSpace(configPath) || File.Exists(configPath) is false)
        {
            ReportMissingConfig(configPath);
            return configPath;
        }

        XmlConfigurator.Configure(new FileInfo(configPath));

        // Both of these are asked for often enough, and guessable from nothing else, that they
        // belong in the log the moment there is a log to put them in. LogPath defaults to the
        // working directory, so the log file is not necessarily beside the config file.
        //
        // Unrelated red herring, for whoever reads this next: a healthy server prints
        //   log4net:ERROR Xml element is -  - not a <log4net> element.
        // as its first line of stdout. That is log4net's default repository being touched
        // before this method runs, not a failure of this configuration - a server printing it
        // writes its log file perfectly well. Judge configuration by whether the .log file is
        // growing, never by that line.
        LogManager.GetLogger(typeof(Log4NetBootstrapper)).InfoFormat(
            "[LOG4NET]: configured from {0}, writing log files to {1}",
            Absolute(configPath), Absolute(LogPath));

        return configPath;
    }

    private static void ReportMissingConfig(string configPath)
    {
        string tried = string.IsNullOrWhiteSpace(configPath) ? "(no path given)" : Absolute(configPath);

        Console.Error.WriteLine($"log4net configuration not found at {tried}");
        Console.Error.WriteLine("  Logging will fall back to whatever the host provides; no log file is guaranteed.");
        Console.Error.WriteLine("  Fix it either way round:");
        Console.Error.WriteLine("    - set the service's working directory to the directory holding the assemblies, or");
        Console.Error.WriteLine("    - pass --logconfig with an absolute path to the config file.");
    }

    /// <summary>
    /// Absolute form of a path for display, falling back to the path as given.
    /// </summary>
    /// <remarks>
    /// GetFullPath throws on a malformed path, and LogPath arrives from the LOGDIR environment
    /// variable. Nothing here is worth failing a server's startup over.
    /// </remarks>
    private static string Absolute(string path)
    {
        try
        {
            return Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "." : path);
        }
        catch (Exception)
        {
            return path;
        }
    }
}
