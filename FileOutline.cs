using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace InazumaPwsh;

public sealed class OutlineEntry
{
    [JsonPropertyName("start")]
    public int Start { get; set; }

    [JsonPropertyName("end")]
    public int End { get; set; }

    [JsonPropertyName("level")]
    public int Level { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    public OutlineEntry(int start, int end, int level, string title)
    {
        Start = start;
        End = end;
        Level = level;
        Title = title;
    }
}

internal static partial class FileOutline
{
    private static bool _providerRegistered;

    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\))")]
    private static partial Regex AnsiRegex();

    private static readonly Regex MarkdownHeadingRegex = new(@"^(#{1,6})\s+(.+?)\s*#*\s*$", RegexOptions.Compiled);

    private static readonly Regex ControlKeywordsRegex = new(@"^(if|for|while|switch|catch|return|new|else)\b", RegexOptions.Compiled);

    private static readonly Regex CodeTypeRegex = new(
        @"^(public|private|protected|internal|export|abstract|sealed|static|partial|default|async|\s)*\s*(class|interface|struct|record|enum|namespace|module|type)\s+\w+", RegexOptions.Compiled);

    private static readonly Regex CodeFunctionRegex = new(
        @"^(export\s+)?(async\s+)?function\s*\*?\s*\w+", RegexOptions.Compiled);

    private static readonly Regex CodeMethodRegex = new(
        @"^(public|private|protected|internal|static|async|override|virtual|sealed|abstract|\s)+[\w<>\[\],\s?]+\s+\w+\s*\(", RegexOptions.Compiled);

    private static readonly Regex CodeExpressionPropertyRegex = new(
        @"^(public|private|protected|internal|static|override|virtual|sealed|abstract|\s)+[\w<>\[\],\s?]+\s+\w+\s*=>", RegexOptions.Compiled);

    private static readonly Regex CodePythonDefRegex = new(@"^(async\s+)?def\s+\w+", RegexOptions.Compiled);

    private static readonly Regex CodeClassRegex = new(@"^class\s+\w+", RegexOptions.Compiled);

    private static readonly Regex CodeGoFuncRegex = new(@"^func\s+", RegexOptions.Compiled);

    private static readonly Regex CodeRustFnRegex = new(@"^(pub\s+)?fn\s+\w+", RegexOptions.Compiled);

    private static readonly Regex CodePwshFunctionRegex = new(@"^function\s+[\w-]+", RegexOptions.Compiled);

    private static readonly Regex CodeVbRegex = new(
        @"^(Public|Private|Protected|Friend|Shared|\s)*\s*(Sub|Function|Class|Module|Property)\s+\w+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex JsonKeyRegex = new(@"^\s{0,4}""([^""]+)""\s*:", RegexOptions.Compiled);

    private static readonly Regex YamlKeyRegex = new(@"^([A-Za-z0-9_""'.-][^:#]*):", RegexOptions.Compiled);

    public static string StripAnsi(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : AnsiRegex().Replace(text, string.Empty);

    public static (string[] Lines, string Encoding, long Bytes) ReadAndDecodeFile(string path)
    {
        var fileInfo = new FileInfo(path);
        long byteLength = fileInfo.Length;
        byte[] bytes = File.ReadAllBytes(path);

        string text;
        string encoding;

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encoding = "utf-8-bom";
            text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encoding = "utf-16le";
            text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encoding = "utf-16be";
            text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else
        {
            try
            {
                var strictUtf8 = new UTF8Encoding(false, true);
                text = strictUtf8.GetString(bytes);
                encoding = "utf-8";
            }
            catch (DecoderFallbackException)
            {
                if (!_providerRegistered)
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    _providerRegistered = true;
                }
                var sjis = Encoding.GetEncoding(932);
                text = sjis.GetString(bytes);
                encoding = "shift_jis";
            }
        }

        var lines = SplitLines(text);
        return (lines, encoding, byteLength);
    }

    public static string[] SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var lines = new List<string>();
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lines.Add(line);
        }
        return lines.ToArray();
    }

    public static string DetermineKind(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".md" or ".mdx" or ".markdown" => "markdown",
            ".cs" or ".vb" or ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" or ".ps1" or ".psm1" or ".py" or ".go" or ".rs" or ".java" or ".kt" => "code",
            ".json" or ".jsonc" => "json",
            ".yml" or ".yaml" => "yaml",
            _ => "text"
        };
    }

    public static List<OutlineEntry> GetOutline(string path, string[] lines, string? kind = null)
    {
        kind ??= DetermineKind(path);
        List<OutlineEntry> entries = kind switch
        {
            "markdown" => OutlineMarkdown(lines),
            "code" when Path.GetExtension(path).Equals(".ps1", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetExtension(path).Equals(".psm1", StringComparison.OrdinalIgnoreCase) => OutlinePowerShell(lines),
            "code" => OutlineCode(lines, Path.GetExtension(path).Equals(".py", StringComparison.OrdinalIgnoreCase)),
            "json" => OutlineJson(lines),
            "yaml" => OutlineYaml(lines),
            _ => OutlineText(lines)
        };

        if (entries.Count == 0 && lines.Length > 0 && kind != "text")
        {
            entries = OutlineText(lines);
        }

        return entries;
    }

    private static List<OutlineEntry> OutlineMarkdown(string[] lines)
    {
        var entries = new List<OutlineEntry>();
        bool inFence = false;
        char fenceMarker = '\0';
        int fenceLength = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i];
            string trimmedStart = line.TrimStart();

            if (!inFence)
            {
                if (trimmedStart.StartsWith("```") || trimmedStart.StartsWith("~~~"))
                {
                    inFence = true;
                    fenceMarker = trimmedStart[0];
                    fenceLength = trimmedStart.TakeWhile(character => character == fenceMarker).Count();
                    continue;
                }

                var match = MarkdownHeadingRegex.Match(line);
                if (match.Success)
                {
                    int level = match.Groups[1].Length;
                    string title = match.Groups[2].Value.Trim();
                    if (title.Length > 120) title = title[..120];
                    entries.Add(new OutlineEntry(lineNum, lineNum, level, title));
                }
            }
            else
            {
                int markerLength = trimmedStart.TakeWhile(character => character == fenceMarker).Count();
                if (markerLength >= fenceLength && string.IsNullOrWhiteSpace(trimmedStart[markerLength..]))
                {
                    inFence = false;
                    fenceMarker = '\0';
                }
            }
        }

        ComputeHierarchicalEnds(entries, lines.Length);
        return entries;
    }

    private static List<OutlineEntry> OutlinePowerShell(string[] lines)
    {
        const string parser = """
            $encoded = [Console]::In.ReadToEnd()
            $source = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($encoded))
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
            $nodes = $ast.FindAll({ param($node)
                ($node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Parent -isnot [System.Management.Automation.Language.FunctionMemberAst]) -or
                $node -is [System.Management.Automation.Language.TypeDefinitionAst] -or
                $node -is [System.Management.Automation.Language.FunctionMemberAst]
            }, $true)
            $entries = @($nodes | ForEach-Object {
                $level = 1
                $parent = $_.Parent
                while ($null -ne $parent) {
                        if (($parent -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                            $parent.Parent -isnot [System.Management.Automation.Language.FunctionMemberAst]) -or
                        $parent -is [System.Management.Automation.Language.TypeDefinitionAst] -or
                        $parent -is [System.Management.Automation.Language.FunctionMemberAst]) { $level++ }
                    $parent = $parent.Parent
                }
                @{ start = $_.Extent.StartLineNumber; end = $_.Extent.EndLineNumber; level = $level }
            })
            [Console]::Out.Write((ConvertTo-Json -InputObject $entries -Compress))
            """;
        var startInfo = new ProcessStartInfo(PowerShellSession.ResolvePwshPath())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(parser)));
        try
        {
            using var process = Process.Start(startInfo) ?? throw new IOException("Could not start PowerShell parser.");
            try
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                var input = process.StandardInput.WriteAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
                if (!input.Wait(TimeSpan.FromSeconds(20))) throw new IOException("PowerShell parser input timed out.");
                process.StandardInput.Close();
                if (!process.WaitForExit(20000)) throw new IOException("PowerShell parser timed out.");
                if (process.ExitCode != 0) throw new IOException($"PowerShell parser failed: {error.GetAwaiter().GetResult()}");
                using var document = JsonDocument.Parse(output.GetAwaiter().GetResult());
                var entries = new List<OutlineEntry>();
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    int start = item.GetProperty("start").GetInt32();
                    string title = lines[start - 1].Trim();
                    if (title.Length > 120) title = title[..120];
                    entries.Add(new OutlineEntry(start, item.GetProperty("end").GetInt32(), item.GetProperty("level").GetInt32(), title));
                }
                return entries;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or AggregateException or JsonException or InvalidOperationException)
        {
            throw new IOException("PowerShell parser failed.", exception);
        }
    }

    private static List<OutlineEntry> OutlineCode(string[] lines, bool isPython)
    {
        var entries = new List<OutlineEntry>();
        int? decoratorStart = null;
        int decoratorIndent = -1;
        int decoratorDepth = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i];
            string trimmed = line.Trim();

            int width = 0;
            foreach (char c in line)
            {
                if (c == ' ') width += 1;
                else if (c == '\t') width += 4;
                else break;
            }

            if (isPython && (decoratorDepth > 0 || trimmed.StartsWith('@')))
            {
                if (decoratorDepth == 0)
                {
                    if (decoratorStart == null || width != decoratorIndent) decoratorStart = lineNum;
                    decoratorIndent = width;
                }
                char quote = '\0';
                bool escaped = false;
                foreach (char c in trimmed)
                {
                    if (quote != '\0')
                    {
                        if (escaped) escaped = false;
                        else if (c == '\\') escaped = true;
                        else if (c == quote) quote = '\0';
                        continue;
                    }
                    if (c == '#') break;
                    if (c is '\'' or '"') { quote = c; continue; }
                    if (c is '(' or '[' or '{') decoratorDepth++;
                    else if (c is ')' or ']' or '}') decoratorDepth = Math.Max(0, decoratorDepth - 1);
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed.StartsWith("//") || trimmed.StartsWith("'")) continue;
            if (ControlKeywordsRegex.IsMatch(trimmed)) continue;

            bool isMatch = false;
            if (CodeTypeRegex.IsMatch(trimmed)) isMatch = true;
            else if (CodeFunctionRegex.IsMatch(trimmed)) isMatch = true;
            else if (CodeMethodRegex.IsMatch(trimmed) && (!trimmed.EndsWith(';') || trimmed.Contains("=>"))) isMatch = true;
            else if (CodeExpressionPropertyRegex.IsMatch(trimmed)) isMatch = true;
            else if (CodePythonDefRegex.IsMatch(trimmed)) isMatch = true;
            else if (CodeClassRegex.IsMatch(trimmed)) isMatch = true;
            else if (CodeGoFuncRegex.IsMatch(trimmed)) isMatch = true;
            else if (CodeRustFnRegex.IsMatch(trimmed)) isMatch = true;
            else if (CodePwshFunctionRegex.IsMatch(trimmed)) isMatch = true;
            else if (CodeVbRegex.IsMatch(trimmed)) isMatch = true;

            if (isMatch)
            {
                int level = (width / 4) + 1;
                string title = trimmed.Length <= 120 ? trimmed : trimmed[..120];
                int start = isPython && width == decoratorIndent && (CodePythonDefRegex.IsMatch(trimmed) || CodeClassRegex.IsMatch(trimmed))
                    ? decoratorStart ?? lineNum : lineNum;
                entries.Add(new OutlineEntry(start, lineNum, level, title));
            }
            if (isPython) { decoratorStart = null; decoratorIndent = -1; }
        }

        ComputeHierarchicalEnds(entries, lines.Length);
        return entries;
    }

    private static List<OutlineEntry> OutlineJson(string[] lines)
    {
        var entries = new List<OutlineEntry>();
        int depth = 0;
        int closingBraceLine = 0;
        var rawKeys = new List<(int lineNum, string key)>();

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i];

            int depthAtKey = depth;
            if (depth == 0 && line.TrimStart().StartsWith('{'))
            {
                depthAtKey = 1;
            }

            var match = JsonKeyRegex.Match(line);
            if (match.Success && depthAtKey == 1)
            {
                string key = match.Groups[1].Value;
                if (key.Length > 120) key = key[..120];
                rawKeys.Add((lineNum, key));
            }

            bool inString = false;
            bool escaped = false;
            for (int ci = 0; ci < line.Length; ci++)
            {
                char c = line[ci];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                }
                else
                {
                    if (c == '"') inString = true;
                    else if (c is '{' or '[') depth++;
                    else if (c is '}' or ']')
                    {
                        depth--;
                        if (depth == 0 && c == '}')
                        {
                            closingBraceLine = lineNum;
                        }
                    }
                }
            }
        }

        for (int i = 0; i < rawKeys.Count; i++)
        {
            int start = rawKeys[i].lineNum;
            string title = rawKeys[i].key;
            int end = (i + 1 < rawKeys.Count)
                ? rawKeys[i + 1].lineNum - 1
                : (closingBraceLine > 0 ? closingBraceLine : lines.Length);
            entries.Add(new OutlineEntry(start, Math.Max(start, end), 1, title));
        }

        return entries;
    }

    private static List<OutlineEntry> OutlineYaml(string[] lines)
    {
        var entries = new List<OutlineEntry>();
        var rawEntries = new List<(int lineNum, string title)>();

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i];

            var match = YamlKeyRegex.Match(line);
            if (match.Success)
            {
                string title = match.Groups[1].Value.Trim().Trim('"', '\'');
                if (title.Length > 120) title = title[..120];
                rawEntries.Add((lineNum, title));
            }
        }

        for (int i = 0; i < rawEntries.Count; i++)
        {
            int start = rawEntries[i].lineNum;
            string title = rawEntries[i].title;
            int end = (i + 1 < rawEntries.Count) ? rawEntries[i + 1].lineNum - 1 : lines.Length;
            entries.Add(new OutlineEntry(start, Math.Max(start, end), 1, title));
        }

        return entries;
    }

    private static List<OutlineEntry> OutlineText(string[] lines)
    {
        var entries = new List<OutlineEntry>();
        for (int start = 1; start <= lines.Length; start += 100)
        {
            int end = Math.Min(start + 99, lines.Length);
            entries.Add(new OutlineEntry(start, end, 1, $"lines {start}-{end}"));
        }
        return entries;
    }

    private static void ComputeHierarchicalEnds(List<OutlineEntry> entries, int totalLines)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            int end = totalLines;
            for (int j = i + 1; j < entries.Count; j++)
            {
                if (entries[j].Level <= entries[i].Level)
                {
                    end = entries[j].Start - 1;
                    break;
                }
            }
            entries[i].End = Math.Max(entries[i].Start, end);
        }
    }
}
