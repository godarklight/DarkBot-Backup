namespace DarkBotBackup
{
    public class AttachmentDownload
    {
        public readonly ulong serverID;
        public readonly ulong channelID;
        public readonly ulong messageID;
        public readonly ulong attachmentID;
        public readonly string url;

        public AttachmentDownload(ulong serverID, ulong channelID, ulong messageID, ulong attachmentID, string url)
        {
            this.serverID = serverID;
            this.channelID = channelID;
            this.messageID = messageID;
            this.attachmentID = attachmentID;
            this.url = url;
        }
    }
}