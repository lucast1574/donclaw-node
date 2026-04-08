using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DonClaw.Shared.Capabilities;

/// <summary>
/// File capability — browse, read, write, edit, search, and open files on the user's PC.
/// Designed to let an AI assistant work with the user's code and files:
/// - Read entire project trees for context
/// - Write/edit code files
/// - Create new files and directories
/// - Open files visibly for the user to see
/// </summary>
public class FileCapability : NodeCapabilityBase
{
    public override string Category => "file";

    private static readonly string[] _commands =
    {
        "file.list", "file.read", "file.readMultiple", "file.write", "file.edit",
        "file.info", "file.search", "file.open", "file.mkdir", "file.delete",
        "file.tree", "file.grep"
    };

    public override IReadOnlyList<string> Commands => _commands;

    private const long MaxReadBytes = 2 * 1024 * 1024; // 2 MB per file
    private const long MaxWriteBytes = 5 * 1024 * 1024; // 5 MB per write
    private const int MaxTreeDepth = 6;
    private const int MaxTreeEntries = 500;

    // Common binary extensions to skip in tree/grep
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".bin", ".obj", ".o", ".so", ".dylib", ".class",
        ".jar", ".zip", ".tar", ".gz", ".7z", ".rar", ".png", ".jpg",
        ".jpeg", ".gif", ".bmp", ".ico", ".mp3", ".mp4", ".avi", ".mov",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".pdb", ".woff", ".woff2",
        ".ttf", ".eot", ".pyc", ".pyo"
    };

    // Directories to always skip in tree
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".hg", ".svn", "__pycache__", ".next",
        "bin", "obj", "dist", "build", ".vs", ".idea", ".vscode",
        "venv", ".venv", "env", ".env", "target", "packages"
    };

    public FileCapability(IOpenClawLogger logger) : base(logger) { }

    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return request.Command switch
        {
            "file.list" => HandleList(request),
            "file.read" => HandleRead(request),
            "file.readMultiple" => HandleReadMultiple(request),
            "file.write" => HandleWrite(request),
            "file.edit" => HandleEdit(request),
            "file.info" => HandleInfo(request),
            "file.search" => HandleSearch(request),
            "file.open" => HandleOpen(request),
            "file.mkdir" => HandleMkdir(request),
            "file.delete" => HandleDelete(request),
            "file.tree" => HandleTree(request),
            "file.grep" => HandleGrep(request),
            _ => Error($"Unknown: {request.Command}")
        };
    }

    /// <summary>
    /// List directory contents with name, size, type, modified date.
    /// </summary>
    private NodeInvokeResponse HandleList(NodeInvokeRequest request)
    {
        var path = GetStringArg(request.Args, "path")
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var showHidden = GetBoolArg(request.Args, "showHidden", false);

        Logger.Info($"file.list: {path}");

        try
        {
            if (!Directory.Exists(path))
                return Error($"Directory not found: {path}");

            var dirInfo = new DirectoryInfo(path);
            var entries = new List<object>();

            foreach (var dir in dirInfo.GetDirectories().OrderBy(d => d.Name))
            {
                if (!showHidden && (dir.Attributes & FileAttributes.Hidden) != 0) continue;
                entries.Add(new { name = dir.Name, type = "directory", modified = dir.LastWriteTimeUtc.ToString("o") });
            }

            foreach (var file in dirInfo.GetFiles().OrderBy(f => f.Name))
            {
                if (!showHidden && (file.Attributes & FileAttributes.Hidden) != 0) continue;
                entries.Add(new
                {
                    name = file.Name, type = "file", size = file.Length,
                    modified = file.LastWriteTimeUtc.ToString("o"), extension = file.Extension.ToLowerInvariant()
                });
            }

            return Success(new { path = dirInfo.FullName, entries, count = entries.Count });
        }
        catch (UnauthorizedAccessException) { return Error($"Access denied: {path}"); }
        catch (Exception ex) { return Error($"List failed: {ex.Message}"); }
    }

    /// <summary>
    /// Read a single file's contents. For code files, returns full text.
    /// </summary>
    private NodeInvokeResponse HandleRead(NodeInvokeRequest request)
    {
        var path = GetStringArg(request.Args, "path");
        var maxLines = GetIntArg(request.Args, "maxLines", 0);
        var offset = GetIntArg(request.Args, "offset", 0); // line offset (0-based)
        var limit = GetIntArg(request.Args, "limit", 0);   // max lines from offset

        if (string.IsNullOrEmpty(path)) return Error("Missing path");

        Logger.Info($"file.read: {path}");

        try
        {
            if (!File.Exists(path)) return Error($"File not found: {path}");

            var fi = new FileInfo(path);
            if (fi.Length > MaxReadBytes)
                return Error($"File too large ({FormatSize(fi.Length)}). Max: {FormatSize(MaxReadBytes)}. Use offset/limit for partial reads.");

            string content;
            int totalLines;

            if (offset > 0 || limit > 0 || maxLines > 0)
            {
                var allLines = File.ReadAllLines(path);
                totalLines = allLines.Length;
                var startLine = Math.Min(offset, allLines.Length);
                var takeCount = limit > 0 ? limit : (maxLines > 0 ? maxLines : allLines.Length);
                var selectedLines = allLines.Skip(startLine).Take(takeCount).ToArray();
                content = string.Join("\n", selectedLines);
            }
            else
            {
                content = File.ReadAllText(path);
                totalLines = content.Split('\n').Length;
            }

            return Success(new
            {
                path = fi.FullName, content, size = fi.Length, totalLines,
                extension = fi.Extension, encoding = "utf-8"
            });
        }
        catch (UnauthorizedAccessException) { return Error($"Access denied: {path}"); }
        catch (Exception ex) { return Error($"Read failed: {ex.Message}"); }
    }

    /// <summary>
    /// Read multiple files at once — perfect for sending project context to the AI.
    /// Returns an array of file contents. Skips files that fail.
    /// </summary>
    private NodeInvokeResponse HandleReadMultiple(NodeInvokeRequest request)
    {
        var paths = new List<string>();

        if (request.Args.ValueKind != System.Text.Json.JsonValueKind.Undefined &&
            request.Args.TryGetProperty("paths", out var pathsEl) &&
            pathsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in pathsEl.EnumerateArray())
                if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var p = item.GetString();
                    if (!string.IsNullOrEmpty(p)) paths.Add(p);
                }
        }

        if (paths.Count == 0) return Error("Missing paths array");

        Logger.Info($"file.readMultiple: {paths.Count} files");

        var results = new List<object>();
        long totalSize = 0;

        foreach (var path in paths.Take(20)) // Max 20 files at once
        {
            try
            {
                if (!File.Exists(path))
                {
                    results.Add(new { path, error = "File not found" });
                    continue;
                }

                var fi = new FileInfo(path);
                if (fi.Length > MaxReadBytes)
                {
                    results.Add(new { path, error = $"Too large: {FormatSize(fi.Length)}" });
                    continue;
                }

                totalSize += fi.Length;
                if (totalSize > 10 * 1024 * 1024) // 10 MB total limit
                {
                    results.Add(new { path, error = "Total size limit reached (10MB)" });
                    break;
                }

                var content = File.ReadAllText(path);
                results.Add(new
                {
                    path = fi.FullName, content, size = fi.Length,
                    extension = fi.Extension, lines = content.Split('\n').Length
                });
            }
            catch (Exception ex)
            {
                results.Add(new { path, error = ex.Message });
            }
        }

        return Success(new { files = results, count = results.Count, totalSize });
    }

    /// <summary>
    /// Write content to a file. Creates parent directories if needed.
    /// For creating new code files or overwriting existing ones.
    /// </summary>
    private NodeInvokeResponse HandleWrite(NodeInvokeRequest request)
    {
        var path = GetStringArg(request.Args, "path");
        var content = GetStringArg(request.Args, "content");
        var append = GetBoolArg(request.Args, "append", false);
        var createDirs = GetBoolArg(request.Args, "createDirs", true);

        if (string.IsNullOrEmpty(path)) return Error("Missing path");
        if (content == null) return Error("Missing content");

        if (Encoding.UTF8.GetByteCount(content) > MaxWriteBytes)
            return Error($"Content too large. Max: {FormatSize(MaxWriteBytes)}");

        Logger.Info($"file.write: {path} (append={append}, {content.Length} chars)");

        try
        {
            if (createDirs)
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }

            if (append)
                File.AppendAllText(path, content);
            else
                File.WriteAllText(path, content);

            var fi = new FileInfo(path);
            return Success(new { written = true, path = fi.FullName, size = fi.Length });
        }
        catch (UnauthorizedAccessException) { return Error($"Access denied: {path}"); }
        catch (Exception ex) { return Error($"Write failed: {ex.Message}"); }
    }

    /// <summary>
    /// Edit a file using search & replace. Same pattern as OpenClaw's edit tool.
    /// Supports multiple edits in one call.
    /// </summary>
    private NodeInvokeResponse HandleEdit(NodeInvokeRequest request)
    {
        var path = GetStringArg(request.Args, "path");
        if (string.IsNullOrEmpty(path)) return Error("Missing path");
        if (!File.Exists(path)) return Error($"File not found: {path}");

        Logger.Info($"file.edit: {path}");

        try
        {
            var content = File.ReadAllText(path);
            int editCount = 0;

            // Support array of edits: [{oldText, newText}, ...]
            if (request.Args.TryGetProperty("edits", out var editsEl) &&
                editsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var edit in editsEl.EnumerateArray())
                {
                    var oldText = edit.TryGetProperty("oldText", out var o) ? o.GetString() : null;
                    var newText = edit.TryGetProperty("newText", out var n) ? n.GetString() : null;

                    if (string.IsNullOrEmpty(oldText)) continue;

                    if (!content.Contains(oldText))
                    {
                        return Error($"oldText not found in file: \"{(oldText.Length > 60 ? oldText[..60] + "..." : oldText)}\"");
                    }

                    content = content.Replace(oldText, newText ?? "");
                    editCount++;
                }
            }
            // Single edit: oldText + newText
            else
            {
                var oldText = GetStringArg(request.Args, "oldText");
                var newText = GetStringArg(request.Args, "newText", "");

                if (string.IsNullOrEmpty(oldText)) return Error("Missing oldText or edits array");
                if (!content.Contains(oldText))
                    return Error($"oldText not found in file");

                content = content.Replace(oldText, newText);
                editCount = 1;
            }

            File.WriteAllText(path, content);

            return Success(new { edited = true, path, editsApplied = editCount });
        }
        catch (Exception ex) { return Error($"Edit failed: {ex.Message}"); }
    }

    /// <summary>
    /// Get file/directory metadata.
    /// </summary>
    private NodeInvokeResponse HandleInfo(NodeInvokeRequest request)
    {
        var path = GetStringArg(request.Args, "path");
        if (string.IsNullOrEmpty(path)) return Error("Missing path");

        try
        {
            if (Directory.Exists(path))
            {
                var dir = new DirectoryInfo(path);
                return Success(new
                {
                    path = dir.FullName, type = "directory", name = dir.Name, exists = true,
                    created = dir.CreationTimeUtc.ToString("o"), modified = dir.LastWriteTimeUtc.ToString("o")
                });
            }
            if (File.Exists(path))
            {
                var file = new FileInfo(path);
                return Success(new
                {
                    path = file.FullName, type = "file", name = file.Name, extension = file.Extension,
                    size = file.Length, sizeHuman = FormatSize(file.Length), exists = true,
                    created = file.CreationTimeUtc.ToString("o"), modified = file.LastWriteTimeUtc.ToString("o"),
                    readOnly = file.IsReadOnly
                });
            }
            return Success(new { path, exists = false });
        }
        catch (Exception ex) { return Error($"Info failed: {ex.Message}"); }
    }

    /// <summary>
    /// Search for files by name pattern in a directory.
    /// </summary>
    private NodeInvokeResponse HandleSearch(NodeInvokeRequest request)
    {
        var path = GetStringArg(request.Args, "path")
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var pattern = GetStringArg(request.Args, "pattern", "*");
        var recursive = GetBoolArg(request.Args, "recursive", false);
        var maxResults = GetIntArg(request.Args, "maxResults", 50);

        Logger.Info($"file.search: {path} pattern={pattern}");

        try
        {
            if (!Directory.Exists(path)) return Error($"Directory not found: {path}");

            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var results = Directory.EnumerateFiles(path, pattern!, option)
                .Take(maxResults)
                .Select(f =>
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        return (object)new { path = fi.FullName, name = fi.Name, size = fi.Length, modified = fi.LastWriteTimeUtc.ToString("o") };
                    }
                    catch { return null; }
                })
                .Where(r => r != null)
                .ToList();

            return Success(new { results, count = results.Count, searchPath = path, pattern });
        }
        catch (Exception ex) { return Error($"Search failed: {ex.Message}"); }
    }

    /// <summary>
    /// Open a file with its default application. User SEES it open.
    /// </summary>
    private NodeInvokeResponse HandleOpen(NodeInvokeRequest request)
    {
        var path = GetStringArg(request.Args, "path");
        if (string.IsNullOrEmpty(path)) return Error("Missing path");
        if (!File.Exists(path) && !Directory.Exists(path)) return Error($"Not found: {path}");

        Logger.Info($"file.open: {path}");

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return Success(new { opened = true, path });
        }
        catch (Exception ex) { return Error($"Open failed: {ex.Message}"); }
    }

    /// <summary>
    /// Create a directory (and parents).
    /// </summary>
    private NodeInvokeResponse HandleMkdir(NodeInvokeRequest request)
    {
        var path = GetStringArg(request.Args, "path");
        if (string.IsNullOrEmpty(path)) return Error("Missing path");

        try
        {
            Directory.CreateDirectory(path);
            return Success(new { created = true, path = Path.GetFullPath(path) });
        }
        catch (Exception ex) { return Error($"Mkdir failed: {ex.Message}"); }
    }

    /// <summary>
    /// Delete a file or directory. Requires explicit confirm=true for safety.
    /// </summary>
    private NodeInvokeResponse HandleDelete(NodeInvokeRequest request)
    {
        var path = GetStringArg(request.Args, "path");
        var confirm = GetBoolArg(request.Args, "confirm", false);

        if (string.IsNullOrEmpty(path)) return Error("Missing path");
        if (!confirm) return Error("Requires confirm=true for safety");

        Logger.Info($"file.delete: {path}");

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return Success(new { deleted = true, path, type = "file" });
            }
            if (Directory.Exists(path))
            {
                var recursive = GetBoolArg(request.Args, "recursive", false);
                Directory.Delete(path, recursive);
                return Success(new { deleted = true, path, type = "directory" });
            }
            return Error($"Not found: {path}");
        }
        catch (Exception ex) { return Error($"Delete failed: {ex.Message}"); }
    }

    /// <summary>
    /// Get a tree view of a directory structure — perfect for understanding project layout.
    /// Automatically skips node_modules, .git, bin, obj, etc.
    /// </summary>
    private NodeInvokeResponse HandleTree(NodeInvokeRequest request)
    {
        var path = GetStringArg(request.Args, "path");
        var maxDepth = GetIntArg(request.Args, "maxDepth", MaxTreeDepth);
        var includeFiles = GetBoolArg(request.Args, "includeFiles", true);
        var showSize = GetBoolArg(request.Args, "showSize", false);

        if (string.IsNullOrEmpty(path)) return Error("Missing path");
        if (!Directory.Exists(path)) return Error($"Directory not found: {path}");

        Logger.Info($"file.tree: {path} depth={maxDepth}");

        try
        {
            var sb = new StringBuilder();
            int count = 0;
            BuildTree(path, "", maxDepth, 0, includeFiles, showSize, sb, ref count);

            return Success(new { path = Path.GetFullPath(path), tree = sb.ToString(), entries = count });
        }
        catch (Exception ex) { return Error($"Tree failed: {ex.Message}"); }
    }

    private void BuildTree(string dirPath, string indent, int maxDepth, int currentDepth,
        bool includeFiles, bool showSize, StringBuilder sb, ref int count)
    {
        if (currentDepth >= maxDepth || count >= MaxTreeEntries) return;

        try
        {
            var dirInfo = new DirectoryInfo(dirPath);

            // Directories
            var dirs = dirInfo.GetDirectories()
                .Where(d => !SkipDirs.Contains(d.Name) && (d.Attributes & FileAttributes.Hidden) == 0)
                .OrderBy(d => d.Name);

            foreach (var dir in dirs)
            {
                if (count >= MaxTreeEntries) { sb.AppendLine($"{indent}... (truncated)"); return; }
                sb.AppendLine($"{indent}📁 {dir.Name}/");
                count++;
                BuildTree(dir.FullName, indent + "  ", maxDepth, currentDepth + 1, includeFiles, showSize, sb, ref count);
            }

            // Files
            if (includeFiles)
            {
                var files = dirInfo.GetFiles()
                    .Where(f => (f.Attributes & FileAttributes.Hidden) == 0)
                    .OrderBy(f => f.Name);

                foreach (var file in files)
                {
                    if (count >= MaxTreeEntries) { sb.AppendLine($"{indent}... (truncated)"); return; }
                    var sizeStr = showSize ? $" ({FormatSize(file.Length)})" : "";
                    sb.AppendLine($"{indent}📄 {file.Name}{sizeStr}");
                    count++;
                }
            }
        }
        catch (UnauthorizedAccessException) { sb.AppendLine($"{indent}⛔ (access denied)"); }
    }

    /// <summary>
    /// Search for text content inside files (like grep).
    /// Essential for finding code across a project.
    /// </summary>
    private NodeInvokeResponse HandleGrep(NodeInvokeRequest request)
    {
        var path = GetStringArg(request.Args, "path");
        var query = GetStringArg(request.Args, "query");
        var pattern = GetStringArg(request.Args, "pattern", "*.*"); // file pattern
        var ignoreCase = GetBoolArg(request.Args, "ignoreCase", true);
        var maxResults = GetIntArg(request.Args, "maxResults", 50);
        var contextLines = GetIntArg(request.Args, "contextLines", 0);

        if (string.IsNullOrEmpty(path)) return Error("Missing path");
        if (string.IsNullOrEmpty(query)) return Error("Missing query");
        if (!Directory.Exists(path)) return Error($"Directory not found: {path}");

        Logger.Info($"file.grep: {path} query=\"{query}\" pattern={pattern}");

        try
        {
            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var results = new List<object>();

            var files = Directory.EnumerateFiles(path, pattern!, SearchOption.AllDirectories)
                .Where(f =>
                {
                    var ext = Path.GetExtension(f);
                    var dir = Path.GetDirectoryName(f) ?? "";
                    // Skip binary files and ignored directories
                    if (BinaryExtensions.Contains(ext)) return false;
                    return !SkipDirs.Any(skip => dir.Contains(Path.DirectorySeparatorChar + skip + Path.DirectorySeparatorChar)
                                                 || dir.EndsWith(Path.DirectorySeparatorChar + skip));
                });

            foreach (var file in files)
            {
                if (results.Count >= maxResults) break;

                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Length > MaxReadBytes) continue; // Skip large files

                    var lines = File.ReadAllLines(file);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (results.Count >= maxResults) break;
                        if (lines[i].Contains(query, comparison))
                        {
                            var match = new Dictionary<string, object>
                            {
                                ["file"] = fi.FullName,
                                ["line"] = i + 1,
                                ["text"] = lines[i].TrimEnd()
                            };

                            if (contextLines > 0)
                            {
                                var start = Math.Max(0, i - contextLines);
                                var end = Math.Min(lines.Length - 1, i + contextLines);
                                var ctx = new List<string>();
                                for (int j = start; j <= end; j++)
                                    ctx.Add($"{j + 1}: {lines[j].TrimEnd()}");
                                match["context"] = ctx;
                            }

                            results.Add(match);
                        }
                    }
                }
                catch { /* skip unreadable files */ }
            }

            return Success(new { results, count = results.Count, query, searchPath = path });
        }
        catch (Exception ex) { return Error($"Grep failed: {ex.Message}"); }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}
