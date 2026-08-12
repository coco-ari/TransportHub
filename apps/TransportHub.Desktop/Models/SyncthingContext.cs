using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace TransportHub.Desktop.Models
{
    internal sealed class RemoteDeviceInfo
    {
        internal string Id { get; set; }
        internal string Name { get; set; }
    }

    internal sealed class SyncthingContext
    {
        private static readonly Regex DeviceIdPattern = new Regex(
            "^[A-Z2-7]{7}(?:-[A-Z2-7]{7}){7}$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        internal string FolderId { get; private set; }
        internal string RootPath { get; private set; }
        internal string MachineFolder { get; private set; }
        internal string StagingPath { get; private set; }
        internal string ConfigDirectory { get; private set; }
        internal string SyncthingExecutable { get; private set; }
        internal string LocalDeviceId { get; private set; }
        internal string LocalDeviceName { get; private set; }
        internal string ApiKey { get; private set; }
        internal Uri GuiUri { get; private set; }
        internal IReadOnlyList<RemoteDeviceInfo> TargetDevices { get; private set; }

        internal void RefreshTargetDevices()
        {
            var configFile = Path.Combine(ConfigDirectory, "config.xml");
            var document = new XmlDocument { XmlResolver = null };
            using (var stream = new FileStream(configFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                document.Load(stream);
            }
            var folderNode = document.SelectSingleNode("/configuration/folder[@id=" + QuoteXPath(FolderId) + "]") as XmlElement;
            if (folderNode == null)
            {
                throw new InvalidOperationException("Syncthing 共享目录配置已被移除，请重新启动 TransportHub。");
            }
            var deviceNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlElement device in document.SelectNodes("/configuration/device"))
            {
                var id = device.GetAttribute("id");
                if (!String.IsNullOrWhiteSpace(id))
                {
                    var name = device.GetAttribute("name");
                    deviceNames[id] = String.IsNullOrWhiteSpace(name) ? id.Substring(0, Math.Min(7, id.Length)) : name;
                }
            }
            var targets = new List<RemoteDeviceInfo>();
            foreach (XmlElement device in folderNode.SelectNodes("device"))
            {
                var id = device.GetAttribute("id");
                if (String.IsNullOrWhiteSpace(id) || String.Equals(id, LocalDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string name;
                if (!deviceNames.TryGetValue(id, out name))
                {
                    name = id.Substring(0, Math.Min(7, id.Length));
                }
                targets.Add(new RemoteDeviceInfo { Id = id, Name = name });
            }
            TargetDevices = targets
                .GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        internal static SyncthingContext Load(string folderId = "transporthub-data", string configDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(configDirectory))
            {
                configDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Syncthing");
            }

            configDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configDirectory));
            var configFile = Path.Combine(configDirectory, "config.xml");
            if (!File.Exists(configFile))
            {
                throw new FileNotFoundException("找不到 Syncthing 配置文件。", configFile);
            }

            var document = new XmlDocument { XmlResolver = null };
            using (var stream = new FileStream(configFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                document.Load(stream);
            }

            var folderNode = document.SelectSingleNode("/configuration/folder[@id=" + QuoteXPath(folderId) + "]") as XmlElement;
            if (folderNode == null)
            {
                throw new InvalidOperationException("Syncthing 中不存在 Folder ID '" + folderId + "'。请先运行 bootstrap.ps1。");
            }

            var rootPath = ResolveFolderPath(folderNode.GetAttribute("path"));
            if (!Directory.Exists(rootPath))
            {
                Directory.CreateDirectory(rootPath);
            }

            var executable = FindSyncthingExecutable();
            var localDeviceId = ReadDeviceId(executable, configDirectory);
            var localDeviceName = Environment.MachineName;

            var deviceNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlElement device in document.SelectNodes("/configuration/device"))
            {
                var id = device.GetAttribute("id");
                var name = device.GetAttribute("name");
                if (!string.IsNullOrWhiteSpace(id))
                {
                    deviceNames[id] = string.IsNullOrWhiteSpace(name) ? id.Substring(0, Math.Min(7, id.Length)) : name;
                }
                if (string.Equals(id, localDeviceId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(name))
                {
                    localDeviceName = name;
                }
            }

            var targets = new List<RemoteDeviceInfo>();
            foreach (XmlElement device in folderNode.SelectNodes("device"))
            {
                var id = device.GetAttribute("id");
                if (string.IsNullOrWhiteSpace(id) || string.Equals(id, localDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string name;
                if (!deviceNames.TryGetValue(id, out name))
                {
                    name = id.Substring(0, Math.Min(7, id.Length));
                }
                targets.Add(new RemoteDeviceInfo { Id = id, Name = name });
            }

            var gui = document.SelectSingleNode("/configuration/gui") as XmlElement;
            if (gui == null)
            {
                throw new InvalidOperationException("Syncthing GUI 配置不存在。");
            }

            var guiAddress = (gui.SelectSingleNode("address") == null ? null : gui.SelectSingleNode("address").InnerText) ?? "127.0.0.1:8384";
            var tlsText = (gui.SelectSingleNode("tls") == null ? null : gui.SelectSingleNode("tls").InnerText) ?? "false";
            var apiKey = (gui.SelectSingleNode("apikey") == null ? null : gui.SelectSingleNode("apikey").InnerText) ?? string.Empty;

            var machineDirectoryName = SanitizeFileName(Environment.MachineName);
            if (string.IsNullOrWhiteSpace(machineDirectoryName))
            {
                machineDirectoryName = "device-" + localDeviceId.Substring(0, 7);
            }
            var machineFolder = Path.Combine(rootPath, machineDirectoryName);
            Directory.CreateDirectory(machineFolder);

            var parent = Directory.GetParent(rootPath);
            if (parent == null)
            {
                throw new InvalidOperationException("同步目录不能是盘符根目录。");
            }
            var stagingPath = Path.Combine(
                parent.FullName,
                "." + new DirectoryInfo(rootPath).Name + ".transporthub-staging-" + ShortDeviceKey(localDeviceId));
            Directory.CreateDirectory(stagingPath);

            return new SyncthingContext
            {
                FolderId = folderId,
                RootPath = rootPath,
                MachineFolder = machineFolder,
                StagingPath = stagingPath,
                ConfigDirectory = configDirectory,
                SyncthingExecutable = executable,
                LocalDeviceId = localDeviceId,
                LocalDeviceName = localDeviceName,
                ApiKey = apiKey,
                GuiUri = BuildGuiUri(guiAddress, string.Equals(tlsText, "true", StringComparison.OrdinalIgnoreCase)),
                TargetDevices = targets
                    .GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList()
            };
        }

        internal static string ShortDeviceKey(string deviceId)
        {
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(deviceId ?? string.Empty));
                return string.Concat(hash.Take(10).Select(value => value.ToString("x2")));
            }
        }

        internal static SyncthingContext CreateForTesting(
            string rootPath,
            string machineFolder,
            string stagingPath,
            string localDeviceId)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(machineFolder) ||
                string.IsNullOrWhiteSpace(stagingPath) || !DeviceIdPattern.IsMatch(localDeviceId ?? string.Empty))
            {
                throw new ArgumentException("Invalid TransferService test context.");
            }
            rootPath = Path.GetFullPath(rootPath);
            machineFolder = Path.GetFullPath(machineFolder);
            stagingPath = Path.GetFullPath(stagingPath);
            Directory.CreateDirectory(rootPath);
            Directory.CreateDirectory(machineFolder);
            Directory.CreateDirectory(stagingPath);
            return new SyncthingContext
            {
                FolderId = "test",
                RootPath = rootPath,
                MachineFolder = machineFolder,
                StagingPath = stagingPath,
                ConfigDirectory = rootPath,
                SyncthingExecutable = string.Empty,
                LocalDeviceId = localDeviceId,
                LocalDeviceName = "Self Test",
                ApiKey = string.Empty,
                GuiUri = new Uri("http://127.0.0.1:8384/"),
                TargetDevices = new List<RemoteDeviceInfo>()
            };
        }

        private static string QuoteXPath(string value)
        {
            if (!value.Contains("'"))
            {
                return "'" + value + "'";
            }
            if (!value.Contains("\""))
            {
                return "\"" + value + "\"";
            }
            throw new ArgumentException("Folder ID contains unsupported quote characters.", "value");
        }

        private static string ResolveFolderPath(string configuredPath)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                throw new InvalidOperationException("Syncthing 共享目录路径为空。");
            }

            var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
            if (expanded == "~")
            {
                expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            else if (expanded.StartsWith("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), expanded.Substring(2));
            }
            return Path.GetFullPath(expanded.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        private static string FindSyncthingExecutable()
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Syncthing", "syncthing.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Syncthing", "syncthing.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Syncthing", "syncthing.exe")
            };
            var found = candidates.FirstOrDefault(File.Exists);
            if (found != null)
            {
                return found;
            }

            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }
                try
                {
                    var candidate = Path.Combine(directory.Trim(), "syncthing.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception)
                {
                    // Ignore malformed PATH entries and continue through trusted standard paths.
                }
            }
            throw new FileNotFoundException("找不到 syncthing.exe。请先运行 bootstrap.ps1。");
        }

        private static string ReadDeviceId(string executable, string configDirectory)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "device-id --home=\"" + configDirectory.Replace("\"", "\\\"") + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new InvalidOperationException("无法启动 Syncthing 身份检测命令。");
                }
                var output = process.StandardOutput.ReadToEnd().Trim();
                var error = process.StandardError.ReadToEnd().Trim();
                if (!process.WaitForExit(10000))
                {
                    try { process.Kill(); } catch (Exception) { }
                    throw new TimeoutException("读取 Syncthing Device ID 超时。");
                }
                if (process.ExitCode != 0 || !DeviceIdPattern.IsMatch(output))
                {
                    throw new InvalidOperationException("无法读取 Syncthing Device ID。" + (string.IsNullOrWhiteSpace(error) ? string.Empty : " " + error));
                }
                return output;
            }
        }

        private static Uri BuildGuiUri(string address, bool tls)
        {
            var value = (address ?? string.Empty).Trim();
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return new Uri(value.TrimEnd('/') + "/");
            }
            if (value.StartsWith("unix", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("暂不支持 Unix Socket 形式的 Syncthing GUI 地址。");
            }
            if (value.StartsWith("0.0.0.0:", StringComparison.Ordinal))
            {
                value = "127.0.0.1:" + value.Substring("0.0.0.0:".Length);
            }
            else if (value.StartsWith("[::]:", StringComparison.Ordinal))
            {
                value = "[::1]:" + value.Substring("[::]:".Length);
            }
            else if (value.StartsWith(":", StringComparison.Ordinal))
            {
                value = "127.0.0.1" + value;
            }
            return new Uri((tls ? "https://" : "http://") + value.TrimEnd('/') + "/");
        }

        private static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string((value ?? string.Empty).Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        }
    }
}
