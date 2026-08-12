using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace TransportHub.Desktop.Interop
{
    /// <summary>
    /// Opens directories through the Windows shell and selects files using
    /// shell item ID lists. No path is interpolated into a command line.
    /// </summary>
    public static class ShellReveal
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHParseDisplayName(
            [MarshalAs(UnmanagedType.LPWStr)] string name,
            IntPtr bindingContext,
            out IntPtr itemIdList,
            uint attributesIn,
            out uint attributesOut);

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern IntPtr ILFindLastID(IntPtr itemIdList);

        [DllImport("shell32.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int SHOpenFolderAndSelectItems(
            IntPtr folderItemIdList,
            uint itemCount,
            [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] childItemIdLists,
            uint flags);

        /// <summary>
        /// Opens a directory directly, or opens its parent directory and selects
        /// the specified file. The path must be an existing absolute file-system
        /// path on this computer or an accessible UNC share.
        /// </summary>
        public static void Reveal(string path)
        {
            var fullPath = NormalizeExistingPath(path, out var isDirectory);
            if (isDirectory)
            {
                OpenDirectory(fullPath);
                return;
            }

            RevealFile(fullPath);
        }

        public static bool TryReveal(string path, out string errorMessage)
        {
            try
            {
                Reveal(path);
                errorMessage = null;
                return true;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                errorMessage = exception.Message;
                return false;
            }
        }

        private static string NormalizeExistingPath(string path, out bool isDirectory)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("需要提供要打开的本地路径。", nameof(path));
            }

            if (path.IndexOf('\0') >= 0 || !Path.IsPathRooted(path))
            {
                throw new ArgumentException("只能打开绝对文件系统路径。", nameof(path));
            }

            var fullPath = Path.GetFullPath(path);
            if (Directory.Exists(fullPath))
            {
                isDirectory = true;
                return fullPath;
            }

            if (File.Exists(fullPath))
            {
                isDirectory = false;
                return fullPath;
            }

            throw new FileNotFoundException("要打开的文件或目录不存在。", fullPath);
        }

        private static void OpenDirectory(string directoryPath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = directoryPath,
                UseShellExecute = true,
                Verb = "open",
                ErrorDialog = false
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                throw new Win32Exception("Windows Shell 未能打开目录。");
            }

            process.Dispose();
        }

        private static void RevealFile(string filePath)
        {
            var folderPath = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            {
                throw new DirectoryNotFoundException("文件的父目录不存在。");
            }

            IntPtr folderItemIdList = IntPtr.Zero;
            IntPtr fileItemIdList = IntPtr.Zero;
            try
            {
                uint ignoredAttributes;
                ThrowIfFailed(SHParseDisplayName(
                    folderPath,
                    IntPtr.Zero,
                    out folderItemIdList,
                    0,
                    out ignoredAttributes));
                ThrowIfFailed(SHParseDisplayName(
                    filePath,
                    IntPtr.Zero,
                    out fileItemIdList,
                    0,
                    out ignoredAttributes));

                if (folderItemIdList == IntPtr.Zero || fileItemIdList == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Windows Shell 无法解析该文件路径。");
                }

                var childItemIdList = ILFindLastID(fileItemIdList);
                if (childItemIdList == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Windows Shell 无法定位该文件。");
                }

                ThrowIfFailed(SHOpenFolderAndSelectItems(
                    folderItemIdList,
                    1,
                    new[] { childItemIdList },
                    0));
            }
            finally
            {
                if (fileItemIdList != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(fileItemIdList);
                }

                if (folderItemIdList != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(folderItemIdList);
                }
            }
        }

        private static void ThrowIfFailed(int hresult)
        {
            if (hresult < 0)
            {
                Marshal.ThrowExceptionForHR(hresult);
            }
        }

        private static bool IsFatal(Exception exception)
        {
            return exception is OutOfMemoryException ||
                   exception is StackOverflowException ||
                   exception is AccessViolationException ||
                   exception is AppDomainUnloadedException ||
                   exception is BadImageFormatException;
        }
    }
}
