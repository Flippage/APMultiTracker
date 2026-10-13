using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace APMultiTracker;

/// <summary>One version's notes from CHANGELOG.md: each entry is a bullet, with **bold** and `code` left in for the renderer.</summary>
public sealed record ChangelogVersion(string Version, IReadOnlyList<string> Entries);

/// <summary>The patch notes shipped inside the app (CHANGELOG.md is an embedded resource).</summary>
public static class Changelog
{
    private static IReadOnlyList<ChangelogVersion>? _versions;

    public static IReadOnlyList<ChangelogVersion> Versions => _versions ??= Load();

    private static IReadOnlyList<ChangelogVersion> Load()
    {
        try
        {
            using var stream = typeof(Changelog).Assembly.GetManifestResourceStream("CHANGELOG.md");
            if (stream == null) return Array.Empty<ChangelogVersion>();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            Log.Warn("app", $"Couldn't read the patch notes: {ex.Message}");
            return Array.Empty<ChangelogVersion>();
        }
    }

    /// <summary>"## v1.2.3" starts a version, "- " starts an entry, indented lines continue the entry above.</summary>
    public static IReadOnlyList<ChangelogVersion> Parse(string markdown)
    {
        var versions = new List<ChangelogVersion>();
        string? version = null;
        var entries = new List<string>();
        var entry = new StringBuilder();

        void EndEntry()
        {
            if (entry.Length > 0) entries.Add(entry.ToString());
            entry.Clear();
        }
        void EndVersion()
        {
            EndEntry();
            if (version != null) versions.Add(new ChangelogVersion(version, entries.ToArray()));
            entries.Clear();
        }

        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("## "))
            {
                EndVersion();
                version = line[3..].Trim();
            }
            else if (version == null) continue;   // the title above the first version
            else if (line.StartsWith("- "))
            {
                EndEntry();
                entry.Append(line[2..].Trim());
            }
            else if (line.Length > 0 && char.IsWhiteSpace(raw[0]) && entry.Length > 0)
                entry.Append(' ').Append(line.Trim());
            else if (line.Length > 0)
            {
                EndEntry();
                entry.Append(line.Trim());   // a plain paragraph
            }
        }
        EndVersion();
        return versions;
    }
}
