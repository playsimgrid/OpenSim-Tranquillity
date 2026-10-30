/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.IO;
using OpenSim.Server.Base.Hosting;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// Unit tests for <see cref="Log4NetBootstrapper"/>. Each server passes a bare
/// <c>OpenSim.Server.&lt;role&gt;.dll.config</c> as its default, so what matters here is which
/// directory a bare filename is resolved against. Every test runs with the working directory
/// moved to an empty temp directory, which is what makes the working-directory case and the
/// assembly-directory case distinguishable at all - run them with the working directory left
/// at the test output directory and the two branches coincide.
///
/// Moving the working directory makes this class part of the WorkingDirectorySensitive
/// collection: those classes capture the working directory in their constructors and restore
/// it when they dispose, so two of them running at once leaves one restoring a directory the
/// other has already deleted.
/// </summary>
[Collection("WorkingDirectorySensitive")]
public sealed class Log4NetBootstrapperTests : IDisposable
{
    private readonly string _basePath;
    private readonly string _workingDirectory;

    public Log4NetBootstrapperTests()
    {
        _workingDirectory = Directory.GetCurrentDirectory();
        _basePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_basePath);
        Directory.SetCurrentDirectory(_basePath);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_workingDirectory);
        Directory.Delete(_basePath, recursive: true);
    }

    [Fact]
    public void ResolveConfigPath_UsesConfiguredPath_WhenProvided()
    {
        var sut = new Log4NetBootstrapper();

        string result = sut.ResolveConfigPath("custom.config", "default.config");

        Assert.Equal("custom.config", result);
    }

    [Fact]
    public void ResolveConfigPath_FallsBackToDefault_WhenConfiguredPathMissing()
    {
        var sut = new Log4NetBootstrapper();

        string result = sut.ResolveConfigPath(string.Empty, "default.config");

        Assert.Equal("default.config", result);
    }

    [Fact]
    public void ResolveConfigPath_KeepsAbsolutePath_EvenWhenItDoesNotExist()
    {
        var sut = new Log4NetBootstrapper();
        string absent = Path.Combine(_basePath, "nowhere.config");

        string result = sut.ResolveConfigPath(absent, "default.config");

        // Handed back untouched rather than silently swapped, so that Configure can report the
        // path the operator actually asked for.
        Assert.Equal(absent, result);
    }

    [Fact]
    public void ResolveConfigPath_KeepsRelativePath_WhenWorkingDirectoryHoldsIt()
    {
        var sut = new Log4NetBootstrapper();
        File.WriteAllText(Path.Combine(_basePath, "here.config"), "<configuration/>");

        string result = sut.ResolveConfigPath(string.Empty, "here.config");

        Assert.Equal("here.config", result);
    }

    [Fact]
    public void ResolveConfigPath_ResolvesAgainstAssemblyDirectory_WhenWorkingDirectoryDoesNotHoldIt()
    {
        var sut = new Log4NetBootstrapper();
        string fileName = Path.GetRandomFileName() + ".config";
        string besideAssemblies = Path.Combine(AppContext.BaseDirectory, fileName);
        File.WriteAllText(besideAssemblies, "<configuration/>");

        try
        {
            string result = sut.ResolveConfigPath(string.Empty, fileName);

            // The case the servers actually hit: a bare default filename, and a working
            // directory that is not the application directory.
            Assert.Equal(besideAssemblies, result);
        }
        finally
        {
            File.Delete(besideAssemblies);
        }
    }

    [Fact]
    public void ResolveConfigPath_ResolvesConfiguredRelativePath_AgainstAssemblyDirectory()
    {
        var sut = new Log4NetBootstrapper();
        string fileName = Path.GetRandomFileName() + ".config";
        string besideAssemblies = Path.Combine(AppContext.BaseDirectory, fileName);
        File.WriteAllText(besideAssemblies, "<configuration/>");

        try
        {
            // An explicitly configured --logconfig gets the same treatment as the default; a
            // relative path there is no easier to resolve by hand.
            string result = sut.ResolveConfigPath(fileName, "default.config");

            Assert.Equal(besideAssemblies, result);
        }
        finally
        {
            File.Delete(besideAssemblies);
        }
    }

    [Fact]
    public void ResolveConfigPath_KeepsPathAsGiven_WhenItExistsNowhere()
    {
        var sut = new Log4NetBootstrapper();

        string result = sut.ResolveConfigPath(string.Empty, "absent-from-everywhere.config");

        Assert.Equal("absent-from-everywhere.config", result);
    }

    [Fact]
    public void Configure_WhenConfigResolvesNowhere_ReportsItAndReturnsThePathTried()
    {
        var sut = new Log4NetBootstrapper();
        TextWriter originalError = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);

        string result;
        try
        {
            // Must not throw: this runs before anything else in a server's startup, and the
            // resolved path is handed on to AddLog4Net afterwards either way.
            result = sut.Configure(string.Empty, "absent-from-everywhere.config");
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Equal("absent-from-everywhere.config", result);

        string reported = captured.ToString();
        Assert.Contains("absent-from-everywhere.config", reported);
        Assert.Contains("--logconfig", reported);
        Assert.Contains("working directory", reported);
    }
}
