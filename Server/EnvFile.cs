using System;
using System.Collections.Generic;
using System.IO;

namespace RadioRelay.Server
{
    /// Minimal .env parser: KEY=VALUE lines, '#' comment lines, optional
    /// surrounding quotes and an optional leading "export". Values are looked
    /// up case-insensitively. Missing/unreadable files yield an empty set.
    public sealed class EnvFile
    {
        public static readonly EnvFile Empty = new EnvFile(new Dictionary<string, string>());

        private readonly IReadOnlyDictionary<string, string> _values;

        private EnvFile(IReadOnlyDictionary<string, string> values) => _values = values;

        public static EnvFile Parse(IEnumerable<string> lines)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in lines)
            {
                if (raw == null) continue;
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
                    line = line.Substring("export ".Length).TrimStart();

                var eq = line.IndexOf('=');
                if (eq <= 0) continue;

                var key = line.Substring(0, eq).Trim();
                if (key.Length == 0) continue;

                var value = Unquote(line.Substring(eq + 1).Trim());
                values[key] = value;
            }

            return new EnvFile(values);
        }

        public static EnvFile Load(string path)
        {
            try
            {
                return File.Exists(path) ? Parse(File.ReadAllLines(path)) : Empty;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Empty;
            }
        }

        public string? Get(string key) =>
            _values.TryGetValue(key, out var value) ? value : null;

        public bool TryGetInt(string key, out int value)
        {
            value = 0;
            return _values.TryGetValue(key, out var raw) && int.TryParse(raw, out value);
        }

        private static string Unquote(string value)
        {
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'')))
            {
                return value.Substring(1, value.Length - 2);
            }

            return value;
        }
    }
}
