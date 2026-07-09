namespace RadioRelay.Shared.Diagnostics
{
    public static class ErrorCodes
    {
        public const string ClientAppStart = "RR-APP-0001";
        public const string ClientAppExit = "RR-APP-0002";
        public const string ClientFormShown = "RR-APP-0003";
        public const string ClientFormClosed = "RR-APP-0004";
        public const string WinFormsThreadException = "RR-UI-0001";
        public const string UnhandledAppDomainException = "RR-BG-0001";
        public const string UnobservedTaskException = "RR-BG-0002";
        public const string ClientConnectFailure = "RR-NET-0001";
        public const string ClientMalformedServerPacket = "RR-NET-0002";
        public const string ClientSendAudioFailure = "RR-NET-0003";
        public const string ClientSettingsLoadSaveFailure = "RR-SET-0001";
        public const string ClientSettingsImportExportFailure = "RR-SET-0002";
        public const string ClientAudioCallbackFailure = "RR-AUD-0001";
        public const string ServerStart = "RR-SRV-0001";
        public const string ServerStop = "RR-SRV-0002";
        public const string ServerUnhandledException = "RR-SRV-0003";
        public const string ServerAdminAudit = "RR-ADM-0001";

        public static readonly string[] All =
        {
            ClientAppStart,
            ClientAppExit,
            ClientFormShown,
            ClientFormClosed,
            WinFormsThreadException,
            UnhandledAppDomainException,
            UnobservedTaskException,
            ClientConnectFailure,
            ClientMalformedServerPacket,
            ClientSendAudioFailure,
            ClientSettingsLoadSaveFailure,
            ClientSettingsImportExportFailure,
            ClientAudioCallbackFailure,
            ServerStart,
            ServerStop,
            ServerUnhandledException,
            ServerAdminAudit
        };
    }
}
