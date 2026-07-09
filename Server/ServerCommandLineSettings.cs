namespace RadioRelay.Server
{
    public sealed class ServerCommandLineSettings
    {
        public int Port { get; init; }
        public string Password { get; init; } = "";
        public bool PortFallbackUsed { get; init; }

        public static ServerCommandLineSettings Parse(string[] args, int defaultPort)
        {
            int port = defaultPort;
            int? parsedPort = null;
            string password = "";
            bool sawPortLikeArgument = false;

            foreach (var raw in args)
            {
                var arg = raw.Trim();
                if (arg.Length == 0) continue;

                var trimmed = arg.TrimStart('-');
                if (parsedPort == null && int.TryParse(trimmed, out var candidate))
                {
                    sawPortLikeArgument = true;
                    if (candidate is > 0 and <= 65535)
                    {
                        parsedPort = candidate;
                        port = candidate;
                    }
                    continue;
                }

                if (password.Length == 0)
                {
                    password = arg;
                }
            }

            return new ServerCommandLineSettings
            {
                Port = port,
                Password = password,
                PortFallbackUsed = parsedPort == null && sawPortLikeArgument
            };
        }
    }
}
