﻿using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using DarkBot;
using DarkBot.Whitelist;
using Discord;
using Discord.WebSocket;

namespace DarkBotBackup
{
    [BotModuleDependency(new Type[] { typeof(Whitelist) })]
    public class Backup : BotModule
    {
        private DiscordSocketClient _client;
        private Whitelist _whitelist;
        private List<string> whitelistObjects = new List<string>();
        //Channel ID, MessageID
        private ConcurrentDictionary<ulong, ulong> channelCategoryCache = new ConcurrentDictionary<ulong, ulong>();
        private ConcurrentDictionary<ulong, ulong> channelRead = new ConcurrentDictionary<ulong, ulong>();
        private ConcurrentDictionary<ulong, ChannelInfo> channelNames = new ConcurrentDictionary<ulong, ChannelInfo>();
        private ConcurrentQueue<AttachmentDownload> attachmentDownloads = new ConcurrentQueue<AttachmentDownload>();
        public event Action<string> PictureEvent;
        private Task backupTask = Task.CompletedTask;
        private string[] fileTypes = new string[] { "bmp", "gif", "jpg", "png", "tiff", "webp", "mp4", "ogg", "webm", "txt", "bin" };

        public Task Initialize(IServiceProvider services)
        {
            _client = services.GetService(typeof(DiscordSocketClient)) as DiscordSocketClient;
            _client.Ready += ReadyAsync;
            _client.MessageReceived += MessageReceivedAsync;
            _whitelist = services.GetService(typeof(Whitelist)) as Whitelist;
            LoadChannelRead();
            LoadChannelNames();
            LoadWhitelist();
            return Task.CompletedTask;
        }

        public async Task BackupServer()
        {
            foreach (SocketGuild server in _client.Guilds)
            {
                foreach (SocketGuildChannel channel in server.Channels)
                {
                    var textChannel = channel as SocketTextChannel;
                    if (textChannel != null)
                    {
                        ulong categoryID = GetCategory(textChannel);
                        string whitelistMatch = null;
                        CheckWhitelist(textChannel.Id, ref whitelistMatch);
                        if (whitelistMatch == null)
                        {
                            CheckWhitelist(categoryID, ref whitelistMatch);
                        }
                        if (whitelistMatch != null)
                        {
                            SetChannelName(textChannel.Id, textChannel.Name, categoryID);
                            await BackupChannel(textChannel, whitelistMatch);
                        }
                        else
                        {
                            Log(LogSeverity.Info, $"Skipping backup of {channel.Name}, not on whitelist");
                        }
                    }
                }
            }
        }

        public async void DownloadEmbeds()
        {
            string backupPath = Path.Combine(Environment.CurrentDirectory, "Backup");
            using (HttpClient hc = new HttpClient())
            {
                while (true)
                {
                    if (attachmentDownloads.TryDequeue(out AttachmentDownload ad))
                    {
                        try
                        {
                            string outputPath = Path.Combine(backupPath, ad.whitelistMatch, ad.channelID.ToString());
                            Directory.CreateDirectory(outputPath);
                            string fileName = Path.Combine(outputPath, ad.messageID + "-" + ad.attachmentID);
                            bool fileExists = false;
                            foreach (string ext in fileTypes)
                            {
                                if (File.Exists($"{fileName}.{ext}"))
                                {
                                    Log(LogSeverity.Info, "Skipping already downloaded file");
                                    fileExists = true;
                                }
                            }
                            if (fileExists)
                            {
                                continue;
                            }
                            HttpResponseMessage fileDownload = await hc.GetAsync(ad.url);
                            if (fileDownload.IsSuccessStatusCode)
                            {
                                string contentType = fileDownload.Content.Headers.ContentType.MediaType;
                                switch (contentType)
                                {
                                    case "image/bmp":
                                        fileName += ".bmp";
                                        break;
                                    case "image/gif":
                                        fileName += ".gif";
                                        break;
                                    case "image/jpeg":
                                        fileName += ".jpg";
                                        break;
                                    case "image/png":
                                        fileName += ".png";
                                        break;
                                    case "image/tiff":
                                        fileName += ".tiff";
                                        break;
                                    case "image/webp":
                                        fileName += ".webp";
                                        break;
                                    case "video/mp4":
                                        fileName += ".mp4";
                                        break;
                                    case "video/ogg":
                                        fileName += ".ogg";
                                        break;
                                    case "video/webm":
                                        fileName += ".webm";
                                        break;
                                    case "text/plain":
                                        fileName += ".txt";
                                        break;
                                    default:
                                        Log(LogSeverity.Info, "Unknown content type: " + contentType);
                                        fileName += ".bin";
                                        break;
                                }
                                using (FileStream fs = new FileStream(fileName, FileMode.Create))
                                {
                                    await fileDownload.Content.CopyToAsync(fs);
                                    Log(LogSeverity.Info, $"Downloaded {ad.url} for message {ad.messageID}");
                                    if (PictureEvent != null)
                                    {
                                        PictureEvent(fileName);
                                    }
                                }
                            }

                        }
                        catch (Exception e)
                        {
                            Log(LogSeverity.Info, $"Error downloading {ad.url} for message {ad.messageID}, error: {e.Message}");
                        }
                    }
                    await Task.Delay(1000);
                }
            }
        }

