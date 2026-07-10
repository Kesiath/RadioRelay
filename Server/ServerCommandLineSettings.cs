namespace RadioRelay.Server
{
    public sealed class ServerCommandLineSettings
    {
        /// Environment variable / .env keys the server understands.
        public const string PortKey = "PORT";
        public const string PasswordKey = "PASSWORD";

        public int Port { get; init; }
        public string Password { get; init; } = "";
        public bool PortFallbackUsed { get; init; }

        public static ServerCommandLineSettings Parse(string[] args, int defaultPort, EnvFile? env = null)
        {
            env ??= EnvFile.Empty;

            // .env values seed the defaults; command-line arguments override them.
            int port = defaultPort;
            if (env.TryGetInt(PortKey, out var envPort) && envPort is > 0 and <= 65535)
                port = envPort;
            string password = env.Get(PasswordKey) ?? "";

            int? parsedPort = null;
            bool sawPortLikeArgument = false;
            bool passwordFromArgument = false;

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

                if (!passwordFromArgument)
                {
                    password = arg;
                    passwordFromArgument = true;
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
