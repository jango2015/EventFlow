﻿// The MIT License (MIT)
//
// Copyright (c) 2015-2016 Rasmus Mikkelsen
// Copyright (c) 2015-2016 eBay Software Foundation
// https://github.com/rasmus/EventFlow
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of
// this software and associated documentation files (the "Software"), to deal in
// the Software without restriction, including without limitation the rights to
// use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
// the Software, and to permit persons to whom the Software is furnished to do so,
// subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
// FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
// COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
// IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
// CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Threading.Tasks;
using EventFlow.Core;
using EventFlow.EventStores.EventStore.Tests.Extensions;
using EventFlow.Extensions;

namespace EventFlow.TestHelpers
{
    public abstract class Runner
    {
        protected class SoftwareDescription
        {
            public Version Version { get; }
            public Uri DownloadUri { get; }

            public SoftwareDescription(
                Version version,
                Uri downloadUri)
            {
                Version = version;
                DownloadUri = downloadUri;
            }
        }

        protected abstract string SoftwareName { get; }
        protected abstract IEnumerable<SoftwareDescription> SoftwareDescriptions { get; }

        protected static IDisposable StartExe(
            string exePath,
            string initializationDone,
            params string[] arguments)
        {
            var process = new Process
                {
                    StartInfo = new ProcessStartInfo(exePath, string.Join(" ", arguments))
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        WorkingDirectory = Path.GetDirectoryName(exePath),
                    }
                };
            var exeName = Path.GetFileName(exePath);
            DataReceivedEventHandler outHandler = (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Console.WriteLine($"OUT - {exeName}: {e.Data}");
                    }
                };
            process.OutputDataReceived += outHandler;
            DataReceivedEventHandler errHandler = (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Console.WriteLine($"ERR - {exeName}: {e.Data}");
                    }
                };
            process.ErrorDataReceived += errHandler;
            Action<Process> initializeProcess = p =>
                {
                    Console.WriteLine($"{exeName} START =======================================");
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                };
            process.WaitForOutput(initializationDone, initializeProcess);

            return new DisposableAction(() =>
                {
                    try
                    {
                        process.OutputDataReceived -= outHandler;
                        process.ErrorDataReceived -= errHandler;

                        KillProcessAndChildren(process.Id);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"Failed to kill process: {e.Message}");
                    }
                    finally
                    {
                        process.DisposeSafe("Process");
                    }
                });
        }

        private static void KillProcessAndChildren(int pid)
        {
            var searcher = new ManagementObjectSearcher("Select * From Win32_Process Where ParentProcessID=" + pid);
            var moc = searcher.Get();
            foreach (var o in moc)
            {
                var mo = (ManagementObject) o;
                KillProcessAndChildren(Convert.ToInt32(mo["ProcessID"]));
            }
            try
            {
                var proc = Process.GetProcessById(pid);
                proc.Kill();
            }
            catch (ArgumentException)
            {
                // Process already exited.
            }
        }

        protected async Task<string> InstallAsync(Version version)
        {
            if (IsInstalled(version))
            {
                Console.WriteLine($"{SoftwareName} v'{version}' is already installed");
                return GetInstallPath(version);
            }

            Console.WriteLine($"{SoftwareName} v{version} not installed, installing it");

            var tempDownload = Path.Combine(
                Path.GetTempPath(),
                $"{SoftwareName}-v{version}-{Guid.NewGuid().ToString("N")}.zip");
            try
            {
                var softwareDescription = SoftwareDescriptions.Single(d => d.Version == version);
                await DownloadFileAsync(softwareDescription.DownloadUri, tempDownload).ConfigureAwait(false);
                var installPath = GetInstallPath(version);
                ExtractZipFile(tempDownload, installPath);
                return installPath;
            }
            finally
            {
                if (File.Exists(tempDownload))
                {
                    File.Delete(tempDownload);
                }
            }
        }

        private static void ExtractZipFile(string zipSourcePath, string directoryDestinationPath)
        {
            Console.WriteLine($"Extracting '{zipSourcePath}' to '{directoryDestinationPath}'");

            if (!Directory.Exists(directoryDestinationPath))
            {
                Directory.CreateDirectory(directoryDestinationPath);
            }

            ZipFile.ExtractToDirectory(zipSourcePath, directoryDestinationPath);
        }

        private bool IsInstalled(Version version)
        {
            return Directory.Exists(GetInstallPath(version));
        }

        protected string GetInstallPath(Version version)
        {
            return Path.Combine(
                Path.GetTempPath(),
                $"eventflow-{SoftwareName}-v{version}");
        }

        private static async Task DownloadFileAsync(Uri sourceUri, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                throw new ArgumentException($"File '{destinationPath}' already exists");
            }

            Console.WriteLine($"Downloading '{sourceUri}' to '{destinationPath}'");

            using (var httpClient = new HttpClient())
            using (var httpRequestMessage = new HttpRequestMessage(HttpMethod.Get, sourceUri))
            using (var httpResponseMessage = await httpClient.SendAsync(httpRequestMessage).ConfigureAwait(false))
            {
                if (!httpResponseMessage.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"Failed to download '{sourceUri}' due to '{httpResponseMessage.StatusCode}'");
                }

                using (var sourceStream = await httpResponseMessage.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var destinationStream = new FileStream(destinationPath, FileMode.CreateNew))
                {
                    await sourceStream.CopyToAsync(destinationStream).ConfigureAwait(false);
                }
            }
        }
    }
}