        private async Task BackupChannel(SocketTextChannel channel, string whitelistMatch)
        {
            if (!channel.GetUser(_client.CurrentUser.Id).GetPermissions(channel).ReadMessageHistory)
            {
                Log(LogSeverity.Info, "Insufficent access to " + channel.Name);
                return;
            }
            Log(LogSeverity.Info, $"Backing up {channel.Name} on server {channel.Guild.Name}");
            ulong fromMessage = GetChannelRead(channel.Id);
            while (true)
            {
                IAsyncEnumerable<IReadOnlyCollection<IMessage>> asyncMessages = null;
                bool newMessage = false;
                asyncMessages = channel.GetMessagesAsync(fromMessage, Direction.After);
                await foreach (IReadOnlyCollection<IMessage> messages in asyncMessages)
                {
                    foreach (IMessage message in messages)
                    {
                        //Discord gives us the list in reverse order so we want to take just the first one as our "read" point
                        if (newMessage == false)
                        {
                            fromMessage = message.Id;
                        }
                        newMessage = true;
                        BackupMessage(channel, message, whitelistMatch);
                    }
                }

                if (!newMessage)
                {
                    break;
                }
                SetChannelRead(channel.Id, fromMessage);
                await Task.Delay(1000);
                while (attachmentDownloads.Count > 0)
                {
                    await Task.Delay(1000);
                }
            }
            Log(LogSeverity.Info, $"Done backing up {channel.Name} on server {channel.Guild.Name}");
        }

        private void BackupMessage(SocketTextChannel channel, IMessage message, string whitelistMatch)
        {
            foreach (IAttachment attachment in message.Attachments)
            {
                Log(LogSeverity.Info, $"Queued download for message: {message.Id}");
                attachmentDownloads.Enqueue(new AttachmentDownload(channel.Guild.Id, channel.Id, message.Id, attachment.Id, attachment.Url, whitelistMatch));
            }
        }

