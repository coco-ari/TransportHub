using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TransportHub.Desktop.Core
{
    /// <summary>
    /// Windows path validation shared by the timeline store and attachment pipeline.
    /// It rejects ambiguous Win32 names, traversal, alternate data streams, Syncthing
    /// conflict copies, and reparse points below the trusted root.
    /// </summary>
    public static class PathSafety
    {
        private static readonly HashSet<string> ReservedDeviceNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
            };

        public static string NormalizeRootPath(string rootPath)
        {
            if (String.IsNullOrWhiteSpace(rootPath))
            {
                throw new ArgumentException("A root path is required.", "rootPath");
            }

            string expanded = Environment.ExpandEnvironmentVariables(rootPath.Trim());
            bool hasDriveAbsolutePrefix = expanded.Length >= 3 &&
                Char.IsLetter(expanded[0]) && expanded[1] == ':' &&
                (expanded[2] == Path.DirectorySeparatorChar || expanded[2] == Path.AltDirectorySeparatorChar);
            if (!hasDriveAbsolutePrefix || !Path.IsPathRooted(expanded) ||
                expanded.StartsWith(@"\\", StringComparison.Ordinal))
            {
                throw new ArgumentException("The root must be an absolute local Windows path.", "rootPath");
            }

            string fullPath = Path.GetFullPath(expanded);
            string pathRoot = Path.GetPathRoot(fullPath);
            if (String.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("A drive root cannot be used as the TransportHub root.", "rootPath");
            }

            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>
        /// Returns a canonical protocol path with '/' separators.
        /// </summary>
        public static string NormalizeRelativePath(string relativePath)
        {
            if (String.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("A relative path is required.", "relativePath");
            }

            if (relativePath.Length > 1024)
            {
                throw new ArgumentException("The relative path is too long.", "relativePath");
            }

            string value = relativePath.Replace('/', '\\');
            if (Path.IsPathRooted(value) || value.StartsWith("\\", StringComparison.Ordinal) ||
                value.IndexOf(':') >= 0)
            {
                throw new ArgumentException("Absolute, drive-relative, UNC, and alternate-stream paths are not allowed.", "relativePath");
            }

            string[] segments = value.Split(new[] { '\\' }, StringSplitOptions.None);
            if (segments.Length == 0)
            {
                throw new ArgumentException("The relative path has no usable segments.", "relativePath");
            }

            for (int index = 0; index < segments.Length; index++)
            {
                ValidateSafePathComponent(segments[index], "relativePath");
                if (index == 0 && IsProtocolOrSyncthingDirectory(segments[index]))
                {
                    throw new ArgumentException("Attachments cannot point into TransportHub or Syncthing metadata.", "relativePath");
                }
            }

            return String.Join("/", segments);
        }

        public static string ResolveUnderRoot(string rootPath, string relativePath)
        {
            string normalizedRoot = NormalizeRootPath(rootPath);
            string normalizedRelative = NormalizeRelativePath(relativePath);
            string nativeRelative = normalizedRelative.Replace('/', Path.DirectorySeparatorChar);
            string candidate = Path.GetFullPath(Path.Combine(normalizedRoot, nativeRelative));
            EnsureContained(normalizedRoot, candidate);
            return candidate;
        }

        public static bool IsSyncConflictPath(string path)
        {
            return !String.IsNullOrEmpty(path) &&
                path.IndexOf(".sync-conflict-", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Throws when an existing component at or below rootPath is a file-system
        /// reparse point. Missing trailing components are allowed.
        /// </summary>
        public static void EnsureNoReparsePoints(string rootPath, string fullPath)
        {
            string normalizedRoot = NormalizeRootPath(rootPath);
            string normalizedFullPath = Path.GetFullPath(fullPath);
            EnsureContainedOrEqual(normalizedRoot, normalizedFullPath);

            InspectExistingPath(normalizedRoot);
            if (String.Equals(normalizedRoot, normalizedFullPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string relative = normalizedFullPath.Substring(normalizedRoot.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string current = normalizedRoot;
            string[] segments = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (string segment in segments)
            {
                current = Path.Combine(current, segment);
                InspectExistingPath(current);
            }
        }

        public static string EnsureExistingFileIsSafe(string rootPath, string relativePath)
        {
            string fullPath = ResolveUnderRoot(rootPath, relativePath);
            EnsureNoReparsePoints(rootPath, fullPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("The attachment file does not exist below the synchronized root.", fullPath);
            }

            FileAttributes attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.Directory) != 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The attachment must be a regular file and cannot be a reparse point.");
            }

            return fullPath;
        }

        public static string EnsureExistingDirectoryIsSafe(string rootPath, string relativePath)
        {
            string fullPath = ResolveUnderRoot(rootPath, relativePath);
            EnsureNoReparsePoints(rootPath, fullPath);
            if (!Directory.Exists(fullPath))
            {
                throw new DirectoryNotFoundException(
                    "The attachment directory does not exist below the synchronized root: " + fullPath);
            }

            FileAttributes attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The attachment must be a regular directory and cannot be a reparse point.");
            }

            return fullPath;
        }

        internal static string GetRelativePathUnderRoot(string rootPath, string fullPath)
        {
            string normalizedRoot = NormalizeRootPath(rootPath);
            string normalizedFullPath = Path.GetFullPath(fullPath);
            EnsureContained(normalizedRoot, normalizedFullPath);
            string relative = normalizedFullPath.Substring(normalizedRoot.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return NormalizeRelativePath(relative);
        }

        internal static void ValidateSafePathComponent(string component, string parameterName)
        {
            if (String.IsNullOrEmpty(component) || component == "." || component == "..")
            {
                throw new ArgumentException("Empty and traversal path segments are not allowed.", parameterName);
            }

            if (component.Length > 255 || !String.Equals(component, component.Trim(), StringComparison.Ordinal) ||
                component.EndsWith(".", StringComparison.Ordinal))
            {
                throw new ArgumentException("The path contains an ambiguous Windows path segment.", parameterName);
            }

            if (IsSyncConflictPath(component))
            {
                throw new ArgumentException("Syncthing conflict-copy paths are not accepted.", parameterName);
            }

            char[] invalidCharacters = Path.GetInvalidFileNameChars();
            for (int index = 0; index < component.Length; index++)
            {
                char character = component[index];
                if (character < 32 || Array.IndexOf(invalidCharacters, character) >= 0)
                {
                    throw new ArgumentException("The path contains an invalid Windows filename character.", parameterName);
                }
            }

            string stem = component;
            int dotIndex = stem.IndexOf('.');
            if (dotIndex >= 0)
            {
                stem = stem.Substring(0, dotIndex);
            }

            if (ReservedDeviceNames.Contains(stem))
            {
                throw new ArgumentException("The path contains a reserved Windows device name.", parameterName);
            }

            // Reject unpaired UTF-16 surrogates before UTF-8 serialization.
            try
            {
                new UTF8Encoding(false, true).GetByteCount(component);
            }
            catch (EncoderFallbackException exception)
            {
                throw new ArgumentException("The path contains invalid Unicode.", parameterName, exception);
            }
        }

        private static bool IsProtocolOrSyncthingDirectory(string component)
        {
            return String.Equals(component, ".transporthub", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(component, ".stversions", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(component, ".stfolder", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(component, ".stignore", StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureContained(string rootPath, string candidatePath)
        {
            if (String.Equals(rootPath, candidatePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The relative path must identify an item below the root.", "candidatePath");
            }

            EnsureContainedOrEqual(rootPath, candidatePath);
        }

        private static void EnsureContainedOrEqual(string rootPath, string candidatePath)
        {
            string prefix = rootPath + Path.DirectorySeparatorChar;
            if (!String.Equals(rootPath, candidatePath, StringComparison.OrdinalIgnoreCase) &&
                !candidatePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The path escapes the trusted root.", "candidatePath");
            }
        }

        private static void InspectExistingPath(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Reparse points are not allowed below the TransportHub root: " + path);
                }
            }
            catch (FileNotFoundException)
            {
                // A caller may be validating a path immediately before creating it.
            }
            catch (DirectoryNotFoundException)
            {
                // A caller may be validating a path immediately before creating it.
            }
        }
    }
}
