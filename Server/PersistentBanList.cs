using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;

namespace RadioRelay.Server
{
    public sealed class PersistentBanList
    {
        private readonly string _path;

        public PersistentBanList(string path)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
        }

        public IReadOnlyCollection<IPAddress> Load()
        {
            if (!File.Exists(_path)) return Array.Empty<IPAddress>();

            var addresses = new List<IPAddress>();
            foreach (var rawLine in File.ReadLines(_path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (IPAddress.TryParse(line, out var address))
                    addresses.Add(NormalizeAddress(address));
            }

            return addresses.Distinct().ToArray();
        }

        public void Save(IEnumerable<IPAddress> addresses)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var lines = addresses
                .Select(NormalizeAddress)
                .Distinct()
                .OrderBy(ip => ip.ToString())
                .Select(ip => ip.ToString())
                .ToArray();
            File.WriteAllLines(_path, lines);
        }

        private static IPAddress NormalizeAddress(IPAddress address) =>
            address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }
}