        private void CleanChannels()
        {
            Dictionary<ulong, SocketChannel> checkCache = new Dictionary<ulong, SocketChannel>();
            HashSet<ulong> existingChannels = new HashSet<ulong>();
            foreach (SocketGuild sg in _client.Guilds)
            {
                foreach (SocketChannel sc in sg.Channels)
                {
                    checkCache.Add(sc.Id, sc);
                    existingChannels.Add(sc.Id);
                }
            }
            string archiveDirectory = Path.Combine(Environment.CurrentDirectory, "Backup-Removed");
            if (!Directory.Exists(archiveDirectory))
            {
                Directory.CreateDirectory(archiveDirectory);
            }
            string[] currentFolders = Directory.GetDirectories(Path.Combine(Environment.CurrentDirectory, "Backup"));
            foreach (string currentBackup in currentFolders)
            {
                string channelIDString = Path.GetFileName(currentBackup);
                if (ulong.TryParse(channelIDString, out ulong channelID))
                {
                    if (!existingChannels.Contains(channelID))
                    {
                        Directory.Move(currentBackup, Path.Combine(archiveDirectory, channelIDString));
                        continue;
                    }
                    if (checkCache.ContainsKey(channelID))
                    {
                        SocketTextChannel stc = checkCache[channelID] as SocketTextChannel;
                        if (stc == null)
                        {
                            Directory.Move(currentBackup, Path.Combine(archiveDirectory, channelIDString));
                            continue;
                        }
                        ulong categoryID = GetCategory(stc);
                        if (!CheckWhitelist(stc.Id) && !CheckWhitelist(categoryID))
                        {
                            Directory.Move(currentBackup, Path.Combine(archiveDirectory, channelIDString));
                            continue;
                        }
                    }
                }
            }
        }

        private Task ReadyAsync()
        {
            Log(LogSeverity.Info, "Starting backup service");
            CleanChannels();
            backupTask = BackupServer();
            DownloadEmbeds();
            return Task.CompletedTask;
        }

        private async Task MessageReceivedAsync(SocketMessage message)
        {
            SocketGuildUser sgu = message.Author as SocketGuildUser;
            SocketTextChannel textChannel = message.Channel as SocketTextChannel;
            if (textChannel == null)
            {
                return;
            }

            if (sgu != null && sgu.GuildPermissions.ManageChannels && message.Content.StartsWith(".backup add "))
            {
                string key = message.Content.Substring(12);
                whitelistObjects.Add(key);
                SaveWhitelist();
                await textChannel.SendMessageAsync($"Backup is now allowing objects in {key}");
                if (!backupTask.IsCompleted)
                {
                    await backupTask;
                }
                backupTask = BackupServer();
            }

            //If not on the whitelist bail
            ulong categoryID = GetCategory(textChannel);
            string whitelistMatch = null;
            CheckWhitelist(textChannel.Id, ref whitelistMatch);
            if (whitelistMatch == null)
            {
                CheckWhitelist(categoryID, ref whitelistMatch);
            }
            if (whitelistMatch == null)
            {
                return;
            }

            //Backup the messages
            SetChannelName(textChannel.Id, textChannel.Name, categoryID);
            BackupMessage(textChannel, message, whitelistMatch);
            channelRead[textChannel.Id] = message.Id;

            return;
        }

        private void SetChannelRead(ulong channelID, ulong messageID)
        {
            channelRead[channelID] = messageID;
            SaveChannelRead();
        }

        private ulong GetChannelRead(ulong channelID)
        {
            if (channelRead.TryGetValue(channelID, out ulong messageID))
            {
                return messageID;
            }
            return 0;
        }

        private void LoadWhitelist()
        {
            string whitelistString = DataStore.Load("BackupWhitelist");
            if (whitelistString == null)
            {
                return;
            }
            whitelistObjects.Clear();
            using (StringReader sr = new StringReader(whitelistString))
            {
                string currentLine = null;
                while ((currentLine = sr.ReadLine()) != null)
                {
                    whitelistObjects.Add(currentLine);
                }
            }
        }

        private void SaveWhitelist()
        {
            StringBuilder sb = new StringBuilder();
            foreach (string writeLine in whitelistObjects)
            {
                sb.AppendLine(writeLine);
            }
            DataStore.Save("BackupWhitelist", sb.ToString());
        }

        private void LoadChannelRead()
        {
            string channelReadString = DataStore.Load("BackupChannelRead");
            if (channelReadString == null)
            {
                return;
            }
            using (StringReader sr = new StringReader(channelReadString))
            {
                channelRead.Clear();
                string currentLine = null;
                while ((currentLine = sr.ReadLine()) != null)
                {
                    currentLine = currentLine.Trim();
                    string channelIDString = currentLine.Substring(0, currentLine.IndexOf("="));
                    string messageIDString = currentLine.Substring(currentLine.IndexOf("=") + 1);
                    if (ulong.TryParse(channelIDString, out ulong channelID) && ulong.TryParse(messageIDString, out ulong messageID))
                    {
                        channelRead[channelID] = messageID;
                    }
                    else
                    {
                        Console.WriteLine("Error reading");
                    }
                }
            }
            Log(LogSeverity.Debug, "Loaded channel read positions");
        }

        private void SaveChannelRead()
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<ulong, ulong> kvp in channelRead)
            {
                sb.AppendLine($"{kvp.Key}={kvp.Value}");
            }
            DataStore.Save("BackupChannelRead", sb.ToString());
        }

        private void LoadChannelNames()
        {
            string channelNamesString = DataStore.Load("BackupChannelNames");
            if (channelNamesString == null)
            {
                return;
            }
            using (StringReader sr = new StringReader(channelNamesString))
            {
                channelNames.Clear();
                string currentLine = null;
                while ((currentLine = sr.ReadLine()) != null)
                {
                    currentLine = currentLine.Trim();
                    int equalSplit = currentLine.IndexOf("=");
                    int commaSplit = currentLine.IndexOf(",");
                    ChannelInfo ci = new ChannelInfo();
                    string channelIDString = currentLine.Substring(0, equalSplit);
                    string categoryIDString = currentLine.Substring(commaSplit + 1);
                    ci.channelName = currentLine.Substring(equalSplit + 1, commaSplit - equalSplit - 1);
                    ci.channelID = ulong.Parse(channelIDString);
                    ci.categoryID = ulong.Parse(categoryIDString);
                    channelNames[ci.channelID] = ci;
                }
            }
            Log(LogSeverity.Debug, "Loaded channel names");
        }

        private void SaveChannelNames()
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<ulong, ChannelInfo> kvp in channelNames)
            {
                sb.AppendLine($"{kvp.Key}={kvp.Value.channelName},{kvp.Value.categoryID}");
            }
            DataStore.Save("BackupChannelNames", sb.ToString());
        }

        private void SetChannelName(ulong channelID, string channelName, ulong categoryID)
        {
            ChannelInfo ci = null;
            bool save = false;
            if (channelNames.TryGetValue(channelID, out ci))
            {
                if (ci.channelName != channelName && ci.categoryID != categoryID)
                {
                    save = true;
                }
            }
            else
            {
                ci = new ChannelInfo();
                channelNames[channelID] = ci;
                save = true;
            }
            if (save)
            {
                ci.channelID = channelID;
                ci.channelName = channelName;
                ci.categoryID = categoryID;
                SaveChannelNames();
            }
        }

        private ulong GetCategory(SocketTextChannel textChannel)
        {
            if (channelCategoryCache.ContainsKey(textChannel.Id))
            {
                return channelCategoryCache[textChannel.Id];
            }
            ulong retVal = 0;
            foreach (SocketCategoryChannel scc in textChannel.Guild.CategoryChannels)
            {
                foreach (SocketChannel sc in scc.Channels)
                {
                    channelCategoryCache[sc.Id] = scc.Id;
                    if (textChannel.Id == sc.Id)
                    {
                        retVal = scc.Id;
                    }
                }
            }
            return retVal;
        }

        private bool CheckWhitelist(ulong id)
        {
            string match = null;
            return CheckWhitelist(id, ref match);
        }

        private bool CheckWhitelist(ulong id, ref string match)
        {
            foreach (string checkKey in whitelistObjects)
            {
                if (_whitelist.ObjectOK(checkKey, id))
                {
                    match = checkKey;
                    return true;
                }
            }
            return false;
        }

        private void Log(LogSeverity severity, string text)
        {
            LogMessage logMessage = new LogMessage(severity, "Backup", text);
            Program.LogAsync(logMessage);
        }
    }
}
